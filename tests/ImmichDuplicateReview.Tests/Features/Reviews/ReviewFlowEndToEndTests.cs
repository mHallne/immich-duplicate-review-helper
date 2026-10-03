using System.Net.Http.Json;
using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Integrations.Immich;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ImmichDuplicateReview.Tests.Features.Reviews;

public sealed class ReviewFlowEndToEndTests : IAsyncDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}");

    [Fact]
    public async Task Confirm_skip_restart_resumes_third_group_and_does_not_repeat_trash()
    {
        var immich = new ScenarioImmichClient();
        await using (var first = new Factory(_dataPath, immich))
        {
            var client = first.CreateClient();
            var batch = await client.PostAsJsonAsync("/api/batches", new { batchSize = 100 });
            batch.EnsureSuccessStatusCode();
            Assert.Contains("g1", await batch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var reviewPage = await client.GetStringAsync("/review");
            Assert.Contains("/api/assets/g1-a/preview", reviewPage, StringComparison.Ordinal);
            Assert.Contains("<dt>Albums</dt><dd>None</dd>", reviewPage, StringComparison.Ordinal);
            Assert.Contains("<dt>Format</dt><dd>JPEG</dd>", reviewPage, StringComparison.Ordinal);
            Assert.Contains("Largest file", reviewPage, StringComparison.Ordinal);
            Assert.Contains("Highest resolution", reviewPage, StringComparison.Ordinal);
            Assert.Contains("role=\"status\" aria-live=\"polite\"", reviewPage, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"Full-screen preview of g1-0.jpg\"", reviewPage, StringComparison.Ordinal);
            Assert.Contains("Keyboard shortcuts", reviewPage, StringComparison.Ordinal);
            Assert.Contains("INPUT", reviewPage, StringComparison.Ordinal);
            Assert.Contains(first.Logs, entry => entry.Contains("Group g1 loaded", StringComparison.Ordinal));

            var nextPage = await client.PostAsync("/review/navigation/next", null);
            nextPage.EnsureSuccessStatusCode();
            Assert.Contains("g2-0.jpg", await nextPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var previousPage = await client.PostAsync("/review/navigation/previous", null);
            previousPage.EnsureSuccessStatusCode();
            Assert.Contains("g1-0.jpg", await previousPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var proposal = await client.PostAsync("/review/g1/propose", new FormUrlEncodedContent(
            [
                new("keepAssetIds", "g1-a"),
                new("trashAssetIds", "g1-b")
            ]));
            proposal.EnsureSuccessStatusCode();
            var proposalHtml = await proposal.Content.ReadAsStringAsync();
            Assert.Contains("Keep", proposalHtml, StringComparison.Ordinal);
            Assert.Contains("g1-0.jpg", proposalHtml, StringComparison.Ordinal);
            Assert.Contains("Trash", proposalHtml, StringComparison.Ordinal);
            Assert.Contains("g1-1.jpg", proposalHtml, StringComparison.Ordinal);
            Assert.Contains("Confirm in Immich", proposalHtml, StringComparison.Ordinal);

            var confirm = await client.PostAsJsonAsync("/api/review/g1/confirm", new { keepAssetIds = new[] { "g1-a" }, trashAssetIds = new[] { "g1-b" }, stackAssetIds = Array.Empty<string>() });
            confirm.EnsureSuccessStatusCode();
            (await client.PostAsync("/api/review/g2/skip", null)).EnsureSuccessStatusCode();
        }

        await using (var restarted = new Factory(_dataPath, immich))
        {
            var client = restarted.CreateClient();
            var next = await client.GetStringAsync("/api/review/next");
            Assert.Contains("g3", next, StringComparison.Ordinal);
            Assert.DoesNotContain("g1", next, StringComparison.Ordinal);
            var store = restarted.Services.GetRequiredService<ReviewStore>();
            Assert.Equal(ReviewStatus.Reviewed, await store.GetStatusAsync("g1"));
            Assert.Equal(ReviewStatus.Skipped, await store.GetStatusAsync("g2"));
        }

        Assert.Equal(1, immich.ResolveCalls);
    }

    [Fact]
    public async Task Failed_review_page_restores_saved_choices_and_requires_confirmation_to_retry()
    {
        var immich = new ScenarioImmichClient();
        await using var factory = new Factory(_dataPath, immich);
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/batches", new { batchSize = 100 })).EnsureSuccessStatusCode();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        await store.MarkFailedAsync("g1", "{\"keep\":[\"g1-b\"],\"trash\":[\"g1-a\"],\"stack\":[\"g1-b\"]}", "ImmichApiException");

        var page = await client.GetStringAsync("/review");

        Assert.Contains("Previous attempt failed", page, StringComparison.Ordinal);
        Assert.Contains("Review saved decision", page, StringComparison.Ordinal);
        Assert.Contains("name=\"keepAssetIds\" value=\"g1-b\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"trashAssetIds\" value=\"g1-a\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ImmichApiException", page, StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, true);
        return ValueTask.CompletedTask;
    }

    private sealed class Factory(string dataPath, IImmichClient immich) : WebApplicationFactory<Program>
    {
        public List<string> Logs { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_PATH"] = dataPath }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IImmichClient>();
                services.RemoveAll<ReviewStore>();
                services.AddSingleton(immich);
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Logs));
                Directory.CreateDirectory(dataPath);
                services.AddSingleton(new ReviewStore(Path.Combine(dataPath, "reviews.db")));
            });
        }
    }

    private sealed class CapturingLoggerProvider(List<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add(formatter(state, exception));
        }
    }

    private sealed class ScenarioImmichClient : IImmichClient
    {
        public Task<IReadOnlyList<string>> GetAlbumNamesAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public int ResolveCalls { get; private set; }
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DuplicateGroup>>(
        [
            Group("g1", 1, 2), Group("g2", 2, 3), Group("g3", 3, 2)
        ]);
        public Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromResult(new PreviewContent([1], "image/jpeg"));
        public Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            Assert.Equal("g1", groupId);
            Assert.Equal(["g1-b"], trashAssetIds);
            return Task.CompletedTask;
        }

        private static DuplicateGroup Group(string id, int day, int count) => new(id,
            Enumerable.Range(0, count).Select(i => new DuplicateAsset(
                $"{id}-{(char)('a' + i)}", $"{id}-{i}.jpg", DateTimeOffset.UnixEpoch.AddDays(day),
                FileSize: 1_000 - i, Width: 4_000 - i, Height: 3_000 - i, Format: "JPEG", HasExif: true)).ToArray());
    }
}
