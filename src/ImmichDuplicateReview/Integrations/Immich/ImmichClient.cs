using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmichDuplicateReview.Features.Batches;

namespace ImmichDuplicateReview.Integrations.Immich;

public sealed record ImmichOptions(Uri BaseUrl, string ApiKey);

public enum ImmichFailureKind { Unavailable, AuthenticationFailed, PermissionDenied, EndpointNotFound, TrashDisabled, OperationRejected }

public class ImmichApiException : Exception
{
    public ImmichApiException(string message, Exception? innerException = null)
        : this(message, ImmichFailureKind.Unavailable, "Immich is unavailable", "The helper could not complete the request against Immich. Retry when Immich is available.", null, null, innerException) { }

    protected ImmichApiException(
        string message,
        ImmichFailureKind kind,
        string userTitle,
        string userDetail,
        HttpStatusCode? statusCode,
        string? requiredPermission,
        Exception? innerException = null) : base(message, innerException)
    {
        Kind = kind;
        UserTitle = userTitle;
        UserDetail = userDetail;
        StatusCode = statusCode;
        RequiredPermission = requiredPermission;
    }

    public ImmichFailureKind Kind { get; }
    public string UserTitle { get; }
    public string UserDetail { get; }
    public HttpStatusCode? StatusCode { get; }
    public string? RequiredPermission { get; }

    public static ImmichApiException FromResponse(string operation, HttpStatusCode statusCode, string requiredPermission)
    {
        var (kind, title, detail) = statusCode switch
        {
            HttpStatusCode.Unauthorized => (
                ImmichFailureKind.AuthenticationFailed,
                "Immich API key was rejected",
                "Replace IMMICH_API_KEY with a valid key and restart the helper."),
            HttpStatusCode.Forbidden => (
                ImmichFailureKind.PermissionDenied,
                "Immich API permission is missing",
                $"Grant {requiredPermission} to the helper's Immich API key, then retry."),
            HttpStatusCode.NotFound => (
                ImmichFailureKind.EndpointNotFound,
                "Immich API endpoint was not found",
                "Check that IMMICH_URL points to a compatible Immich server and that the helper supports its version."),
            _ => (
                ImmichFailureKind.Unavailable,
                "Immich is unavailable",
                "The helper could not complete the request against Immich. Retry when Immich is available.")
        };
        return new($"Immich returned HTTP {(int)statusCode} while attempting to {operation}.", kind, title, detail, statusCode, requiredPermission);
    }

    public static ImmichApiException TrashDisabled() => new(
        "Immich Trash is disabled; refusing an operation that could permanently delete assets.",
        ImmichFailureKind.TrashDisabled,
        "Immich Trash is disabled",
        "Enable Trash in Immich before confirming any review that would trash assets.",
        null,
        null);

    public static ImmichApiException OperationRejected(string operation) => new(
        $"Immich rejected the {operation} operation.",
        ImmichFailureKind.OperationRejected,
        "Immich rejected the operation",
        $"No local review state was lost. Check the group in Immich, then retry {operation}.",
        null,
        null);
}

public sealed class AmbiguousResolveException(string groupId) : ImmichApiException(
    $"Immich no longer reports duplicate group '{groupId}' after a lost resolve response. Manually verify the result in Immich; the helper will not repeat the destructive request or mark it reviewed.",
    ImmichFailureKind.OperationRejected,
    "Resolve result needs verification",
    "The earlier request may have succeeded. Verify this duplicate group directly in Immich before acknowledging the result.",
    null,
    null);

