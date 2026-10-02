using System.Net;
using System.Text;
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

        endpoints.MapPost("/review/start", async (HttpRequest request, IImmichClient immich, ReviewStore store, BatchOptions options, CancellationToken cancellationToken) =>
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var size = int.TryParse(form["batchSize"], out var parsed) ? parsed : options.DefaultSize;
            var sortMode = SortModes.Parse(form["sortMode"]);
            var groups = await immich.GetDuplicateGroupsAsync(cancellationToken);
            await store.UpsertGroupsAsync(groups, cancellationToken);
            var batch = CreateBatch.Handle(await store.LoadPendingAsync(cancellationToken), size, sortMode);
            await store.CreateOrResumeSessionAsync(size, batch.Groups.Select(group => group.Id).ToArray(), sortMode.ToValue(), cancellationToken);
            return Results.Redirect("/review");
        }).DisableAntiforgery();

        endpoints.MapGet("/review", async (ReviewStore store, CancellationToken cancellationToken) =>
        {
            var group = await store.LoadCurrentActiveGroupAsync(cancellationToken);
            if (group is null) return Results.Content(Layout("<main><h1>Batch complete</h1><p>No pending groups remain.</p></main>"), "text/html");
            var progress = await store.GetActiveProgressAsync(cancellationToken);
            return Results.Content(RenderReview(group, progress), "text/html");
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

        endpoints.MapPost("/review/{groupId}/confirm", async (string groupId, HttpRequest request, ReviewStore store, IImmichClient immich, CancellationToken cancellationToken) =>
        {
            var group = await store.LoadGroupAsync(groupId, cancellationToken);
            if (group is null) return Results.NotFound();
            var form = await request.ReadFormAsync(cancellationToken);
            var decision = ReviewDecision.Create(group, form["keepAssetIds"], form["trashAssetIds"], form["stackAssetIds"]);
            await new ConfirmReview(store, immich).HandleAsync(group, decision, cancellationToken);
            return Results.Redirect("/review");
        }).DisableAntiforgery();

        endpoints.MapPost("/review/{groupId}/skip", async (string groupId, ReviewStore store, CancellationToken cancellationToken) =>
        {
            await store.SkipAsync(groupId, null, cancellationToken);
            return Results.Redirect("/review");
        }).DisableAntiforgery();
        return endpoints;
    }

    private static string RenderReview(DuplicateGroup group, ReviewProgress progress)
    {
        var actionable = group.Status is ReviewStatus.Pending or ReviewStatus.Failed;
        var html = new StringBuilder($"""
            <main><header><h1>Group {progress.CurrentPosition + 1} / {progress.Total}</h1>
            <div class="progress">Reviewed: {progress.Reviewed} · Skipped: {progress.Skipped} · Failed: {progress.Failed} · Remaining: {progress.Remaining}</div></header>
            <p class="status">Status: {group.Status}</p>
            <form id="decision" method="post" action="/review/{E(group.Id)}/propose"><div class="assets">
            """);
        for (var index = 0; index < group.Assets.Count; index++)
        {
            var asset = group.Assets[index];
            var keep = index == 0 ? "checked" : string.Empty;
            var trash = index == 0 ? string.Empty : "checked";
            var controls = actionable
                ? $"""
                  <label><input type="radio" name="choice-{E(asset.Id)}" value="keep" {keep} data-keep> Keep</label>
                  <label><input type="radio" name="choice-{E(asset.Id)}" value="trash" {trash} data-trash> Trash</label>
                  <label><input type="checkbox" name="stackAssetIds" value="{E(asset.Id)}" data-stack> Stack</label>
                  <input type="hidden" name="{(index == 0 ? "keepAssetIds" : "trashAssetIds")}" value="{E(asset.Id)}" data-decision>
                  """
                : "<p>Decision already recorded. This group is read-only.</p>";
            html.Append($"""
                <article class="asset" data-index="{index}">
                  <button type="button" class="preview-button" aria-label="Full-screen preview"><img src="/api/assets/{E(asset.Id)}/preview" alt="{E(asset.FileName)}"></button>
                  <h2>{index + 1}. {E(asset.FileName)}</h2>
                  <dl><dt>Path</dt><dd>{E(asset.OriginalPath ?? "Unknown")}</dd><dt>Captured</dt><dd>{asset.CaptureDate:yyyy-MM-dd}</dd>
                  <dt>Size</dt><dd>{FormatBytes(asset.FileSize)}</dd><dt>Dimensions</dt><dd>{asset.Width?.ToString() ?? "?"}×{asset.Height?.ToString() ?? "?"}</dd>
                  <dt>Camera</dt><dd>{E(asset.Camera ?? "Unknown")}</dd><dt>Signals</dt><dd>{Signals(asset)}</dd></dl>
                  {controls}
                </article>
                """);
        }
        var decisionAction = actionable ? "<div class=\"actions\"><button type=\"submit\">Review proposed changes</button></div>" : string.Empty;
        var skipAction = actionable ? $"<form id=\"skip\" method=\"post\" action=\"/review/{E(group.Id)}/skip\"><button type=\"submit\">Skip</button></form>" : string.Empty;
        html.Append($"""
            </div>{decisionAction}</form>
            <nav><form id="previous" method="post" action="/review/navigation/previous"><button type="submit">Previous</button></form>
            <form id="next" method="post" action="/review/navigation/next"><button type="submit">Next</button></form></nav>
            {skipAction}
            <details><summary>Keyboard shortcuts</summary><p>1–9 prefer asset · Space Keep/Trash · S stack · X skip · Enter review · Left/Right navigate · F full screen</p></details>
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

    private static string Signals(DuplicateAsset asset) => string.Join(" · ", new[]
    {
        asset.HasExif ? "EXIF" : null, asset.HasGps ? "GPS" : null, asset.IsFavorite ? "Favorite" : null,
        asset.AlbumNames?.Count > 0 ? "In albums" : null, asset.Rating is not null ? $"Rating {asset.Rating}" : null
    }.Where(x => x is not null));

    private static string FormatBytes(long? bytes) => bytes is null ? "Unknown" : $"{bytes.Value / 1024d / 1024d:0.##} MB";
    private static string E(string value) => WebUtility.HtmlEncode(value);
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
        document.querySelectorAll('[data-keep],[data-trash]').forEach(r=>r.addEventListener('change',e=>{const c=e.target.closest('.asset'), h=c.querySelector('[data-decision]'); h.name=e.target.value==='keep'?'keepAssetIds':'trashAssetIds'; if(e.target.value==='trash')c.querySelector('[data-stack]').checked=false;}));
        document.addEventListener('keydown',e=>{if(typing(e)||!cards.length)return; const c=cards[focused];
          if(e.key>='1'&&e.key<='9'&&+e.key<=cards.length&&cards[+e.key-1].querySelector('[data-keep]')){choose(+e.key-1);cards[focused].querySelector('[data-keep]').click();}
          else if(e.key===' '&&c.querySelector('[data-keep]')){e.preventDefault();(c.querySelector('[data-keep]').checked?c.querySelector('[data-trash]'):c.querySelector('[data-keep]')).click();}
          else if(e.key.toLowerCase()==='s'&&c.querySelector('[data-keep]')?.checked)c.querySelector('[data-stack]').click();
          else if(e.key.toLowerCase()==='x'&&document.querySelector('#skip'))document.querySelector('#skip').requestSubmit();
          else if(e.key==='Enter'&&document.querySelector('#decision button[type=submit]'))document.querySelector('#decision').requestSubmit();
          else if(e.key==='ArrowLeft')document.querySelector('#previous').requestSubmit(); else if(e.key==='ArrowRight')document.querySelector('#next').requestSubmit();
          else if(e.key.toLowerCase()==='f')c.querySelector('img').requestFullscreen();}); choose(0);
        """;

    private static string Layout(string body) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Immich Duplicate Review Helper</title><style>
        :root{color-scheme:dark;background:#111;color:#eee;font:15px system-ui}body{margin:0}main{max-width:1500px;margin:auto;padding:1rem}header,.actions{display:flex;justify-content:space-between;align-items:center}.assets{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:.8rem}.asset{background:#202124;padding:.7rem;border:2px solid transparent}.asset.focused{border-color:#7c9cff}.asset img{width:100%;height:42vh;object-fit:contain;background:#090909}.preview-button{border:0;padding:0;width:100%;background:none}.asset h2{font-size:1rem;overflow-wrap:anywhere}dl{display:grid;grid-template-columns:auto 1fr;gap:.2rem .6rem}dt{color:#aaa}dd{margin:0;overflow-wrap:anywhere}button,select{padding:.65rem;margin:.25rem;font:inherit}.danger{background:#a22;color:white}label{display:inline-block;margin:.3rem}.progress{font-size:1.1rem}
        </style></head><body>{{body}}</body></html>
        """;
}
