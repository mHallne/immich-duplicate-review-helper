using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ImmichDuplicateReview.Tests.Features.Security;

public sealed class AccessProtectionTests : IClassFixture<AccessProtectionTests.Factory>
{
    private readonly Factory _factory;

    public AccessProtectionTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Review_ui_requires_valid_basic_credentials()
    {
        var anonymous = await _factory.CreateClient().GetAsync("/");
        var invalid = await SendAuthenticatedAsync(HttpMethod.Get, "/", "wrong-password");
        var valid = await SendAuthenticatedAsync(HttpMethod.Get, "/", Factory.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Basic", anonymous.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task Cross_site_mutating_request_is_rejected()
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/batches", Factory.Password);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Content = JsonContent.Create(new { batchSize = 100 });

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_probes_remain_public()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpMethod method, string path, string password)
    {
        using var request = CreateAuthenticatedRequest(method, path, password);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path, string password)
    {
        var request = new HttpRequestMessage(method, path);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"review:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return request;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Password = "a-long-test-password";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HELPER_PASSWORD", Password);
        }
    }
}
