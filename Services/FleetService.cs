using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Portfolio.Services;

public sealed record FleetGuest(string Name, string Type, string Node, string Status, long UptimeSeconds);
public sealed record FleetNode(string Name, string Status, double Cpu, long Mem, long MaxMem, long UptimeSeconds);
public sealed record FleetResponse(int Total, int Running, List<FleetGuest> Guests, List<FleetNode> Nodes);

/// <summary>
/// Holt den Gast- und Node-Status aus der Proxmox-Cluster-API und cached ihn.
/// Token & URL kommen ausschließlich aus der Konfiguration (Environment auf dem Server).
/// </summary>
public sealed class FleetService(IHttpClientFactory factory, IMemoryCache cache, IConfiguration config)
{
    private const int CacheSeconds = 45;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<FleetResponse> GetAsync(CancellationToken ct)
    {
        var cached = await cache.GetOrCreateAsync("fleet", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheSeconds);
            return await FetchAsync(ct);
        });
        return cached ?? new FleetResponse(0, 0, [], []);
    }

    private async Task<FleetResponse> FetchAsync(CancellationToken ct)
    {
        var baseUrl = config["Fleet:PveUrl"];
        var token = config["Fleet:PveToken"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return new FleetResponse(0, 0, [], []);
        }

        var client = factory.CreateClient("pve");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api2/json/cluster/resources");
        // Token kommt als kompletter Authorization-Header-Wert ("PVEAPIToken=user@realm!id=secret")
        request.Headers.TryAddWithoutValidation("Authorization", token);
        using var resp = await client.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var guests = new List<FleetGuest>();
        var nodes = new List<FleetNode>();
        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var typeRaw = (el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null) ?? "";
            if (typeRaw == "node")
            {
                nodes.Add(new FleetNode(
                    el.TryGetProperty("node", out var nn) ? nn.GetString() ?? "?" : "?",
                    el.TryGetProperty("status", out var nst) ? nst.GetString() ?? "?" : "?",
                    el.TryGetProperty("cpu", out var ncpu) && ncpu.ValueKind == JsonValueKind.Number ? ncpu.GetDouble() : 0,
                    el.TryGetProperty("mem", out var nmem) && nmem.ValueKind == JsonValueKind.Number ? nmem.GetInt64() : 0,
                    el.TryGetProperty("maxmem", out var nmax) && nmax.ValueKind == JsonValueKind.Number ? nmax.GetInt64() : 0,
                    el.TryGetProperty("uptime", out var nup) && nup.ValueKind == JsonValueKind.Number ? nup.GetInt64() : 0));
                continue; // Nodes separat, nicht als Gäste
            }
            if (typeRaw != "qemu" && typeRaw != "lxc") continue;
            if (el.TryGetProperty("template", out var tpl) &&
                tpl.ValueKind == JsonValueKind.Number && tpl.GetInt32() == 1)
            {
                continue; // Templates sind keine laufenden Systeme
            }
            guests.Add(new FleetGuest(
                el.TryGetProperty("name", out var name) ? name.GetString() ?? "?" : "?",
                typeRaw == "qemu" ? "vm" : "lxc",
                el.TryGetProperty("node", out var node) ? node.GetString() ?? "?" : "?",
                el.TryGetProperty("status", out var status) ? status.GetString() ?? "?" : "?",
                el.TryGetProperty("uptime", out var up) && up.ValueKind == JsonValueKind.Number ? up.GetInt64() : 0));
        }

        var sorted = guests
            .OrderByDescending(g => g.Status == "running")
            .ThenBy(g => g.Node, StringComparer.Ordinal)
            .ThenBy(g => g.Type, StringComparer.Ordinal)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

        var sortedNodes = nodes.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        return new FleetResponse(sorted.Count, sorted.Count(g => g.Status == "running"), sorted, sortedNodes);
    }
}