using System.Net;
using System.Text;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Tests.Integrations.Immich;

public sealed class ImmichClientTests
{
    [Fact]
    public async Task Retrieves_and_maps_duplicate_groups_with_server_side_api_key()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"duplicateId":"group-1","assets":[
              {"id":"a","originalFileName":"IMG.JPG","originalPath":"/photos/IMG.JPG","fileCreatedAt":"2020-01-02T00:00:00Z","isFavorite":true,
               "exifInfo":{"fileSizeInByte":42,"exifImageWidth":4000,"exifImageHeight":3000,"make":"Canon","model":"R5","latitude":1.0,"longitude":2.0,"rating":5}},
              {"id":"b","originalFileName":"COPY.JPG","originalPath":"/photos/COPY.JPG","fileCreatedAt":"2020-01-01T00:00:00Z","isFavorite":false,
               "exifInfo":{"fileSizeInByte":21,"exifImageWidth":2000,"exifImageHeight":1500}}
            ]}]
            """);
        var client = Create(handler);

        var groups = await client.GetDuplicateGroupsAsync();

        var group = Assert.Single(groups);
        Assert.Equal("group-1", group.Id);
        Assert.Equal(DateTimeOffset.Parse("2020-01-01Z"), group.SortDate);
        Assert.True(group.Assets[0].IsFavorite);
        Assert.True(group.Assets[0].HasGps);
        Assert.Equal(12_000_000, group.Assets[0].PixelCount);
        Assert.Equal("R5", group.Assets[0].Camera);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("https://immich.example/api/duplicates", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("secret", handler.LastRequest.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task Preview_returns_bytes_and_content_type()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "image-bytes", "image/jpeg");

        var preview = await Create(handler).GetPreviewAsync("asset-1");

        Assert.Equal("image/jpeg", preview.ContentType);
        Assert.Equal("image-bytes", Encoding.UTF8.GetString(preview.Bytes));
        Assert.Equal("https://immich.example/api/assets/asset-1/thumbnail?size=preview", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Resolve_sends_exact_keep_and_trash_operation()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[{\"id\":\"group-1\",\"success\":true}]");

        await Create(handler).ResolveAsync("group-1", ["a"], ["b", "c"]);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("{\"groups\":[{\"duplicateId\":\"group-1\",\"keepAssetIds\":[\"a\"],\"trashAssetIds\":[\"b\",\"c\"]}]}", handler.LastBody);
    }

    [Fact]
    public async Task Failed_resolve_throws_and_is_not_reported_as_success()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[{\"id\":\"group-1\",\"success\":false,\"error\":\"no_permission\"}]");
        await Assert.ThrowsAsync<ImmichApiException>(() => Create(handler).ResolveAsync("group-1", ["a"], ["b"]));
    }

    private static ImmichClient Create(StubHandler handler) =>
        new(new HttpClient(handler), new ImmichOptions(new Uri("https://immich.example"), "secret"));

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType = "application/json") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequest = request;
            return new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        }
    }
}
