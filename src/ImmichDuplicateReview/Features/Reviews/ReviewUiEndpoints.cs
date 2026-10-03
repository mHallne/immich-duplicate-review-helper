using System.Net;
using System.Text;
using System.Text.Json;
using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Reviews;

public static class ReviewUiEndpoints
{
    public static IEndpointRouteBuilder MapReviewUiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", (BatchOptions options) => Results.Content(Layout($$"""
            <main><h1>Immich Duplicate Review Helper</h1>
            <p>Review duplicate groups in a small, resumable batch. No action occurs without confirmation.</p>
            <form method="post" action="/review/start">
              <label>Batch size <select name="batchSize">{{RenderBatchSizeOptions(options.DefaultSize)}}</select></label>
              <label>Sort <select name="sortMode">{{RenderSortModeOptions()}}</select></label>
              <button type="submit">Load oldest duplicates</button>
            </form></main>
            """), "text/html"));

        endpoints.MapPost("/review/start", async (HttpRequest request, IImmichClient immich, AlbumMetadataEnricher albumEnricher, ReviewStore store, BatchOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var size = int.TryParse(form["batchSize"], out var parsed) ? parsed : options.DefaultSize;
            var sortMode = SortModes.Parse(form["sortMode"]);
            var groups = await immich.GetDuplicateGroupsAsync(cancellationToken);
            await store.UpsertGroupsAsync(groups, cancellationToken);
            var batch = CreateBatch.Handle(await store.LoadPendingAsync(cancellationToken), size, sortMode);
            var session = await store.CreateOrResumeSessionAsync(size, batch.Groups.Select(group => group.Id).ToArray(), sortMode.ToValue(), cancellationToken);
            var activeGroups = await albumEnricher.EnrichAsync(await store.LoadActiveBatchAsync(cancellationToken), cancellationToken);
            await store.UpsertGroupsAsync(activeGroups, cancellationToken);
            loggerFactory.CreateLogger("Batch").LogInformation("Batch {SessionId} active with {GroupCount} groups and size {BatchSize}", session.Id, activeGroups.Count, session.BatchSize);
            return Results.Redirect("/review");
        }).DisableAntiforgery();

        endpoints.MapGet("/review", async (ReviewStore store, AlbumMetadataEnricher albumEnricher, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var group = await store.LoadCurrentActiveGroupAsync(cancellationToken);
            if (group is null) return Results.Content(Layout("<main><h1>Batch complete</h1><p>No pending groups remain.</p></main>"), "text/html");
            group = (await albumEnricher.EnrichAsync([group], cancellationToken))[0];
            await store.UpsertGroupsAsync([group], cancellationToken);
            var progress = await store.GetActiveProgressAsync(cancellationToken);
            var attempt = group.Status == ReviewStatus.Failed ? await store.LoadReviewAttemptAsync(group.Id, cancellationToken) : null;
            loggerFactory.CreateLogger("Review").LogInformation("Group {GroupId} loaded", group.Id);
            return Results.Content(RenderReview(group, progress, attempt), "text/html");
        });

        endpoints.MapPost("/review/navigation/{direction}", async (string direction, ReviewStore store, CancellationToken cancellationToken) =>
        {
            var offset = direction.ToLowerInvariant() switch
            {
                "previous" => -1,
                "next" => 1,
                _ => 0
            };
            if (offset == 0) return Results.BadRequest();
            await store.MoveActiveCursorAsync(offset, cancellationToken);
            return Results.Redirect("/review");
        }).DisableAntiforgery();

        endpoints.MapPost("/review/{groupId}/propose", async (string groupId, HttpRequest request, ReviewStore store, CancellationToken cancellationToken) =>
        {
            var group = await store.LoadGroupAsync(groupId, cancellationToken);
            if (group is null) return Results.NotFound();
            var form = await request.ReadFormAsync(cancellationToken);
            var decision = ReviewDecision.Create(group, form["keepAssetIds"], form["trashAssetIds"], form["stackAssetIds"]);
            return Results.Content(RenderProposal(group, decision), "text/html");
        }).DisableAntiforgery();

        endpoints.MapPost("/review/{groupId}/confirm", async (string groupId, HttpRequest request, ReviewStore store, ConfirmReview workflow, CancellationToken cancellationToken) =>
        {
            var group = await store.LoadGroupAsync(groupId, cancellationToken);
            if (group is null) return Results.NotFound();
            var form = await request.ReadFormAsync(cancellationToken);
            var decision = ReviewDecision.Create(group, form["keepAssetIds"], form["trashAssetIds"], form["stackAssetIds"]);
            await workflow.HandleAsync(group, decision, cancellationToken);
            return Results.Redirect("/review");
        }).DisableAntiforgery();

        endpoints.MapPost("/review/{groupId}/skip", async (string groupId, ReviewStore store, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            await store.SkipAsync(groupId, null, cancellationToken);
            loggerFactory.CreateLogger("Review").LogInformation("Review skipped for group {GroupId}", groupId);
            return Results.Redirect("/review");
        }).DisableAntiforgery();
        return endpoints;
    }

    private static string RenderReview(DuplicateGroup group, ReviewProgress progress, ReviewAttempt? attempt)
    {
        var actionable = group.Status is ReviewStatus.Pending or ReviewStatus.Failed;
        var largestFile = group.Assets.Where(asset => asset.FileSize is not null).MaxBy(asset => asset.FileSize)?.Id;
        var highestResolution = group.Assets.Where(asset => asset.PixelCount is not null).MaxBy(asset => asset.PixelCount)?.Id;
        var saved = attempt?.DecisionJson is { } json ? JsonSerializer.Deserialize<SavedDecision>(json, JsonOptions) : null;
        var failureNotice = group.Status == ReviewStatus.Failed
            ? "<aside role=\"alert\"><strong>Previous attempt failed.</strong> Your saved decision is restored. Review it and confirm again when Immich is available.</aside>"
            : string.Empty;
        var decisionLabel = group.Status == ReviewStatus.Failed ? "Review saved decision" : "Review proposed changes";
        var decisionAction = actionable
            ? $"<button id=\"review-decision\" form=\"decision\" type=\"submit\"{(saved is null ? " disabled" : string.Empty)}>{decisionLabel}</button>"
            : string.Empty;
        var skipAction = actionable
            ? $"<form id=\"skip\" method=\"post\" action=\"/review/{E(group.Id)}/skip\"><button type=\"submit\">Skip</button></form>"
            : string.Empty;
        var html = new StringBuilder($"""
            <main><header><h1>Group {progress.CurrentPosition + 1} / {progress.Total}</h1>
            <div class="progress" role="status" aria-live="polite">Reviewed: {progress.Reviewed} · Skipped: {progress.Skipped} · Failed: {progress.Failed} · Remaining: {progress.Remaining}</div></header>
            <p class="status">Status: {group.Status}</p>{failureNotice}
            <details><summary>Keyboard shortcuts</summary><p>1–9 prefer asset · Space Keep/Trash · S stack · X skip · Enter review · Left/Right navigate · F full screen</p></details>
            <div class="review-toolbar"><div class="toolbar-left">
            <form id="previous" method="post" action="/review/navigation/previous"><button type="submit">Previous</button></form>
            <form id="next" method="post" action="/review/navigation/next"><button type="submit">Next</button></form>
            {skipAction}</div><div class="toolbar-right">{decisionAction}</div></div>
            <form id="decision" method="post" action="/review/{E(group.Id)}/propose"><div class="assets">
            """);
        for (var index = 0; index < group.Assets.Count; index++)
        {
            var asset = group.Assets[index];
            var isKept = saved is null ? (bool?)null : saved.Keep.Contains(asset.Id, StringComparer.Ordinal);
            var isStacked = saved?.Stack.Contains(asset.Id, StringComparer.Ordinal) ?? false;
            var keep = isKept == true ? "checked" : string.Empty;
            var trash = isKept == false ? "checked" : string.Empty;
            var stack = isStacked ? "checked" : string.Empty;
            var stackDisabled = isKept == true ? string.Empty : "disabled";
            var decisionInput = isKept is null
                ? string.Empty
                : $"<input type=\"hidden\" name=\"{(isKept.Value ? "keepAssetIds" : "trashAssetIds")}\" value=\"{E(asset.Id)}\" data-decision>";
            var controls = actionable
                ? $"""
                  <label><input type="radio" name="choice-{E(asset.Id)}" value="keep" {keep} required data-keep> Keep</label>
                  <label><input type="radio" name="choice-{E(asset.Id)}" value="trash" {trash} required data-trash> Trash</label>
                  <label><input type="checkbox" name="stackAssetIds" value="{E(asset.Id)}" {stack} {stackDisabled} data-stack> Stack</label>
                  {decisionInput}
                  """
                : "<p>Decision already recorded. This group is read-only.</p>";
            html.Append($"""
                <article class="asset" data-index="{index}">
                  <button type="button" class="preview-button" aria-label="Full-screen preview of {E(asset.FileName)}"><img src="/api/assets/{E(asset.Id)}/preview" alt="{E(asset.FileName)}"></button>
                  <h2>{index + 1}. {E(asset.FileName)}</h2>
                  <dl><dt>Path</dt><dd>{E(asset.OriginalPath ?? "Unknown")}</dd><dt>Captured</dt><dd>{asset.CaptureDate:yyyy-MM-dd}</dd>
                  <dt>Size</dt><dd>{FormatBytes(asset.FileSize)}</dd><dt>Dimensions</dt><dd>{asset.Width?.ToString() ?? "?"}×{asset.Height?.ToString() ?? "?"}</dd>
                  <dt>Format</dt><dd>{E(asset.Format ?? "Unknown")}</dd><dt>Camera</dt><dd>{E(asset.Camera ?? "Unknown")}</dd>
                  <dt>Albums</dt><dd>{AlbumNames(asset)}</dd><dt>Signals</dt><dd>{Signals(asset, asset.Id == largestFile, asset.Id == highestResolution)}</dd></dl>
                  {controls}
                </article>
                """);
        }
        html.Append($"""
            </div></form>
            </main><script>{KeyboardScript}</script>
            """);
        return Layout(html.ToString());
    }

    private static string RenderProposal(DuplicateGroup group, ReviewDecision decision)
    {
        static string List(DuplicateGroup value, IEnumerable<string> ids) => "<ul>" + string.Concat(ids.Select(id => $"<li>{E(value.Assets.Single(x => x.Id == id).FileName)}</li>")) + "</ul>";
        var hidden = string.Concat(decision.Keep.Select(id => $"<input type=\"hidden\" name=\"keepAssetIds\" value=\"{E(id)}\">"))
            + string.Concat(decision.Trash.Select(id => $"<input type=\"hidden\" name=\"trashAssetIds\" value=\"{E(id)}\">"))
            + string.Concat(decision.Stack.Select(id => $"<input type=\"hidden\" name=\"stackAssetIds\" value=\"{E(id)}\">"));
        var stack = decision.Stack.Count == 0 ? "<p>None</p>" : List(group, decision.Stack);
        return Layout($"""
            <main><h1>Confirm exact result</h1><section><h2>Keep</h2>{List(group, decision.Keep)}</section>
            <section><h2>Trash</h2>{List(group, decision.Trash)}<p>Assets are sent to Immich Trash; this helper never permanently deletes them.</p></section>
            <section><h2>Stack kept assets</h2>{stack}</section>
            <form method="post" action="/review/{E(group.Id)}/confirm">{hidden}<button class="danger" type="submit">Confirm in Immich</button></form>
            <a href="/review">Go back</a></main>
            """);
    }

    private static string Signals(DuplicateAsset asset, bool largestFile, bool highestResolution) => string.Join(" · ", new[]
    {
        largestFile ? "Largest file" : null, highestResolution ? "Highest resolution" : null,
        asset.HasExif ? "EXIF" : null, asset.HasGps ? "GPS" : null, asset.IsFavorite ? "Favorite" : null,
        asset.AlbumNames?.Count > 0 ? "In albums" : null, asset.Rating is not null ? $"Rating {asset.Rating}" : null
    }.Where(x => x is not null));

    private static string FormatBytes(long? bytes) => bytes is null ? "Unknown" : $"{bytes.Value / 1024d / 1024d:0.##} MB";
    private static string AlbumNames(DuplicateAsset asset) => asset.AlbumNames is null
        ? "Unavailable"
        : asset.AlbumNames.Count == 0 ? "None" : E(string.Join(", ", asset.AlbumNames));
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record SavedDecision(string[] Keep, string[] Trash, string[] Stack);
    private static string RenderBatchSizeOptions(int selected) => string.Concat(new[] { 50, 100, 250, 500 }.Select(size =>
        $"<option{(size == selected ? " selected" : string.Empty)}>{size}</option>"));
    private static string RenderSortModeOptions() => """
        <option value="oldest">Oldest first</option>
        <option value="newest">Newest first</option>
        <option value="smallest-group">Smallest group first</option>
        <option value="largest-group">Largest group first</option>
        <option value="largest-potential-saving">Largest potential storage saving</option>
        <option value="path">Path</option>
        <option value="filename">Filename</option>
        """;

    private const string KeyboardScript = """
        let focused=0; const cards=[...document.querySelectorAll('.asset')];
        function typing(e){return ['INPUT','TEXTAREA','SELECT'].includes(e.target.tagName)||e.target.isContentEditable}
        function choose(i){focused=i; cards.forEach((c,n)=>c.classList.toggle('focused',n===i));}
        cards.forEach((c,i)=>c.addEventListener('click',()=>choose(i)));
        function updateReady(){const b=document.querySelector('#review-decision');if(b)b.disabled=cards.some(c=>!c.querySelector('[data-decision]'));}
        document.querySelectorAll('[data-keep],[data-trash]').forEach(r=>r.addEventListener('change',e=>{const c=e.target.closest('.asset');let h=c.querySelector('[data-decision]');if(!h){h=document.createElement('input');h.type='hidden';h.dataset.decision='';h.value=e.target.closest('.asset').querySelector('[data-keep]').value&&e.target.name.slice(7);c.append(h);}h.name=e.target.value==='keep'?'keepAssetIds':'trashAssetIds';const s=c.querySelector('[data-stack]');s.disabled=e.target.value!=='keep';if(e.target.value==='trash')s.checked=false;updateReady();}));
        document.addEventListener('keydown',e=>{if(typing(e)||!cards.length)return; const c=cards[focused];
          if(e.key>='1'&&e.key<='9'&&+e.key<=cards.length&&cards[+e.key-1].querySelector('[data-keep]')){choose(+e.key-1);cards[focused].querySelector('[data-keep]').click();}
          else if(e.key===' '&&c.querySelector('[data-keep]')){e.preventDefault();(c.querySelector('[data-keep]').checked?c.querySelector('[data-trash]'):c.querySelector('[data-keep]')).click();}
          else if(e.key.toLowerCase()==='s'&&c.querySelector('[data-keep]')?.checked)c.querySelector('[data-stack]').click();
          else if(e.key.toLowerCase()==='x'&&document.querySelector('#skip'))document.querySelector('#skip').requestSubmit();
          else if(e.key==='Enter'&&document.querySelector('[form="decision"][type="submit"]'))document.querySelector('#decision').requestSubmit(document.querySelector('[form="decision"][type="submit"]'));
          else if(e.key==='ArrowLeft')document.querySelector('#previous').requestSubmit(); else if(e.key==='ArrowRight')document.querySelector('#next').requestSubmit();
          else if(e.key.toLowerCase()==='f')c.querySelector('img').requestFullscreen();}); choose(0);updateReady();
        """;

    private static string Layout(string body) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Immich Duplicate Review Helper</title><style>
        :root{color-scheme:dark;background:#111;color:#eee;font:15px system-ui}body{margin:0}main{max-width:1500px;margin:auto;padding:1rem}header,.review-toolbar,.toolbar-left{display:flex;justify-content:space-between;align-items:center}.review-toolbar{margin:.5rem 0}.toolbar-left{justify-content:flex-start}.toolbar-left form{margin:0}.assets{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:.8rem}.asset{background:#202124;padding:.7rem;border:2px solid transparent}.asset.focused{border-color:#7c9cff}.asset img{width:100%;height:42vh;object-fit:contain;background:#090909}.preview-button{border:0;padding:0;width:100%;background:none}.asset h2{font-size:1rem;overflow-wrap:anywhere}dl{display:grid;grid-template-columns:auto 1fr;gap:.2rem .6rem}dt{color:#aaa}dd{margin:0;overflow-wrap:anywhere}button,select{padding:.65rem;margin:.25rem;font:inherit}.danger{background:#a22;color:white}label{display:inline-block;margin:.3rem}.progress{font-size:1.1rem}
        </style></head><body>{{body}}</body></html>
        """;
}
