using System.Security.Cryptography;
using System.Text;

namespace Portfolio.Services;

/// <summary>Wie eine Anfrage ausgeht — die Absage ist billig, der Modellaufruf nicht.</summary>
public enum ChatVerdict
{
    Allowed,
    VisitorLimit,
    DailyLimit
}

/// <summary>
/// Die drei Deckel des KI-Chats in <b>einem</b> Sperrbereich: je Besucher, über alle
/// Besucher pro Tag und gleichzeitig ans Modell.
///
/// Warum eine Sperre und nicht drei <see cref="System.Collections.Concurrent"/>-Strukturen:
/// die Begrenzung ist kein heißer Pfad (acht Anfragen je zehn Minuten und Besucher), und
/// eine Sperre, die drei Dinge auf einmal richtig macht, ist weniger wert, als sie kostet,
/// wenn drei Strukturen sich gegenseitig inkonsistent sehen.
///
/// <b>Die Adresse selbst liegt hier nie.</b> Der Schlüssel ist ein HMAC-Abdruck der
/// Adresse mit einem beim Start zufällig erzeugten Wert — er wird nicht protokolliert und
/// überlebt keinen Neustart. Das ist die prüfbare Zusage aus <c>/impressum</c>.
///
/// Bewusst ohne Abhängigkeit auf HTTP: dadurch ist die ganze Zählerei im Selbsttest
/// (<c>--selftest-chat</c>) ohne Netz und ohne Modell prüfbar.
/// </summary>
public sealed class ChatLimits
{
    public const int DefaultPerVisitor = 8;
    public const int DefaultDaily = 300;
    public const int DefaultMaxConcurrent = 2;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Obergrenze der geführten Schlüssel. Ohne sie wächst das Wörterbuch mit jeder
    /// fremden Adresse — heute unbemerkt, weil es genau einen Schlüssel gibt.
    /// </summary>
    private const int MaxKeys = 10_000;

    /// <summary>
    /// Die Zeitzone des Tagesdeckels, einmal aufgelöst. Findet sie sich nicht, bleibt
    /// <c>null</c> und der Tag läuft in UTC weiter — <b>nicht</b> ohne Deckel. Eine
    /// Ausnahme an dieser Stelle würde die Begrenzung aufheben, und ein stiller
    /// Ausfall der Begrenzung ist genau das, was hier nicht passieren darf.
    /// </summary>
    private static readonly TimeZoneInfo? Zone = ResolveZone();

    private readonly TimeProvider _clock;
    private readonly int _perVisitor;
    private readonly int _daily;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _hits = [];
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private DateOnly _day;
    private int _dayCount;

    public ChatLimits(int perVisitor, int daily, int maxConcurrent, TimeProvider clock)
    {
        _clock = clock;
        _perVisitor = Positive(perVisitor, DefaultPerVisitor);
        _daily = Positive(daily, DefaultDaily);
        Slots = new SemaphoreSlim(Positive(maxConcurrent, DefaultMaxConcurrent));
    }

    /// <summary>
    /// Fail-closed: ein fehlender, unlesbarer oder unsinniger Wert bedeutet „Vorgabe",
    /// niemals „kein Limit". Ein Tippfehler in der Env-Datei darf den Schutz nicht
    /// abschalten.
    /// </summary>
    public static int Positive(int? configured, int fallback) =>
        configured is > 0 ? configured.Value : fallback;

    /// <summary>Wie viele Anfragen gleichzeitig ans Modell dürfen (es bedient seriell).</summary>
    public SemaphoreSlim Slots { get; }

    public int PerVisitorLimit => _perVisitor;
    public int DailyLimit => _daily;

    public string Fingerprint(string address)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(address));
        return Convert.ToBase64String(mac, 0, 16);
    }

    /// <summary>
    /// Prüft, ohne zu verbrauchen — damit eine abgewiesene Anfrage billig bleibt und
    /// eine kaputte Anfrage kein Kontingent kostet.
    /// </summary>
    public ChatVerdict Check(string key)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            RollDay(now);
            if (_dayCount >= _daily)
            {
                return ChatVerdict.DailyLimit;
            }
            if (!_hits.TryGetValue(key, out var list))
            {
                return ChatVerdict.Allowed;
            }
            list.RemoveAll(h => now - h > Window);
            if (list.Count == 0)
            {
                _hits.Remove(key);
                return ChatVerdict.Allowed;
            }
            return list.Count >= _perVisitor ? ChatVerdict.VisitorLimit : ChatVerdict.Allowed;
        }
    }

    /// <summary>
    /// Verbraucht ein Kontingent. Aufzurufen <b>unmittelbar vor</b> dem Modellaufruf.
    /// Zwischen <see cref="Check"/> und hier passen bei gleichzeitigen Anfragen bis zu
    /// <c>MaxConcurrent</c> durch — mehr nicht, und das ist die Größe, die die
    /// Parallelgrenze ohnehin begrenzt.
    /// </summary>
    public void Consume(string key)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            RollDay(now);
            _dayCount++;
            if (!_hits.TryGetValue(key, out var list))
            {
                if (_hits.Count >= MaxKeys)
                {
                    EvictOldest();
                }
                list = _hits[key] = [];
            }
            list.RemoveAll(h => now - h > Window);
            list.Add(now);
        }
    }

    private void RollDay(DateTimeOffset now)
    {
        var today = LocalDay(now);
        if (today != _day)
        {
            _day = today;
            _dayCount = 0;
        }
    }

    /// <summary>
    /// Der Tag des Tagesdeckels in deutscher Zeit. Der Neustart-Reset ist bekannt und
    /// gewollt: das ist ein Missbrauchsschutz, kein Wirtschaftlichkeitszähler.
    /// </summary>
    private static DateOnly LocalDay(DateTimeOffset now) =>
        Zone is null
            ? DateOnly.FromDateTime(now.UtcDateTime)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

    private static TimeZoneInfo? ResolveZone()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                // Weiter zur nächsten Schreibweise; am Ende bleibt UTC.
            }
        }
        return null;
    }

    /// <summary>
    /// Ältesten Eintrag verwerfen, wenn das Wörterbuch voll ist. Aufgerufen nur im
    /// Ausnahmefall (mehr als <see cref="MaxKeys"/> verschiedene Adressen im Fenster).
    /// </summary>
    private void EvictOldest()
    {
        // Erst suchen, dann entfernen: ein Remove mitten in der Schleife wäre je nach
        // Laufzeit erlaubt oder nicht — darauf soll sich hier nichts verlassen.
        string? emptyKey = null;
        string? oldestKey = null;
        var oldestHit = DateTimeOffset.MaxValue;
        foreach (var (key, list) in _hits)
        {
            if (list.Count == 0)
            {
                emptyKey ??= key;
                continue;
            }
            if (list[^1] < oldestHit)
            {
                oldestHit = list[^1];
                oldestKey = key;
            }
        }
        if (emptyKey is not null || oldestKey is not null)
        {
            _hits.Remove(emptyKey ?? oldestKey!);
        }
    }
}
