using ImmichDuplicateReview.Features.Health;
using ImmichDuplicateReview.Features.Previews;
using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Integrations.Immich;

var builder = WebApplication.CreateBuilder(args);
var immichUrl = builder.Configuration["IMMICH_URL"] ?? "http://localhost:2283";
var apiKey = builder.Configuration["IMMICH_API_KEY"] ?? string.Empty;
var dataPath = builder.Configuration["DATA_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataPath);

builder.Services.AddSingleton(new ImmichOptions(new Uri(immichUrl), apiKey));
builder.Services.AddHttpClient<IImmichClient, ImmichClient>();
builder.Services.AddSingleton(new ReviewStore(Path.Combine(dataPath, "reviews.db")));

var app = builder.Build();
await app.Services.GetRequiredService<ReviewStore>().InitializeAsync();

app.MapPreviewEndpoints();
app.MapHealthEndpoints();
app.MapBatchEndpoints();
app.MapReviewEndpoints();
app.MapReviewUiEndpoints();

app.Run();

public partial class Program;
