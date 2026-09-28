using System.Net;
using ImmichDuplicateReview.Features.Batches;
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
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DuplicateGroup>>([]);
        public Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromResult(new PreviewContent([1, 2, 3], "image/jpeg"));
        public Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
