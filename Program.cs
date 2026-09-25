using Portfolio.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();

builder.Services.AddHttpClient("pve").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler
    {
        // PVE nutzt ein selbstsigniertes Zertifikat — nur für den internen API-Abruf
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
builder.Services.AddHttpClient("ollama", client =>
    client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient("health", client =>
    client.Timeout = TimeSpan.FromSeconds(8));

builder.Services.AddSingleton<FleetService>();
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<ChatService>();

// Der Flow-Ring steht auf der Wanduhr. TimeProvider statt DateTimeOffset.UtcNow,
// damit die Slot-Mathe später ohne Uhr-Tricks prüfbar ist.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FlowService>();

var app = builder.Build();
var startedAt = DateTimeOffset.UtcNow;

// Fail-closed: ohne Knotenliste lehnt der Ingest jeden Push ab (FlowService), weil
// der Knotenname bestimmt, wer ein Paar zählt und wann eine 0 die Wahrheit ist.
// Ist das Secret gesetzt, die Liste aber nicht, ist das eine Fehlkonfiguration —
// und zwar eine stumme: die Seite zeigt dann einfach `keine daten`. Deshalb laut.
if (!string.IsNullOrWhiteSpace(app.Configuration["Flows:IngestSecret"]) &&
    string.IsNullOrWhiteSpace(app.Configuration["Flows:Nodes"]))
{
    app.Logger.LogError("Flows:Nodes fehlt — der Flow-Ingest lehnt jeden Push ab (403)");
}

app.UseStaticFiles();

// 404 im Console-Stil — CSP erlaubt keine inline styles, daher nur der site.css-Link
app.Use(async (ctx, next) =>
{
    await next();
    if (ctx.Response.StatusCode == 404 && !ctx.Response.HasStarted
        && !ctx.Request.Path.StartsWithSegments("/api")
        && ctx.Request.Headers.Accept.ToString().Contains("text/html"))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var path = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(ctx.Request.Path);
        await ctx.Response.WriteAsync($@"<!DOCTYPE html>
<html lang=""de"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <meta name=""robots"" content=""noindex"">
  <title>404 — befehl nicht gefunden</title>
  <link rel=""stylesheet"" href=""/css/site.css"">
</head>
<body>
  <main class=""section"">
    <div class=""wrap"">
      <div class=""term"">
        <div class=""term-bar""><span></span><span></span><span></span><em>portfolio-shell — fehler</em></div>
        <pre class=""term-out""><span class=""t-cmd"">besucher@bastian-frese.de:~$</span> curl -i {path}

<span class=""t-warn"">HTTP/2 404</span>
<span class=""t-dim"">inhalt: nicht gefunden</span>

<span class=""t-cmd"">besucher@bastian-frese.de:~$</span> <span class=""t-warn"">befehl nicht gefunden: {path}</span>

<span class=""t-dim"">  diese seite existiert nicht — oder nicht mehr.
  die shell hier unten kennt trotzdem ein paar befehle:</span>

  <a href=""/"">cd ~</a>            <span class=""t-dim"">zurück zur startseite</span>
  <a href=""/#projekte"">ls projekte/</a>   <span class=""t-dim"">was ich gebaut habe</span>
  <a href=""/#fleet"">fleet</a>          <span class=""t-dim"">live-status meiner systeme</span>
  <a href=""/#kontakt"">mail -s hi</a>     <span class=""t-dim"">kontakt</span>
</pre>
      </div>
      <p class=""term-hint""><a href=""/impressum"">impressum &amp; datenschutz</a></p>
    </div>
  </main>
</body>
</html>");
    }
});

app.MapControllers();

app.MapGet("/health/live", () => Results.Ok("healthy"));
app.MapGet("/api/status", () => Results.Json(new
{
    status = "ok",
    uptimeSeconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds
}));

app.MapGet("/api/fleet", async (FleetService fleet, CancellationToken ct) =>
{
    try
    {
        return Results.Json(await fleet.GetAsync(ct));
    }
    catch (Exception)
    {
        return Results.Json(new { error = "fleet nicht erreichbar" }, statusCode: 503);
    }
});

app.MapGet("/api/health", async (HealthService health, CancellationToken ct) =>
{
    try
    {
        return Results.Json(await health.GetAllAsync(ct));
    }
    catch (Exception)
    {
        return Results.Json(new { error = "health-checks nicht erreichbar" }, statusCode: 503);
    }
});

// Gemessene Traffic-Raten — nur Namen und Dienst-Labels, nie eine IP.
// `stale: true` liefert eine leere Liste: die UI hat dann genau eine Verzweigung.
app.MapGet("/api/flows", (FlowService flows) => Results.Json(flows.Snapshot()));

// Eingang der Messpunkte. Zwingend unter /api/… — die 404-Middleware oben rendert
// sonst HTML statt eines Statuscodes. Die Knoten pushen direkt an :5000; nginx
// weist diesen Pfad nach außen ab (404, nicht 401 — eine 401 würde ihn bestätigen).
app.MapPost("/api/flows/ingest", async (HttpContext ctx, FlowService flows,
                                        IConfiguration cfg, TimeProvider clock, CancellationToken ct) =>
{
    var expected = cfg["Flows:IngestSecret"];
    // Default-Deny: ohne konfiguriertes Secret wird nicht durchgewinkt.
    if (string.IsNullOrWhiteSpace(expected)) return Results.StatusCode(503);
    if (!SecretEquals(ctx.Request.Headers["X-Flow-Secret"].ToString(), expected))
        return Results.StatusCode(401);
    if (ctx.Request.ContentLength is null or > 262_144) return Results.StatusCode(413);

    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        if (!FlowService.TryParseWindow(doc.RootElement, clock, out var window, out var error))
        {
            if (FlowService.Throttle.TryPass("ingest-parse", clock))
                app.Logger.LogWarning("flow-ingest abgelehnt: {Error}", error);
            return Results.StatusCode(400);
        }
        if (!flows.IsAllowedNode(window.Node))
        {
            if (FlowService.Throttle.TryPass("ingest-knoten", clock))
                app.Logger.LogWarning("flow-ingest abgelehnt: unbekannter knoten {Node}", window.Node);
            return Results.StatusCode(403);
        }
        flows.Ingest(window);
        return Results.StatusCode(202);
    }
    catch (Exception ex)
    {
        // Nicht nur JsonException: eine Zahl, wo eine Zeichenkette erwartet wird,
        // wirft InvalidOperationException im Parser, ein abgebrochener Body eine
        // IOException. Ohne diesen Zweig würde daraus ein 500 samt Stacktrace — pro
        // Anfrage, und die Anfrage ist ~100 Byte groß.
        if (FlowService.Throttle.TryPass("ingest-fehler", clock))
            app.Logger.LogWarning(ex, "flow-ingest abgebrochen");
        return Results.StatusCode(400);
    }
});

app.MapGet("/api/deploys", (IConfiguration cfg) =>
{
    var root = cfg["Deploys:Root"];
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
    {
        return Results.Json(new { current = "", releases = Array.Empty<string>() });
    }
    var current = "";
    // Der current-Symlink liegt neben dem releases-Ordner: /var/www/portfolio/current
    var link = Path.Join(Path.GetDirectoryName(root.TrimEnd('/')) ?? "/", "current");
    if (Directory.Exists(link))
    {
        current = Path.GetFileName(Path.TrimEndingDirectorySeparator(
            Directory.ResolveLinkTarget(link, true)?.FullName ?? link));
    }
    var releases = Directory.GetDirectories(root)
        .Select(Path.GetFileName)
        .Where(n => n is not null && n != "current")
        .OrderByDescending(n => n)
        .Take(10)
        .ToArray();
    return Results.Json(new { current, releases });
});

app.MapPost("/api/chat", async (HttpContext ctx, ChatService chat, CancellationToken ct) =>
{
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (chat.IsRateLimited(clientIp))
    {
        return Results.Json(new { error = "zu viele Anfragen — bitte in ein paar Minuten nochmal." },
            statusCode: 429);
    }

    ChatRequest request;
    try
    {
        request = await ctx.Request.ReadFromJsonAsync<ChatRequest>(ct) ?? new ChatRequest("", []);
    }
    catch (JsonException)
    {
        return Results.Json(new { error = "ungültige Anfrage." }, statusCode: 400);
    }

    request = ChatService.Sanitize(request);
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.Json(new { error = "Nachricht fehlt." }, statusCode: 400);
    }

    try
    {
        var reply = await chat.AskAsync(request, ct);
        return Results.Json(new { reply });
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { error = "das ki-modell ist gerade nicht erreichbar." },
            statusCode: 503);
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Json(new { error = "antwort hat zu lange gedauert." }, statusCode: 504);
    }
});

app.Run();

// Vergleich zweier Geheimnisse in konstanter Zeit: beide Seiten zuerst auf eine
// feste Länge hashen, dann FixedTimeEquals. Ohne das verrät die Laufzeit die
// Länge des erwarteten Secrets und den übereinstimmenden Präfix.
static bool SecretEquals(string provided, string expected)
{
    var a = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided));
    var b = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected));
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}