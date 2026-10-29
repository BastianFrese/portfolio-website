using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Portfolio.Services;

// Öffentlich sind nur Aggregate. Bewusst nicht enthalten: Gastnamen, Knotennamen,
// Einzel-Laufzeiten, Zuordnungen und Traffic-Raten. Die Seite soll den Zustand
// belegen, nicht die interne Topologie veröffentlichen. Knoten erscheinen in
// stabiler Reihenfolge, aber ohne Namen als „Knoten 1..n".
public sealed record FleetCount(int Total, int Running);
public sealed record FleetNode(string Status, double Cpu, long Mem, long MaxMem);
public sealed record FleetResponse(int Total, int Running, FleetCount Vm, FleetCount Ct, List<FleetNode> Nodes);

/// <summary>
/// Holt die Betriebswerte aus der Proxmox-Cluster-API und cached sie.
/// Token &amp; URL kommen ausschließlich aus der Konfiguration (Environment auf dem Server).
/// </summary>
public sealed class FleetService(IHttpClientFactory factory, IMemoryCache cache, IConfiguration config)
{
    private const int CacheSeconds = 5;

    public async Task<FleetResponse> GetAsync(CancellationToken ct)
    {
        var cached = await cache.GetOrCreateAsync("fleet", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheSeconds);
            return await FetchAsync(ct);
        });
        return cached ?? Empty;
    }

    private static readonly FleetResponse Empty =
        new(0, 0, new FleetCount(0, 0), new FleetCount(0, 0), []);

    private async Task<FleetResponse> FetchAsync(CancellationToken ct)
    {
        var baseUrl = config["Fleet:PveUrl"];
        var token = config["Fleet:PveToken"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return Empty;
        }

        var client = factory.CreateClient("pve");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api2/json/cluster/resources");
        // Token kommt als kompletter Authorization-Header-Wert ("PVEAPIToken=user@realm!id=secret")
        request.Headers.TryAddWithoutValidation("Authorization", token);
        using var resp = await client.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // Namen werden nur zum stabilen Sortieren gelesen und nie ausgeliefert.
        var nodes = new List<(string Name, FleetNode Node)>();
        int vmTotal = 0, vmRunning = 0, ctTotal = 0, ctRunning = 0;

        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var typeRaw = (el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null) ?? "";

            if (typeRaw == "node")
            {
                nodes.Add((
                    Str(el, "node"),
                    new FleetNode(
                        Str(el, "status"),
                        Num(el, "cpu"),
                        Long(el, "mem"),
                        Long(el, "maxmem"))));
                continue; // Nodes separat, nicht als Gäste
            }

            if (typeRaw != "qemu" && typeRaw != "lxc") continue;
            if (el.TryGetProperty("template", out var tpl) &&
                tpl.ValueKind == JsonValueKind.Number && tpl.GetInt32() == 1)
            {
                continue; // Templates sind keine laufenden Systeme
            }

            var running = Str(el, "status") == "running";
            if (typeRaw == "qemu")
            {
                vmTotal++;
                if (running) vmRunning++;
            }
            else
            {
                ctTotal++;
                if (running) ctRunning++;
            }
        }

        var sorted = nodes
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .Select(n => n.Node)
            .ToList();

        return new FleetResponse(
            vmTotal + ctTotal,
            vmRunning + ctRunning,
            new FleetCount(vmTotal, vmRunning),
            new FleetCount(ctTotal, ctRunning),
            sorted);
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "?"
            : "?";

    private static long Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    private static double Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0;
}