public sealed class ImmichClient(HttpClient httpClient, ImmichOptions options) : IImmichClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "duplicates");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "load duplicate groups", "duplicate.read");
        var dtos = await response.Content.ReadFromJsonAsync<DuplicateGroupDto[]>(JsonOptions, cancellationToken) ?? [];
        return dtos.Select(Map).ToArray();
    }

    public async Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"assets/{Uri.EscapeDataString(assetId)}/thumbnail?size=preview");
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            await EnsureSuccessAsync(response, "load an asset preview", "asset.view");
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new PreviewContent(
                stream,
                response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                response.Content.Headers.ContentLength,
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetAlbumNamesAsync(string assetId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"albums?assetId={Uri.EscapeDataString(assetId)}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "load album membership", "album.read");
        var albums = await response.Content.ReadFromJsonAsync<AlbumDto[]>(JsonOptions, cancellationToken) ?? [];
        return albums.Select(album => album.AlbumName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default)
    {
        var payload = new ResolveRequest([new(groupId, keepAssetIds, trashAssetIds)]);
        using var request = CreateRequest(HttpMethod.Post, "duplicates/resolve");
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "resolve duplicate assets", "duplicate.delete and asset.delete");
        var results = await response.Content.ReadFromJsonAsync<ResolveResult[]>(JsonOptions, cancellationToken) ?? [];
        var result = results.SingleOrDefault(x => x.Id == groupId);
        if (result is null || !result.Success)
            throw ImmichApiException.OperationRejected("duplicate resolution");
    }

    public async Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "config");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "read Trash configuration", "userConfig.read");
        var config = await response.Content.ReadFromJsonAsync<UserConfigDto>(JsonOptions, cancellationToken);
        if (config?.Trash.Enabled != true)
            throw ImmichApiException.TrashDisabled();
    }

    public async Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
    {
        if (assetIds.Count < 2) throw new ArgumentException("A stack requires at least two assets.", nameof(assetIds));
        var primaryAssetId = assetIds[0];
        using (var search = CreateRequest(HttpMethod.Get, $"stacks?primaryAssetId={Uri.EscapeDataString(primaryAssetId)}"))
        using (var response = await httpClient.SendAsync(search, cancellationToken))
        {
            await EnsureSuccessAsync(response, "find an existing stack", "stack.read");
            var stacks = await response.Content.ReadFromJsonAsync<StackDto[]>(JsonOptions, cancellationToken) ?? [];
            var desired = assetIds.ToHashSet(StringComparer.Ordinal);
            if (stacks.Any(stack => desired.IsSubsetOf(stack.Assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal)))) return;
        }

        using var create = CreateRequest(HttpMethod.Post, "stacks");
        create.Content = JsonContent.Create(new StackCreateRequest(assetIds), options: JsonOptions);
        using var createResponse = await httpClient.SendAsync(create, cancellationToken);
        await EnsureSuccessAsync(createResponse, "create a stack", "stack.create");
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await CanAccessAsync("users/me", cancellationToken)) return false;
            if (!await CanAccessAsync("duplicates", cancellationToken)) return false;
            await EnsureTrashEnabledAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or ImmichApiException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> CanAccessAsync(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var baseUrl = options.BaseUrl.ToString().TrimEnd('/') + "/api/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        request.Headers.Add("x-api-key", options.ApiKey);
        return request;
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, string operation, string requiredPermission)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        throw ImmichApiException.FromResponse(operation, response.StatusCode, requiredPermission);
    }

    private static DuplicateGroup Map(DuplicateGroupDto group) => new(group.DuplicateId,
        group.Assets.Select(asset =>
        {
            var exif = asset.ExifInfo;
            var camera = string.IsNullOrWhiteSpace(exif?.Model) ? exif?.Make : exif.Model;
            return new DuplicateAsset(
                asset.Id,
                asset.OriginalFileName,
                asset.FileCreatedAt,
                asset.OriginalPath,
                exif?.FileSizeInByte,
                exif?.ExifImageWidth,
                exif?.ExifImageHeight,
                Path.GetExtension(asset.OriginalFileName).TrimStart('.').ToUpperInvariant(),
                camera,
                exif is not null,
                exif?.Latitude is not null && exif.Longitude is not null,
                asset.IsFavorite,
                exif?.Rating);
        }).ToArray());

    private sealed record DuplicateGroupDto(string DuplicateId, AssetDto[] Assets);
    private sealed record AssetDto(string Id, string OriginalFileName, string OriginalPath, DateTimeOffset FileCreatedAt, bool IsFavorite, ExifDto? ExifInfo);
    private sealed record ExifDto(long? FileSizeInByte, int? ExifImageWidth, int? ExifImageHeight, string? Make, string? Model, double? Latitude, double? Longitude, int? Rating);
    private sealed record ResolveRequest(IReadOnlyList<ResolveGroup> Groups);
    private sealed record ResolveGroup(string DuplicateId, IReadOnlyCollection<string> KeepAssetIds, IReadOnlyCollection<string> TrashAssetIds);
    private sealed record ResolveResult(string Id, bool Success, string? Error);
    private sealed record UserConfigDto(TrashConfigDto Trash);
    private sealed record TrashConfigDto(bool Enabled);
    private sealed record StackCreateRequest(IReadOnlyList<string> AssetIds);
    private sealed record StackDto(string Id, string PrimaryAssetId, AssetReferenceDto[] Assets);
    private sealed record AssetReferenceDto(string Id);
    private sealed record AlbumDto(string Id, string AlbumName);
}
