using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Portfolio.Services;

public sealed record FleetGuest(string Name, string Type, string Node, string Status, long UptimeSeconds, long NetIn, long NetOut);
public sealed record FleetNode(string Name, string Status, double Cpu, long Mem, long MaxMem, long UptimeSeconds, long NetIn, long NetOut);
public sealed record FleetResponse(int Total, int Running, List<FleetGuest> Guests, List<FleetNode> Nodes);

/// <summary>
/// Holt den Gast- und Node-Status aus der Proxmox-Cluster-API und cached ihn.
/// Token & URL kommen ausschließlich aus der Konfiguration (Environment auf dem Server).
/// netin/netout der API sind kumulative Bytes — Raten berechnen wir selbst per Delta
/// zwischen zwei Abrufen (Abstand = Cache-Fenster).
/// </summary>
public sealed class FleetService(IHttpClientFactory factory, IMemoryCache cache, IConfiguration config)
{
    private const int CacheSeconds = 45;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Kumulative Zähler des letzten Abrufs: key = "node/name"
    private readonly ConcurrentDictionary<string, (long NetIn, long NetOut, DateTimeOffset At)> _lastCounters = new();

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

        var now = DateTimeOffset.UtcNow;
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
                    el.TryGetProperty("uptime", out var nup) && nup.ValueKind == JsonValueKind.Number ? nup.GetInt64() : 0,
                    0, 0));
                continue; // Nodes separat, nicht als Gäste
            }
            if (typeRaw != "qemu" && typeRaw != "lxc") continue;
            if (el.TryGetProperty("template", out var tpl) &&
                tpl.ValueKind == JsonValueKind.Number && tpl.GetInt32() == 1)
            {
                continue; // Templates sind keine laufenden Systeme
            }

            var key = (el.TryGetProperty("node", out var nodeEl) ? nodeEl.GetString() ?? "?" : "?")
                      + "/" + (el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "?" : "?");
            var netIn = el.TryGetProperty("netin", out var ni) && ni.ValueKind == JsonValueKind.Number ? ni.GetInt64() : 0;
            var netOut = el.TryGetProperty("netout", out var no) && no.ValueKind == JsonValueKind.Number ? no.GetInt64() : 0;

            // Rate per Delta: kumulative Zähler → bytes/s seit dem letzten Abruf
            long rateIn = 0, rateOut = 0;
            if (_lastCounters.TryGetValue(key, out var prev) && now > prev.At)
            {
                var seconds = (now - prev.At).TotalSeconds;
                if (seconds > 0 && netIn >= prev.NetIn && netOut >= prev.NetOut)
                {
                    rateIn = (long)((netIn - prev.NetIn) / seconds);
                    rateOut = (long)((netOut - prev.NetOut) / seconds);
                }
                // Zähler-Reset (Neustart): cur < prev → Rate 0 für diesen Zyklus
            }
            _lastCounters[key] = (netIn, netOut, now);

            guests.Add(new FleetGuest(
                key.Split('/')[1],
                typeRaw == "qemu" ? "vm" : "lxc",
                key.Split('/')[0],
                el.TryGetProperty("status", out var status) ? status.GetString() ?? "?" : "?",
                el.TryGetProperty("uptime", out var up) && up.ValueKind == JsonValueKind.Number ? up.GetInt64() : 0,
                rateIn,
                rateOut));
        }

        // Node-Traffic = Summe seiner Gäste
        var withRates = guests
            .OrderByDescending(g => g.Status == "running")
            .ThenBy(g => g.Node, StringComparer.Ordinal)
            .ThenBy(g => g.Type, StringComparer.Ordinal)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
        var nodesWithNet = nodes.Select(n => new FleetNode(
            n.Name, n.Status, n.Cpu, n.Mem, n.MaxMem, n.UptimeSeconds,
            withRates.Where(g => g.Node == n.Name).Sum(g => g.NetIn),
            withRates.Where(g => g.Node == n.Name).Sum(g => g.NetOut))).ToList();

        return new FleetResponse(withRates.Count, withRates.Count(g => g.Status == "running"), withRates, nodesWithNet);
    }
}