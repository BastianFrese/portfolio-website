using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace Portfolio.Services;

public sealed record HealthCheck(string Name, bool Ok, int Ms, int Status);

/// <summary>
/// Erreichbarkeits-Checks für die öffentlich laufenden Projekte.
/// Server-seitig (kein CORS/AdBlock im Weg), gecacht, jede Antwort zählt
/// als "erreichbar" — auch ein SSO-Redirect beweist, dass der Dienst lebt.
/// </summary>
public sealed class HealthService(IHttpClientFactory factory, IMemoryCache cache, IConfiguration config)
{
    private const int CacheSeconds = 60;
    private static readonly (string Name, string Url)[] DefaultSites =
    {
        ("erdi-erc.de", "https://erdi-erc.de"),
        ("loren-flowers.shop", "https://loren-flowers.shop"),
        ("cloud.", "https://cloud.bastian-frese.de"),
        ("bastian-frese.de", "https://bastian-frese.de/health/live"),
    };

    public async Task<List<HealthCheck>> GetAllAsync(CancellationToken ct)
    {
        var cached = await cache.GetOrCreateAsync("health", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheSeconds);
            return await CheckAllAsync(ct);
        });
        return cached ?? [];
    }

    private async Task<List<HealthCheck>> CheckAllAsync(CancellationToken ct)
    {
        var sites = ParseSites();
        var client = factory.CreateClient("health");
        var results = new List<HealthCheck>();

        foreach (var (name, url) in sites)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                results.Add(new HealthCheck(name, (int)resp.StatusCode < 500, (int)sw.ElapsedMilliseconds, (int)resp.StatusCode));
            }
            catch (Exception)
            {
                results.Add(new HealthCheck(name, false, 0, 0));
            }
        }
        return results;
    }

    private List<(string Name, string Url)> ParseSites()
    {
        var raw = config["Health:Sites"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [.. DefaultSites];
        }
        var sites = new List<(string, string)>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('|');
            if (idx > 0)
            {
                sites.Add((part[..idx], part[(idx + 1)..]));
            }
        }
        return sites.Count > 0 ? sites : [.. DefaultSites];
    }
}