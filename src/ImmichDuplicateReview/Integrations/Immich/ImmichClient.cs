using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmichDuplicateReview.Features.Batches;

namespace ImmichDuplicateReview.Integrations.Immich;

public sealed record ImmichOptions(Uri BaseUrl, string ApiKey);

public sealed class ImmichApiException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class ImmichClient(HttpClient httpClient, ImmichOptions options) : IImmichClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "duplicates");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var dtos = await response.Content.ReadFromJsonAsync<DuplicateGroupDto[]>(JsonOptions, cancellationToken) ?? [];
        return dtos.Select(Map).ToArray();
    }

    public async Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"assets/{Uri.EscapeDataString(assetId)}/thumbnail?size=preview");
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
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
        await EnsureSuccessAsync(response, cancellationToken);
        var albums = await response.Content.ReadFromJsonAsync<AlbumDto[]>(JsonOptions, cancellationToken) ?? [];
        return albums.Select(album => album.AlbumName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default)
    {
        var payload = new ResolveRequest([new(groupId, keepAssetIds, trashAssetIds)]);
        using var request = CreateRequest(HttpMethod.Post, "duplicates/resolve");
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var results = await response.Content.ReadFromJsonAsync<ResolveResult[]>(JsonOptions, cancellationToken) ?? [];
        var result = results.SingleOrDefault(x => x.Id == groupId);
        if (result is null || !result.Success)
            throw new ImmichApiException($"Immich failed to resolve duplicate group '{groupId}': {result?.Error ?? "missing result"}.");
    }

    public async Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "config");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var config = await response.Content.ReadFromJsonAsync<UserConfigDto>(JsonOptions, cancellationToken);
        if (config?.Trash.Enabled != true)
            throw new ImmichApiException("Immich Trash is disabled; refusing an operation that could permanently delete assets.");
    }

    public async Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
    {
        if (assetIds.Count < 2) throw new ArgumentException("A stack requires at least two assets.", nameof(assetIds));
        var primaryAssetId = assetIds[0];
        using (var search = CreateRequest(HttpMethod.Get, $"stacks?primaryAssetId={Uri.EscapeDataString(primaryAssetId)}"))
        using (var response = await httpClient.SendAsync(search, cancellationToken))
        {
            await EnsureSuccessAsync(response, cancellationToken);
            var stacks = await response.Content.ReadFromJsonAsync<StackDto[]>(JsonOptions, cancellationToken) ?? [];
            var desired = assetIds.ToHashSet(StringComparer.Ordinal);
            if (stacks.Any(stack => desired.IsSubsetOf(stack.Assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal)))) return;
        }

        using var create = CreateRequest(HttpMethod.Post, "stacks");
        create.Content = JsonContent.Create(new StackCreateRequest(assetIds), options: JsonOptions);
        using var createResponse = await httpClient.SendAsync(create, cancellationToken);
        await EnsureSuccessAsync(createResponse, cancellationToken);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "users/me");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var baseUrl = options.BaseUrl.ToString().TrimEnd('/') + "/api/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        request.Headers.Add("x-api-key", options.ApiKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ImmichApiException($"Immich returned {(int)response.StatusCode}: {body}");
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
