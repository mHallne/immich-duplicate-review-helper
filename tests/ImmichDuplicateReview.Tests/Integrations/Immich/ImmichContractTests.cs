using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Tests.Integrations.Immich;

public sealed class ImmichContractFactAttribute : FactAttribute
{
    public ImmichContractFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_IMMICH_CONTRACT_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set RUN_IMMICH_CONTRACT_TESTS=1 to start the pinned Immich Testcontainers stack.";
    }
}

public sealed class ImmichContractTests
{
    private const string ServerService = "immich-server";
    private const ushort ServerPort = 2283;

    [ImmichContractFact]
    [Trait("Category", "ImmichContract")]
    public async Task Pinned_Immich_supports_the_helpers_authenticated_read_contract()
    {
        var composePath = Path.Combine(AppContext.BaseDirectory, "Integrations", "Immich", "contract.compose.yml");
        var serverReady = Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(ServerPort).ForPath("/api/server/ping"));
        await using var environment = new ComposeBuilder("docker:29.8.1-cli")
            .WithComposeFile(composePath)
            .WithProjectNamePrefix("immich-contract")
            .WithExposedService(ServerService, ServerPort, serverReady)
            .Build();
        await environment.StartAsync();

        var host = environment.GetServiceHost(ServerService, ServerPort);
        var port = environment.GetServicePort(ServerService, ServerPort);
        var baseUrl = new UriBuilder(Uri.UriSchemeHttp, host, port).Uri;
        using var bootstrapClient = new HttpClient { BaseAddress = new Uri(baseUrl, "api/") };
        var apiKey = await CreateApiKeyAsync(bootstrapClient);
        var client = new ImmichClient(new HttpClient(), new ImmichOptions(baseUrl, apiKey));

        Assert.Empty(await client.GetDuplicateGroupsAsync());
        await client.EnsureTrashEnabledAsync();
    }

    private static async Task<string> CreateApiKeyAsync(HttpClient client)
    {
        using var signUp = await client.PostAsJsonAsync("auth/admin-sign-up", new
        {
            email = "contract@example.invalid",
            password = "contract-test-password",
            name = "Contract Test"
        });
        signUp.EnsureSuccessStatusCode();

        using var login = await client.PostAsJsonAsync("auth/login", new
        {
            email = "contract@example.invalid",
            password = "contract-test-password"
        });
        login.EnsureSuccessStatusCode();
        using var loginJson = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
        var token = loginJson.RootElement.GetProperty("accessToken").GetString()
            ?? throw new InvalidDataException("Immich login did not return an access token.");

        using var createKey = new HttpRequestMessage(HttpMethod.Post, "api-keys")
        {
            Content = JsonContent.Create(new
            {
                name = "Duplicate review contract",
                permissions = new[] { "duplicate.read", "userConfig.read", "user.read" }
            })
        };
        createKey.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var keyResponse = await client.SendAsync(createKey);
        keyResponse.EnsureSuccessStatusCode();
        using var keyJson = await JsonDocument.ParseAsync(await keyResponse.Content.ReadAsStreamAsync());
        return keyJson.RootElement.GetProperty("secret").GetString()
            ?? throw new InvalidDataException("Immich did not return an API key secret.");
    }
}
