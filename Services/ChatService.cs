using System.Text;
using System.Text.Json;

namespace Portfolio.Services;

public sealed record ChatMessage(string Role, string Content);
public sealed record ChatRequest(string Message, List<ChatMessage> History);

/// <summary>
/// Proxy zur lokalen Ollama-Instanz (GPU-VM). Kein Cloud-Aufruf — das Modell läuft
/// im eigenen Netz.
///
/// Der Chat ist <b>kein</b> allgemeiner Assistent: er beantwortet ausschließlich Fragen
/// zu Bastian, seinen Kenntnissen, Erfahrungen und Projekten, und er gibt keinen Code
/// aus. Die eigentliche Steuerung ist der Systemprompt; <see cref="StripCode"/> ist der
/// billige Fang für den Normalfall — <b>kein Beweis</b>. Ein Modell dieser Größe hält
/// ein Thema nicht mit Sicherheit ein, und die Prüfung erkennt Codeblöcke, nicht Code:
/// Fließtext, der wie eine Anleitung klingt, kommt durch.
/// </summary>
public sealed class ChatService(IHttpClientFactory factory, IConfiguration config, TimeProvider clock)
{
    /// <summary>
    /// Die eine Absage, die überall gleich klingt — im Prompt (das Modell benutzt sie
    /// wörtlich) und in <see cref="StripCode"/> (wenn von einer Antwort nichts übrig
    /// bleibt). Zwei Wege, ein Satz: sonst merkt der Besucher, welcher Weg gegriffen hat.
    /// </summary>
    public const string Refusal =
        "Das gehört nicht zu Bastians Portfolio — ich erzähle gern, was er gebaut hat und was er kann.";

    private const int MaxMessageChars = 500;
    private const int MaxHistoryItems = 4;
    private const int NumPredict = 200;

    /// <summary>
    /// Unter dieser Länge ist ein <b>Rest</b> keine Antwort mehr. Gilt nur, wenn
    /// tatsächlich etwas entfernt wurde — eine kurze, vollständige Antwort ohne Code
    /// wird nie angefasst (siehe <see cref="StripCode"/>).
    /// </summary>
    private const int MinReplyChars = 40;

    public string Model => config["Ollama:Model"] ?? "qwen3:8b";

    /// <summary>
    /// Die drei Deckel. Zahlen aus der Konfiguration (<c>Chat:*</c> bzw. <c>CHAT__*</c>),
    /// mit den Vorgaben als Rückfall — dasselbe Muster wie <c>Fleet:*</c> und
    /// <c>Ollama:*</c>. Fehlt der Wert, gilt die Vorgabe, nicht „kein Limit".
    /// </summary>
    public ChatLimits Limits { get; } = new(
        Limit(config, "Chat:PerVisitorLimit", ChatLimits.DefaultPerVisitor),
        Limit(config, "Chat:DailyLimit", ChatLimits.DefaultDaily),
        Limit(config, "Chat:MaxConcurrent", ChatLimits.DefaultMaxConcurrent),
        clock);

