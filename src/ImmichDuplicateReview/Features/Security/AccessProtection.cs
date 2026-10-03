using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ImmichDuplicateReview.Features.Security;

public sealed record AccessProtectionOptions(string Username, string Password)
{
    public static AccessProtectionOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var username = configuration["HELPER_USERNAME"] ?? "review";
        var password = configuration["HELPER_PASSWORD"] ?? string.Empty;

        if (!environment.IsEnvironment("Testing") &&
            (password.Length < 12 || password.Equals("replace-with-a-strong-password", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("HELPER_PASSWORD must be set to at least 12 characters.");
        }

        return new AccessProtectionOptions(username, password);
    }
}

public sealed class AccessProtectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AccessProtectionOptions options, IHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing") || IsHealthProbe(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (!HasValidCredentials(context.Request, options))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Immich duplicate review\", charset=\"UTF-8\"";
            return;
        }

        if (IsUnsafeCrossSiteRequest(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool IsHealthProbe(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/ready", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidCredentials(HttpRequest request, AccessProtectionOptions options)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            var separator = decoded.IndexOf(':');
            if (separator < 0) return false;

            return FixedTimeEquals(decoded[..separator], options.Username) &
                   FixedTimeEquals(decoded[(separator + 1)..], options.Password);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private static bool IsUnsafeCrossSiteRequest(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        {
            return false;
        }

        if (request.Headers["Sec-Fetch-Site"].Equals("cross-site")) return true;
        if (!request.Headers.TryGetValue("Origin", out var origin)) return false;

        return !Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var originUri) ||
               !originUri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase) ||
               !originUri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
