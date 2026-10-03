using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Integrations.Immich;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImmichDuplicateReview.Tests.Features.Previews;

public sealed class PreviewEndpointTests : IClassFixture<PreviewEndpointTests.Factory>
{
    private readonly HttpClient _client;

    public PreviewEndpointTests(Factory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Streams_preview_without_exposing_api_key()
    {
        var response = await _client.GetAsync("/api/assets/asset-1/preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("super-secret", response.Headers.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_is_live_and_ready_requires_Immich()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/ready")).StatusCode);
    }

    [Fact]
    public async Task Immich_failure_returns_safe_gateway_error_and_unready_status()
    {
        await using var factory = new DependencyFactory(new UnavailableImmichClient());
        var client = factory.CreateClient();

        var preview = await client.GetAsync("/api/assets/asset-1/preview");
        var ready = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.BadGateway, preview.StatusCode);
        Assert.Equal("application/problem+json", preview.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Immich is unavailable", await preview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", await preview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Missing_Immich_permission_returns_actionable_safe_problem()
    {
        await using var factory = new DependencyFactory(new PermissionDeniedImmichClient());

        var response = await factory.CreateClient().GetAsync("/api/assets/asset-1/preview");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Immich API permission is missing", problem, StringComparison.Ordinal);
        Assert.Contains("asset.view", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive upstream body", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sqlite_failure_keeps_liveness_up_but_readiness_down()
    {
        var invalidDatabasePath = Path.Combine(Path.GetTempPath(), $"sqlite-directory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidDatabasePath);
        try
        {
            await using var factory = new DependencyFactory(new FakeImmichClient(), new ReviewStore(invalidDatabasePath));
            var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        }
        finally
        {
            Directory.Delete(invalidDatabasePath);
        }
    }

    [Fact]
    public async Task Configured_default_batch_size_is_selected_on_start_page()
    {
        await using var factory = new ConfiguredFactory("20");
        var html = await factory.CreateClient().GetStringAsync("/");
        Assert.Contains("<option selected>20</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option>10</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option>50</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option>100</option>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<option>250</option>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<option>500</option>", html, StringComparison.Ordinal);
        Assert.Contains("value=\"largest-potential-saving\"", html, StringComparison.Ordinal);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IImmichClient>();
                services.AddSingleton<IImmichClient, FakeImmichClient>();
            });
        }
    }

    private sealed class FakeImmichClient : IImmichClient
    {
        public Task<IReadOnlyList<string>> GetAlbumNamesAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DuplicateGroup>>([]);
        public Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromResult(new PreviewContent([1, 2, 3], "image/jpeg"));
        public Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class UnavailableImmichClient : IImmichClient
    {
        public Task<IReadOnlyList<string>> GetAlbumNamesAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<string>>(new ImmichApiException("unavailable"));
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<DuplicateGroup>>(new ImmichApiException("upstream included super-secret"));
        public virtual Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromException<PreviewContent>(new ImmichApiException("upstream included super-secret"));
        public Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default) => Task.FromException(new ImmichApiException("unavailable"));
        public Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => Task.FromException(new ImmichApiException("unavailable"));
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default) => Task.FromException(new ImmichApiException("unavailable"));
    }

    private sealed class PermissionDeniedImmichClient : UnavailableImmichClient
    {
        public override Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) =>
            Task.FromException<PreviewContent>(ImmichApiException.FromResponse(
                "load an asset preview",
                HttpStatusCode.Forbidden,
                "asset.view"));
    }

    private sealed class DependencyFactory(IImmichClient immich, ReviewStore? store = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HELPER_PASSWORD", "a-long-test-password");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IImmichClient>();
                services.AddSingleton(immich);
                if (store is not null)
                {
                    services.RemoveAll<ReviewStore>();
                    services.AddSingleton(store);
                }
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("review:a-long-test-password"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    private sealed class ConfiguredFactory(string batchSize) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DEFAULT_BATCH_SIZE", batchSize);
        }
    }
}