    private static int Limit(IConfiguration config, string key, int fallback) =>
        ChatLimits.Positive(int.TryParse(config[key], out var value) ? value : null, fallback);

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
            // TakeLast, nicht Take: der Verlauf ist chronologisch, und das Modell
            // braucht den jüngsten Austausch. Mit Take bekam es den Anfang eines
            // Gesprächs und verlor genau die Frage, auf die es antworten soll.
            .TakeLast(MaxHistoryItems)
            .Select(m => new ChatMessage(m.Role, m.Content[..Math.Min(m.Content.Length, MaxMessageChars)]))
            .ToList();
        var message = request.Message.Trim()[..Math.Min(request.Message.Trim().Length, MaxMessageChars)];
        return new ChatRequest(message, history);
    }

    /// <summary>
    /// Nimmt Codeblöcke aus der fertigen Antwort; was bleibt, ist die Prosa. Bleibt
    /// nach dem Entfernen zu wenig übrig, kommt <see cref="Refusal"/> zurück.
    ///
    /// Erkannt werden <b>Blöcke</b>: Zeilen mit drei Backticks (mit und ohne
    /// Sprachmarke) und durchgehend eingerichtete Blöcke ab vier Leerzeichen. Einzelne
    /// Backticks bleiben unangetastet — <c>ASP.NET Core</c> in einfachen Backticks ist
    /// ein Technik-Name, kein Code.
    /// </summary>
    public static string StripCode(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return Refusal;
        }

        var lines = reply.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var inFence = false;
        var dropped = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                dropped = true;
                continue;
            }
            if (inFence)
            {
                continue;
            }

            if (IsIndented(line))
            {
                // Ein Block, kein Zufall: erst zusammenhängend lesen, dann entscheiden.
                // Eine einzelne eingerückte Zeile bleibt stehen — das ist meist ein
                // fortgesetzter Absatz.
                var start = i;
                var end = i;
                while (end < lines.Length &&
                       (IsIndented(lines[end]) || string.IsNullOrWhiteSpace(lines[end])))
                {
                    end++;
                }
                var blockEnd = end;
                while (blockEnd > start && string.IsNullOrWhiteSpace(lines[blockEnd - 1]))
                {
                    blockEnd--;
                }
                var codeLines = 0;
                for (var k = start; k < blockEnd; k++)
                {
                    if (IsIndented(lines[k]))
                    {
                        codeLines++;
                    }
                }
                if (codeLines >= 2)
                {
                    dropped = true;
                    i = end - 1;
                    continue;
                }
            }
            kept.Add(line);
        }

        var rest = string.Join("\n", kept).Trim();
        if (!dropped)
        {
            // Nichts entfernt: die Antwort ist, was das Modell gesagt hat — auch wenn
            // sie kurz ist. Hier darf die Längenschwelle nicht greifen, sonst würde
            // eine knappe, korrekte Antwort zur Absage.
            return rest;
        }
        return rest.Length < MinReplyChars ? Refusal : rest;
    }

    private static bool IsIndented(string line) =>
        line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t');

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
            options = new { num_predict = NumPredict, temperature = 0.7 }
        });

        var client = factory.CreateClient("ollama");
        using var resp = await client.PostAsync(
            $"{baseUrl}/api/chat",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        return StripCode(content);
    }

    /// <summary>
    /// Der Text, den das Modell als Systemnachricht bekommt. <c>internal</c>, damit
    /// <c>--selftest-chat --dump-prompt</c> ihn zeigen kann — die Grenze ist der
    /// eigentliche Mechanismus dieses Chats und muss nachlesbar sein.
    /// </summary>
    internal static string SystemPrompt =>
        $"""
        Du bist der Assistent auf bastian-frese.de, dem Portfolio von Bastian Frese,
        angehender Fachinformatiker für Systemintegration.

        DEINE GRENZE
        Du beantwortest ausschließlich Fragen zu Bastian selbst: zu seiner Person,
        seinen Kenntnissen, Erfahrungen, Projekten und seiner beruflichen Ausrichtung.
        Alles andere beantwortest du nicht — kein Allgemeinwissen, keine Erklärungen zu
        fremden Themen, keine Aufgaben aus Ausbildung oder Studium, keine Rezepte,
        keine Programmierhilfe für Besucher. Auf eine Frage außerhalb dieses Themas
        antwortest du mit genau diesem Satz und nichts weiter: {Refusal}

        KEIN CODE
        Gib niemals Code aus: keine Codeblöcke, keine Befehlszeilen, keine
        Konfigurationsdateien, keine vollständigen Implementierungen — auch nicht
        "nur kurz". Du darfst Technik erklären (womit hat er das gebaut, warum so),
        aber nichts zum Abschreiben liefern.

        PRIVATES BLEIBT PRIVAT
        Fragen zu Gesundheit, Familie, Wohnort, Geld oder Privatleben beantwortest du
        nicht — dieselbe Absage.

        FORM
        Antworte auf Deutsch, kurz (höchstens drei Sätze), freundlich und sachlich.
        Erfinde nichts, was nicht in den Fakten unten steht. Weißt du etwas nicht,
        sagst du das offen, statt zu raten.

        FAKTEN — nur das hier ist belegt:
        {PortfolioFacts.Text}

        /no_think
        """;
}
