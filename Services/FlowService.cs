using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Portfolio.Services;

public sealed record FlowSample(string From, string To, int Port, string Proto, long Bytes, long Packets);

public sealed record FlowWindow(string Node, DateTimeOffset From, DateTimeOffset To,
                                List<FlowSample> Flows, int Dropped, int Truncated, int Rejected);

public sealed record FlowRate(string From, string To, string Service, long BytesPerSecond, long?[] Spark);

public sealed record FlowResponse(DateTimeOffset UpdatedAt, bool Stale, int WindowSeconds,
                                  List<string> MeasuredNodes, List<FlowRate> Flows);

/// <summary>
/// Hält die echten Traffic-Raten, die die Messpunkte auf den Knoten pushen.
///
/// Der Ring besteht aus Wanduhr-Slots à 45 s — der Meter richtet seine Fenster auf
/// Vielfache von 45 s aus, damit drei Knoten ohne Absprache im selben Slot landen.
/// Ein Doppel-Push desselben Knotens <b>ersetzt</b> seinen Beitrag, er addiert nicht;
/// sonst verdoppelt ein Wiederholversuch die Rate — und zwar unauffällig.
///
/// Grundsatz wie beim Meter: hier stehen nur <b>Namen und Dienst-Labels</b>. Ports
/// werden sofort zu Labels, IPs kommen im Payload gar nicht erst vor.
///
/// Die eine Regel, an der alles hängt: <b>eine 0 darf nur erscheinen, wo wirklich
/// gemessen wurde</b>. Fehlt ein Paar in einem Slot, ist 0 nur dann die Wahrheit,
/// wenn <i>alle</i> erwarteten Messpunkte dieses Fenster sauber gemeldet haben.
/// Sonst ist es <c>null</c> — „nicht gemessen" ist etwas anderes als „gemessen, still".
/// </summary>
public sealed class FlowService(IConfiguration config, ILogger<FlowService> logger,
                                TimeProvider clock, IMemoryCache cache)
{
    public const int WindowSeconds = 45;
    public const int WindowCount = 20;          // 15 Minuten Verlauf
    /// <summary>Fenster weiter als ±2 Slots von der Wanduhr weg sind keine Live-Daten.</summary>
    private const int ToleranceSlots = 2;
    /// <summary>Obergrenze wie im Meter (<c>flows.MAX_FLOWS</c>): was der Meter
    /// deckelt, darf die App nicht strenger ablehnen — sonst kostet ein
    /// verkehrsreiches Fenster die ganze Messung statt nur der Ränder.</summary>
    private const int MaxFlowsPerWindow = 400;
    /// <summary>Höchstens so viele Knotennamen je Slot. Ohne Deckel wäre das die
    /// einzige Struktur ohne Obergrenze: ein <c>flows: []</c>-Push ist gültig, legt
    /// aber keinen einzigen Schlüssel im Ring an und käme deshalb an jedem Ventil
    /// vorbei — nur der Knotenname bliebe hängen.</summary>
    private const int MaxNodesPerSlot = 16;
    /// <summary>Plausibilitätsschranke je Eintrag. Eine 45-s-Fenster-Menge kann auch
    /// auf 10 GbE keine Terabyte sein; ohne Deckel könnte eine Summe überlaufen und
    /// als <b>negative</b> Rate in der öffentlichen Antwort landen.</summary>
    private const long MaxBytesPerWindow = 1_000_000_000_000;
    /// <summary>Sicherheitsventil gegen unbegrenztes Wachstum. Rechnerisch normal:
    /// 20 Slots × 3 Knoten × ~120 Paare ≈ 7 200 Schlüssel (≈ 1 MB). Der Wert liegt
    /// bewusst weit darüber — er ist ein Notaus, keine Betriebsgrenze. Er wird
    /// <b>nach</b> dem Aufräumen geprüft, sonst könnte sich ein voller Ring nie
    /// wieder erholen.</summary>
    private const int MaxTotalKeys = 50_000;
    /// <summary>Höchstzahl der Zeilen in der öffentlichen Antwort. Der Ring ist auf
    /// 50 000 Paare gedeckelt — alle auszuliefern wären ~12 MB je Anfrage, und
    /// <c>GET /api/flows</c> ist unauthentifiziert. Erwartet werden 20–80 aktive
    /// Paare; 250 ist der dreifache Worst Case.</summary>
    private const int MaxRows = 250;
    /// <summary>Der Snapshot ändert sich nur alle 45 s — kurz zwischenspeichern, damit
    /// der öffentliche Endpunkt nicht pro Anfrage den ganzen Ring durchrechnet.
    /// 10 s ist gegenüber der Datenauflösung unsichtbar.</summary>
    private const int CacheSeconds = 10;

    /// <summary>Namen für alles, was über die Leitung geht. <c>\z</c> statt <c>$</c>:
    /// <c>$</c> trifft in .NET auch <b>vor</b> einem abschließenden <c>\n</c> — ein
    /// Name mit Zeilenumbruch käme durch und könnte eine Log-Zeile fälschen.
    /// Länge 63 wie ein DNS-Label; die früheren 41 hätten lange LXC-Hostnamen still
    /// verworfen.</summary>
    private static readonly Regex NamePattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,62}\z", RegexOptions.Compiled);
    /// <summary>Eine Adresse, die als Name durchginge: <c>192.168.100.24</c> erfüllt die
    /// Namensregel, weil Ziffern und Punkte darin erlaubt sind. Sie darf trotzdem
    /// niemals als Name in die öffentliche Antwort — die Seite sagt „keine IPs" zu,
    /// und das muss auch dann gelten, wenn ein Push mit gültigem Secret Unsinn
    /// behauptet. Bewusst nur der vierteilige Ziffernblock: ein Gast-Hostname wie
    /// `111` oder `CT111` soll nicht fälschlich als Adresse gelten.</summary>
    private static readonly Regex DottedQuad = new(@"^\d{1,3}(\.\d{1,3}){3}\z", RegexOptions.Compiled);
    private static readonly HashSet<string> Protos = ["tcp", "udp", "icmp", "other"];

    /// <summary>Wohlbekannte Ports. Nur was hier steht, bekommt einen Namen — alles
    /// andere heißt ehrlich <c>sonstiges</c> statt geraten zu werden.</summary>
    private static readonly Dictionary<int, string> LabelsByPort = new()
    {
        [22] = "ssh", [25] = "mail", [53] = "dns", [67] = "dhcp", [68] = "dhcp",
        [80] = "web", [111] = "rpcbind", [137] = "netbios", [138] = "netbios",
        [443] = "web", [445] = "smb", [587] = "mail", [993] = "mail",
        [2049] = "nfs", [3128] = "spice", [3306] = "mysql", [5353] = "mdns",
        [5404] = "corosync", [5405] = "corosync", [6379] = "redis", [8006] = "pve",
        [9090] = "prometheus", [9100] = "node-exporter", [11434] = "ollama",
        [25565] = "minecraft",
    };

    private readonly record struct Pair(string From, string To, string Service);

    //: Slot → Knoten → (Paar → Bytes). Der Knoten-Beitrag ist eine <b>eigene Map</b>,
    //: die beim nächsten Push per Referenz ausgetauscht wird. Ein „erst löschen, dann
    //: addieren" wäre bei zwei gleichzeitigen Pushes desselben Knotens nicht atomar
    //: und würde genau das verdoppeln, was die Ersetzung verhindern soll.
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, ConcurrentDictionary<Pair, long>>> _slots = new();
    //: Slot → Knoten → Flag (1 = sauber gemeldet, 2 = mit Verlust/Verwurf)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, byte>> _nodesBySlot = new();
    private readonly ConcurrentDictionary<string, byte> _warnedPorts = new();

    /// <summary>Log-Bremse: ein abgelehnter Push darf das Log nicht fluten. Eine
    /// Meldung je Schlüssel und Minute zeigt den Fall — und die 100-Byte-Anfrage,
    /// die 1000 Meldungen erzeugt, ist selbst das Problem.</summary>
    public static class Throttle
    {
        private static readonly ConcurrentDictionary<string, long> Last = new();

        public static bool TryPass(string key, TimeProvider clock)
        {
            var minute = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
            if (Last.TryGetValue(key, out var seen) && seen == minute) return false;
            // Bei einem Gleichstand zweier Threads entsteht höchstens eine doppelte
            // Meldung. Das ist eine Bremse, kein Sicherheitsmerkmal.
            Last[key] = minute;
            return true;
        }
    }

    /// <summary>Bekannte Ports → Dienst-Label. Rein und ohne Nebenwirkung, damit prüfbar.</summary>
    public static string ServiceLabel(int port, string proto)
    {
        if (LabelsByPort.TryGetValue(port, out var label)) return label;
        // ICMP hat keine Ports — ohne diesen Fall bliebe jede Ping-Zeile `sonstiges`.
        if (port == 0 && proto == "icmp") return "icmp";
        return "sonstiges";
    }

    public static bool IsKnownPort(int port, string proto) => LabelsByPort.ContainsKey(port);

    /// <summary>Ein Name ist nur brauchbar, wenn er die Zeichenregel erfüllt und
    /// keine IP ist. Für Knoten, Absender und Empfänger gleichermaßen.</summary>
    public static bool NameOk(string? name) =>
        name is not null && NamePattern.IsMatch(name) && !DottedQuad.IsMatch(name);

    /// <summary>
    /// Die Knoten, die messen <b>sollen</b> — aus <c>Flows:Nodes</c>. Daran hängt die
    /// Null-Regel: nur wenn alle diese Knoten ein Fenster sauber gemeldet haben, ist
    /// das Fehlen eines Paares eine echte Null. Deshalb muss die Liste wachsen, wenn
    /// ein Messpunkt dazukommt: steht dort nur <c>prox1</c>, während drei Knoten
    /// pushen, liefert sie nie eine Null (die Anzeige bleibt bei `—`) — das ist die
    /// vorsichtige Richtung, aber sie verschenkt Information.
    /// </summary>
    public static List<string> ConfiguredNodes(IConfiguration config)
    {
        var raw = config["Flows:Nodes"];
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(n => n.ToLowerInvariant())];
    }

    /// <summary>
    /// Erlaubte Knotennamen — <b>fail-closed</b>. Ohne Liste ist nicht entscheidbar,
    /// ob ein Push echt ist: der Knotenname bestimmt, wer ein Paar zählt, und die
    /// Null-Regel hängt daran. Ist er frei wählbar, kann ein Push mit gültigem Secret
    /// sich als zweiter Knoten ausgeben und dieselben Bytes ein zweites Mal zählen.
    /// Die Liste ist damit keine optionale Härtung, sondern die einzige Instanz, die
    /// den Namen bestätigt — genau wie das Secret für die Herkunft.
    /// </summary>
    public bool IsAllowedNode(string node)
    {
        var allowed = ConfiguredNodes(config);
        return allowed.Count > 0 && allowed.Contains(node, StringComparer.Ordinal);
    }

    //: long, nicht int: die Slot-Nummer wächst mit der Zeit über int.MaxValue hinaus,
    //: und ein unchecked-Cast könnte auf int.MinValue kippen — Math.Abs darauf wirft.
    private static long SlotOf(DateTimeOffset t) => t.ToUnixTimeSeconds() / WindowSeconds;

    // ------------------------------------------------------------------ Eingang

    /// <summary>
    /// Prüft einen Push. Strukturell kaputte Eingaben werden abgelehnt, einzelne
    /// unbrauchbare Einträge nur <b>verworfen und gezählt</b> — ein einzelner
    /// Ausreißer darf nicht das ganze Fenster kosten.
    /// </summary>
    public static bool TryParseWindow(JsonElement root, TimeProvider clock,
                                      out FlowWindow window, out string error)
    {
        window = null!;
        error = "";

        if (root.ValueKind != JsonValueKind.Object) { error = "kein objekt"; return false; }

        var node = Str(root, "node");
        // Der Name wird hier einmal kanonisiert (klein). Sonst wären `prox1` und
        // `PROX1` zwei verschiedene Knoten: die Erlaubnisliste prüft
        // case-insensitiv, der Ring unterscheidet sie aber — derselbe Push zweimal
        // gesendet hätte jede Rate verdoppelt. `Pair` vergleicht ordinal, deshalb
        // muss die Kanonisierung vor dem ersten Schlüssel passieren.
        if (!NameOk(node)) { error = "node fehlt oder unbrauchbar"; return false; }
        node = node!.ToLowerInvariant();

        if (!root.TryGetProperty("window", out var winEl) || winEl.ValueKind != JsonValueKind.Object)
        { error = "window fehlt"; return false; }

        var style = System.Globalization.DateTimeStyles.AdjustToUniversal;
        if (!DateTimeOffset.TryParse(Str(winEl, "from"), null, style, out var from) ||
            !DateTimeOffset.TryParse(Str(winEl, "to"), null, style, out var to))
        { error = "window nicht lesbar"; return false; }
        if (to <= from) { error = "window verdreht"; return false; }

        // Die Länge muss stimmen: die Rate wird durch WindowSeconds geteilt. Ein
        // Meter mit `--window 30` würde sonst still um ein Drittel zu niedrige
        // Zahlen liefern — lieber laut ablehnen als leise falsch rechnen.
        if ((to - from).TotalSeconds != WindowSeconds)
        { error = $"window ist {(to - from).TotalSeconds:0.#}s statt {WindowSeconds}s"; return false; }

        // Ein Fenster, das nicht in die Nähe der Wanduhr gehört, ist keine Live-Messung.
        // Die Subtraktion zweier long-Slots kann hier nicht überlaufen: der
        // Parse-Bereich endet 9999, der Abstand bleibt weit unter long.MaxValue.
        var drift = Math.Abs(SlotOf(from) - SlotOf(clock.GetUtcNow()));
        if (drift > ToleranceSlots) { error = $"window {drift} slots neben der uhr"; return false; }

        if (!root.TryGetProperty("flows", out var flowsEl) || flowsEl.ValueKind != JsonValueKind.Array)
        { error = "flows fehlt"; return false; }
        if (flowsEl.GetArrayLength() > MaxFlowsPerWindow) { error = "zu viele flüsse"; return false; }

        var flows = new List<FlowSample>();
        var rejected = 0;
        foreach (var el in flowsEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) { rejected++; continue; }
            var fromName = Str(el, "from");
            var toName = Str(el, "to");
            var proto = Str(el, "proto");
            if (!NameOk(fromName) || !NameOk(toName) || proto is null || !Protos.Contains(proto))
            { rejected++; continue; }

            if (!TryLong(el, "port", out var port) || port is < 0 or > 65535 ||
                !TryLong(el, "bytes", out var bytes) || bytes is < 0 or > MaxBytesPerWindow ||
                !TryLong(el, "packets", out var packets) || packets is < 0 or > MaxBytesPerWindow)
            { rejected++; continue; }

            flows.Add(new FlowSample(fromName!, toName!, (int)port, proto, bytes, packets));
        }

        window = new FlowWindow(node, from, to, flows,
                                Count(root, "dropped"), Count(root, "truncated"), rejected);
        return true;
    }

    /// <summary>Zeichenkette lesen, <b>ohne zu werfen</b>: <c>GetString()</c> wirft eine
    /// <c>InvalidOperationException</c>, wenn das Feld eine Zahl oder ein Objekt ist
    /// — die fängt der Endpunkt nicht, und aus einem 400 würde ein 500 samt
    /// Stacktrace im Log.</summary>
    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    /// <summary>Zähler lesen, ohne zu werfen: <c>GetInt32</c> würde bei <c>1.5</c> oder
    /// <c>1e30</c> eine <c>FormatException</c> werfen. Nicht ganze Zahlen sind keine
    /// Zähler — sie werden als 0 gelesen, nicht als Fehler behandelt.</summary>
    private static int Count(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return 0;
        return el.TryGetInt64(out var value) ? (int)Math.Clamp(value, 0, int.MaxValue) : 0;
    }

    private static bool TryLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
               && el.TryGetInt64(out value);
    }

    public void Ingest(FlowWindow window)
    {
        // Erst aufräumen, dann prüfen: stünde das Ventil davor, könnte ein einmal
        // voller Ring nie wieder ablaufen und die Messung bliebe dauerhaft stumm.
        Evict(SlotOf(clock.GetUtcNow()));
        if (TotalKeys() > MaxTotalKeys)
        {
            if (Throttle.TryPass("ring-voll", clock))
                logger.LogWarning("flow-meter: ring über {Max} schlüssel — fenster von '{Node}' verworfen",
                                  MaxTotalKeys, window.Node);
            return;
        }

        var slot = SlotOf(window.From);
        var nodes = _nodesBySlot.GetOrAdd(slot, _ => new ConcurrentDictionary<string, byte>());
        // Deckel erst prüfen, dann schreiben: sonst legt ein leerer Push den Namen
        // an, ohne dass ihn irgendein Ventil sieht.
        if (nodes.Count >= MaxNodesPerSlot && !nodes.ContainsKey(window.Node))
        {
            if (Throttle.TryPass("knoten-voll", clock))
                logger.LogWarning("flow-meter: {Max} knoten in einem slot — fenster von '{Node}' verworfen",
                                  MaxNodesPerSlot, window.Node);
            return;
        }

        foreach (var flow in window.Flows)
            if (flow.Port > 0 && !IsKnownPort(flow.Port, flow.Proto))
                WarnPortOnce(flow.Port, flow.Proto);

        if (window.Dropped > 0)
            logger.LogWarning("flow-meter {Node}: {Dropped} pakete vom kernel verworfen",
                              window.Node, window.Dropped);
        if (window.Truncated > 0)
            logger.LogWarning("flow-meter {Node}: {Truncated} flüsse abgeschnitten",
                              window.Node, window.Truncated);
        if (window.Rejected > 0)
            logger.LogWarning("flow-meter {Node}: {Rejected} einträge verworfen",
                              window.Node, window.Rejected);

        // Beitrag dieses Knotens vollständig ersetzen — per Referenz, atomar.
        var contribution = new ConcurrentDictionary<Pair, long>();
        foreach (var flow in window.Flows)
        {
            var pair = new Pair(flow.From, flow.To, ServiceLabel(flow.Port, flow.Proto));
            contribution.AddOrUpdate(pair, flow.Bytes, (_, old) => old + flow.Bytes);
        }
        _slots.GetOrAdd(slot, _ => new ConcurrentDictionary<string, ConcurrentDictionary<Pair, long>>())
              [window.Node] = contribution;

        // Der Knotenmarker kommt NACH den Daten. Ein gleichzeitiger Leser, der die
        // Daten schon sieht, den Marker aber noch nicht, hält den Slot für
        // unvollständig und schreibt `null` — die vorsichtige Richtung. Umgekehrt
        // entstünde eine 0 für etwas, das nie gemessen wurde.
        var lossy = window.Dropped > 0 || window.Truncated > 0 || window.Rejected > 0;
        nodes[window.Node] = lossy ? (byte)2 : (byte)1;
    }

    private int TotalKeys()
    {
        var total = 0;
        foreach (var slot in _slots.Values)
            foreach (var node in slot.Values)
                total += node.Count;
        // Die Knotenmarker zählen mit: sie sind die Struktur, die ein leerer Push
        // ohne jeden Fluss anlegt — und damit der einzige Weg am Ventil vorbei.
        foreach (var slot in _nodesBySlot.Values) total += slot.Count;
        return total;
    }

    /// <summary>Räumt nach der <b>Wanduhr</b> auf, nicht nach dem Slot des eingehenden
    /// Fensters: eine innerhalb der Toleranz schnelle Knotenuhr dürfte sonst Slots
    /// löschen, die noch im Verlaufsfenster liegen — gemessene Werte würden zu
    /// „nie gemessen".</summary>
    private void Evict(long nowSlot)
    {
        var oldest = nowSlot - WindowCount + 1;
        foreach (var slot in _slots.Keys)
            if (slot < oldest) _slots.TryRemove(slot, out _);
        foreach (var slot in _nodesBySlot.Keys)
            if (slot < oldest) _nodesBySlot.TryRemove(slot, out _);
    }

    /// <summary>Unbekannte Ports einmal je Port melden. <c>LogWarning</c>, nicht
    /// <c>LogInformation</c> — die App steht auf <c>Default: Warning</c>.</summary>
    private void WarnPortOnce(int port, string proto)
    {
        // Die Menge ist gedeckelt: sie wächst sonst mit jedem je gesehenen Port
        // und wäre neben dem Ring die einzige Struktur ohne Obergrenze.
        if (_warnedPorts.Count > 4096) _warnedPorts.Clear();
        if (_warnedPorts.TryAdd($"{port}/{proto}", 0))
            logger.LogWarning("unbekannter port {Port}/{Proto} → sonstiges", port, proto);
    }

    // ----------------------------------------------------------------- Ausgang

    /// <summary>Zwischengespeichert (10 s), weil nur der öffentliche Endpunkt hier
    /// anfragt und sich der Ring ohnehin nur alle 45 s ändert.</summary>
    public FlowResponse Snapshot() => cache.GetOrCreate("flows", entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheSeconds);
        return Compute();
    })!;

    private FlowResponse Compute()
    {
        var now = clock.GetUtcNow();
        // Das jüngste *abgeschlossene* Fenster ist der Slot vor dem laufenden.
        var newestSlot = SlotOf(now) - 1;
        var oldestSlot = newestSlot - WindowCount + 1;

        var activeNodes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in _nodesBySlot)
            if (entry.Key >= oldestSlot && entry.Key <= newestSlot)
                foreach (var node in entry.Value.Keys) activeNodes.Add(node);

        long? newestPush = null;
        foreach (var slot in _nodesBySlot.Keys)
            if (slot <= newestSlot && (newestPush is null || slot > newestPush)) newestPush = slot;

        // Bei gesundem Betrieb ist `newestPush == newestSlot`: der Meter pusht das
        // eben abgeschlossene Fenster genau an der Grenze. EIN verpasster Push
        // (tcpdump startet neu und verwirft dabei ein Teilfenster) ist normal und
        // darf die Anzeige nicht auf `keine daten` werfen — ZWEI verpasste nicht
        // mehr: dann ist die jüngste Zahl ~2 Minuten alt und als „live" zu gelten
        // wäre eine Falschaussage.
        var stale = newestPush is null || newestPush < newestSlot - 1;
        var updatedAt = newestPush is null
            ? now
            : DateTimeOffset.FromUnixTimeSeconds((newestPush.Value + 1) * WindowSeconds);
        // Bei `stale` bleiben die Namen stehen: die UI schreibt dann zwar
        // `keine daten`, kann aber weiter sagen, *welcher* Messpunkt fehlt.
        // `updatedAt` ist nur aussagekräftig, solange `stale` false ist.
        if (stale) return new FlowResponse(updatedAt, true, WindowSeconds, [.. activeNodes], []);

        // Maßstab für „vollständig gemeldet": die konfigurierten Messpunkte. Wer
        // nicht konfiguriert ist, pusht ohnehin nicht (fail-closed) — die Liste ist
        // deshalb zugleich die Wahrheit über die Messpunkte.
        var expected = ConfiguredNodes(config);

        // Erster Durchgang: je Slot die Summe über alle Knoten und die Antwort auf
        // „haben *alle* erwarteten Knoten dieses Fenster sauber gemeldet?".
        var sumsBySlot = new Dictionary<Pair, long>?[WindowCount];
        var completeBySlot = new bool[WindowCount];
        var pairs = new HashSet<Pair>();
        for (var i = 0; i < WindowCount; i++)
        {
            var slot = oldestSlot + i;
            var byNode = _slots.TryGetValue(slot, out var b) ? b : null;
            var nodes = _nodesBySlot.TryGetValue(slot, out var n) ? n : null;
            if (byNode is null && nodes is null) continue;

            var sums = new Dictionary<Pair, long>();
            if (byNode is not null)
                foreach (var contribution in byNode.Values)
                    foreach (var entry in contribution)
                        sums[entry.Key] = sums.TryGetValue(entry.Key, out var old)
                            ? old + entry.Value : entry.Value;

            sumsBySlot[i] = sums;
            // `== 1` heißt: sauber gemeldet. Ein Fenster mit Kernel-Verlust, mit
            // abgeschnittenen oder verworfenen Einträgen ist unvollständig — sein
            // Fehlen darf keine 0 ergeben, denn wir wissen nicht, was gefehlt hat.
            completeBySlot[i] = expected.Count > 0 &&
                expected.All(name => nodes is not null && nodes.TryGetValue(name, out var flag) && flag == 1);
            foreach (var pair in sums.Keys) pairs.Add(pair);
        }

        var rates = new List<FlowRate>();
        foreach (var pair in pairs)
        {
            var spark = new long?[WindowCount];
            long? latest = null;
            for (var i = 0; i < WindowCount; i++)
            {
                var sums = sumsBySlot[i];
                if (sums is null) continue;                     // kein Push → keine Messung
                // Der Meter sendet **rohe Fenster-Bytes**, der Vertrag dieser
                // Antwort ist Bytes/s. Ohne diese Division wäre jede angezeigte
                // Zahl um den Faktor 45 zu hoch — und zwar unauffällig, weil die
                // Größenordnung auf einer Live-Seite plausibel aussieht.
                if (sums.TryGetValue(pair, out var sum))
                {
                    var rate = sum / WindowSeconds;
                    spark[i] = rate;
                    latest = rate;
                }
                else if (completeBySlot[i]) { spark[i] = 0; latest = 0; }
            }
            // `latest` = die jüngste *echte* Messung dieses Paares. „Gemessen und
            // gerade still" (0) und „nie gemessen" (`null`) bleiben unterscheidbar.
            if (latest is null) continue;
            rates.Add(new FlowRate(pair.From, pair.To, pair.Service, latest.Value, spark));
        }
        rates.Sort((a, b) => b.BytesPerSecond.CompareTo(a.BytesPerSecond));

        return new FlowResponse(updatedAt, false, WindowSeconds, [.. activeNodes],
                                rates.Count > MaxRows ? rates.GetRange(0, MaxRows) : rates);
    }
}
