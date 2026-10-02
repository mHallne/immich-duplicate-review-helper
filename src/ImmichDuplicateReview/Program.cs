using ImmichDuplicateReview.Features.Health;
using ImmichDuplicateReview.Features.Previews;
using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Integrations.Immich;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var immichUrl = builder.Configuration["IMMICH_URL"] ?? "http://localhost:2283";
var apiKey = builder.Configuration["IMMICH_API_KEY"] ?? string.Empty;
var dataPath = builder.Configuration["DATA_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataPath);

builder.Services.AddSingleton(new ImmichOptions(new Uri(immichUrl), apiKey));
builder.Services.AddSingleton(BatchOptions.FromConfiguration(builder.Configuration["DEFAULT_BATCH_SIZE"]));
builder.Services.AddHttpClient<IImmichClient, ImmichClient>();
builder.Services.AddTransient<AlbumMetadataEnricher>();
builder.Services.AddSingleton(new ReviewStore(Path.Combine(dataPath, "reviews.db")));

var app = builder.Build();
try
{
    await app.Services.GetRequiredService<ReviewStore>().InitializeAsync();
}
catch (SqliteException exception)
{
    app.Logger.LogError("SQLite initialization failed with code {SqliteErrorCode}; readiness will remain unavailable", exception.SqliteErrorCode);
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var dependencyFailure = exception is ImmichApiException or HttpRequestException;
    var status = dependencyFailure ? StatusCodes.Status502BadGateway : StatusCodes.Status500InternalServerError;
    app.Logger.LogError(
        "Request failed at {RequestPath} with {FailureType}",
        context.Request.Path,
        exception?.GetType().Name ?? "UnknownError");

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        await Results.Problem(
            statusCode: status,
            title: dependencyFailure ? "Immich is unavailable" : "The request failed",
            detail: dependencyFailure ? "The helper could not complete the request against Immich. Retry when Immich is available." : "An unexpected error occurred.")
            .ExecuteAsync(context);
        return;
    }

    context.Response.StatusCode = status;
    context.Response.ContentType = "text/html; charset=utf-8";
    var title = dependencyFailure ? "Immich is unavailable" : "The request failed";
    await context.Response.WriteAsync($"<!doctype html><html><body><main><h1>{title}</h1><p>Your local review state was preserved. <a href=\"/review\">Retry the review</a>.</p></main></body></html>");
}));

app.MapPreviewEndpoints();
app.MapHealthEndpoints();
app.MapBatchEndpoints();
app.MapReviewEndpoints();
app.MapReviewUiEndpoints();

app.Run();

public partial class Program;
