using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Portfolio.Services;

public sealed record ChatMessage(string Role, string Content);
public sealed record ChatRequest(string Message, List<ChatMessage> History);

/// <summary>
/// Proxy zur lokalen Ollama-Instanz (GPU-VM). Kein Cloud-Aufruf — das Modell
/// läuft im eigenen Netz. Per-IP-Rate-Limit gegen Missbrauch.
/// </summary>
public sealed class ChatService(IHttpClientFactory factory, IConfiguration config)
{
    private const int MaxMessageChars = 800;
    private const int MaxHistoryItems = 8;
    private const int MaxRequestsPerWindow = 12;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _hits = new();

    public string Model => config["Ollama:Model"] ?? "qwen3:8b";

    public bool IsRateLimited(string clientIp)
    {
        var now = DateTimeOffset.UtcNow;
        var list = _hits.GetOrAdd(clientIp, _ => []);
        lock (list)
        {
            list.RemoveAll(h => now - h > RateWindow);
            if (list.Count >= MaxRequestsPerWindow)
            {
                return true;
            }
            list.Add(now);
            return false;
        }
    }

    public static ChatRequest Sanitize(ChatRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return new ChatRequest("", []);
        }
        var history = (request.History ?? [])
            .Where(m => m is not null &&
                        (m.Role == "user" || m.Role == "assistant") &&
                        !string.IsNullOrWhiteSpace(m.Content))
            .Take(MaxHistoryItems)
            .Select(m => new ChatMessage(m.Role, m.Content[..Math.Min(m.Content.Length, MaxMessageChars)]))
            .ToList();
        var message = request.Message.Trim()[..Math.Min(request.Message.Trim().Length, MaxMessageChars)];
        return new ChatRequest(message, history);
    }

    public async Task<string> AskAsync(ChatRequest request, CancellationToken ct)
    {
        var baseUrl = config["Ollama:Url"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Ollama nicht konfiguriert.");
        }

        var messages = new List<ChatMessage> { new("system", SystemPrompt) };
        messages.AddRange(request.History);
        messages.Add(new ChatMessage("user", request.Message));

        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            stream = false,
            think = false,
            messages,
            options = new { num_predict = 280, temperature = 0.7 }
        });

        var client = factory.CreateClient("ollama");
        using var resp = await client.PostAsync(
            $"{baseUrl}/api/chat",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string SystemPrompt =>
        "Du bist der Assistent auf bastian-frese.de, dem Portfolio von Bastian Frese, " +
        "angehender Fachinformatiker für Systemintegration. Antworte auf Deutsch, kurz " +
        "(höchstens drei Sätze), freundlich und sachlich. Du darfst über Bastians Projekte " +
        "erzählen: ERDI's Racing Community (Liga-Plattform, ASP.NET Core, 340 Tests), " +
        "ERCTelemetry (WPF-Desktop-App), Loren Flowers (Onlineshop, Ehrenamt), Lyra (lokaler " +
        "Sprachassistent) und sein Homelab (Proxmox-Cluster, Monitoring, CI/CD). Eine " +
        "Ausbildung ab dem 01.08.2027 ist möglich, ein Praktikum davor gern. Erfinde nichts, " +
        "was nicht hier steht, und gib keine privaten oder sensiblen Informationen weiter. /no_think";
}
