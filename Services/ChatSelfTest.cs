namespace Portfolio.Services;

/// <summary>
/// Der Selbsttest für alles am KI-Chat, was <b>ohne Netz und ohne Modell</b> prüfbar
/// ist: die Antwortprüfung, die drei Deckel und der Adress-Abdruck.
///
/// Aufruf: <c>dotnet Portfolio.dll --selftest-chat</c> — Rückgabe 0 (ok) oder 1 mit
/// den fehlgeschlagenen Punkten.
///
/// Warum im Programm und nicht in einem Testprojekt: dieses Repo hat bewusst <b>keine</b>
/// externen Pakete, und ein Testprojekt bräuchte xunit. Der Hausbrauch ist ohnehin der
/// Schalter im Werkzeug selbst (<c>meter.py --selftest</c>, <c>tools/ui-check/*.js</c>).
/// Der Preis steht auch hier: das ist ein Programmaufruf, <b>kein CI-Gate</b>.
///
/// Was hier <b>nicht</b> geprüft wird und nicht geprüft werden kann: ob das Modell ein
/// Thema einhält. Geprüft wird der Codeblock-Fang und die Absage — nicht die Absicht.
/// </summary>
internal static class ChatSelfTest
{
    private const string Visitor = "203.0.113.7";
    private const string Other = "198.51.100.9";

    public static int Run(bool dumpPrompt = false)
    {
        if (dumpPrompt)
        {
            Console.WriteLine("=== Systemprompt ===");
            Console.WriteLine(ChatService.SystemPrompt);
            Console.WriteLine("=== Ende ===");
        }

        var failures = new List<string>();

        void Check(bool condition, string label)
        {
            if (!condition)
            {
                failures.Add(label);
            }
        }

        StripCodeChecks(Check);
        LimitChecks(Check);
        FingerprintChecks(Check);

        if (failures.Count > 0)
        {
            Console.WriteLine("SELFTEST CHAT FEHLGESCHLAGEN:");
            foreach (var failure in failures)
            {
                Console.WriteLine($"  - {failure}");
            }
            return 1;
        }
        Console.WriteLine("selftest-chat ok — Codeblock-Fang, Absage, drei Deckel, Adress-Abdruck");
        return 0;
    }

    private static void StripCodeChecks(Action<bool, string> check)
    {
        var refusal = ChatService.Refusal;

        // --- Zaun mit Sprachmarke, Prosa davor und danach -----------------
        var fenced = ChatService.StripCode(
            "Bastian hat das in C# gebaut.\n\n" +
            "```csharp\nvar x = 1;\nvar y = 2;\n```\n\n" +
            "Er nutzt dafür ASP.NET Core.");
        check(fenced.Contains("Bastian hat das in C# gebaut."), "Zaun: Prosa davor verloren");
        check(fenced.Contains("Er nutzt dafür ASP.NET Core."), "Zaun: Prosa danach verloren");
        check(!fenced.Contains("```"), "Zaun: Backticks überlebt");
        check(!fenced.Contains("var x"), "Zaun: Code überlebt");

        // --- Zaun ohne Sprachmarke ----------------------------------------
        var plain = ChatService.StripCode(
            "Das ist der Weg, den er gewählt hat.\n```\nSELECT 1;\n```\nUnd das war Absicht.");
        check(plain.Contains("Das ist der Weg"), "Zaun ohne Marke: Prosa verloren");
        check(!plain.Contains("SELECT 1"), "Zaun ohne Marke: Code überlebt");

        // --- Antwort besteht nur aus einem Block → genau die Absage -------
        check(ChatService.StripCode("```\nSELECT 1;\n```") == refusal,
            "nur Block: nicht die Absage");

        // --- Eingerückter Block -------------------------------------------
        var indented = ChatService.StripCode(
            "Bastian hat die Oberfläche in WPF gebaut.\n\n" +
            "    var a = 1;\n    var b = 2;\n\n" +
            "Das war aufwendiger als gedacht.");
        check(indented.Contains("Bastian hat die Oberfläche in WPF gebaut."), "Einrückung: Prosa davor verloren");
        check(indented.Contains("Das war aufwendiger als gedacht."), "Einrückung: Prosa danach verloren");
        check(!indented.Contains("var a"), "Einrückung: Code überlebt");

        // --- Kein Code → unverändert --------------------------------------
        const string prose = "Bastian arbeitet mit C# und ASP.NET Core.";
        check(ChatService.StripCode(prose) == prose, "Prosa ohne Code wurde verändert");

        // Einzelne Backticks sind ein Technik-Name, kein Code. Der wichtigste
        // Fehlalarm, den die Prüfung nicht produzieren darf.
        const string inline = "Er nutzt `ASP.NET Core` und `MySQL`.";
        check(ChatService.StripCode(inline) == inline, "einzelne Backticks wurden entfernt");

        // --- Kurze, vollständige Antwort ohne Code bleibt stehen ----------
        //
        // Genau die Gegenprobe, die die Längenschwelle davor bewahrt, eine korrekte
        // knappe Antwort durch die Absage zu ersetzen. Ohne sie wäre der Chat für
        // Ein-Satz-Antworten kaputt, ohne dass es auffiele.
        const string short_ = "Ja, das hat er gebaut.";
        check(ChatService.StripCode(short_) == short_, "kurze Antwort ohne Code wurde ersetzt");

        // --- Ein Zaun, der nie schließt -----------------------------------
        //
        // Unsicherer Fall: lieber zu viel wegnehmen als Code ausgeben.
        var open = ChatService.StripCode("Kurz:\n```csharp\nvar a = 1;");
        check(!open.Contains("var a"), "offener Zaun: Code überlebt");
        check(open == refusal, "offener Zaun: nicht die Absage");

        check(ChatService.StripCode("") == refusal, "leere Antwort: nicht die Absage");
    }

    private static void LimitChecks(Action<bool, string> check)
    {
        // --- Fail-closed: unsinnige Konfiguration heißt Vorgabe ------------
        check(ChatLimits.Positive(null, 8) == 8, "Positive(null) nicht fail-closed");
        check(ChatLimits.Positive(0, 8) == 8, "Positive(0) nicht fail-closed");
        check(ChatLimits.Positive(-5, 8) == 8, "Positive(negativ) nicht fail-closed");
        check(ChatLimits.Positive(5, 8) == 5, "Positive(5) verworfen");

        // --- Je Besucher: 8 erlaubt, der 9. nicht --------------------------
        var clock = new FakeClock();
        var limits = new ChatLimits(8, 300, 2, clock);
        var key = limits.Fingerprint(Visitor);
        for (var i = 0; i < 8; i++)
        {
            check(limits.Check(key) == ChatVerdict.Allowed, $"Anfrage {i + 1} fälschlich gesperrt");
            limits.Consume(key);
        }
        check(limits.Check(key) == ChatVerdict.VisitorLimit, "die 9. Anfrage war nicht gesperrt");

        // Prüfen darf nicht verbrauchen.
        for (var i = 0; i < 20; i++)
        {
            limits.Check(key);
        }
        check(limits.Check(key) == ChatVerdict.VisitorLimit, "Prüfen hat Kontingent verbraucht");

        // --- Nach Ablauf des Fensters wieder erlaubt -----------------------
        clock.Advance(TimeSpan.FromMinutes(11));
        check(limits.Check(key) == ChatVerdict.Allowed, "Fenster lief ab, blieb aber gesperrt");

        // --- Getrennte Töpfe je Adresse ------------------------------------
        var perKey = new ChatLimits(2, 300, 2, new FakeClock());
        var keyA = perKey.Fingerprint(Visitor);
        var keyB = perKey.Fingerprint(Other);
        perKey.Consume(keyA);
        perKey.Consume(keyA);
        check(perKey.Check(keyA) == ChatVerdict.VisitorLimit, "Topf A nicht gesperrt");
        check(perKey.Check(keyB) == ChatVerdict.Allowed, "Topf B hing am Topf A");

        // --- Tagesdeckel sperrt alle ---------------------------------------
        var daily = new ChatLimits(100, 2, 2, new FakeClock());
        check(daily.DailyLimit == 2, "Tagesdeckel nicht übernommen");
        var dailyA = daily.Fingerprint(Visitor);
        var dailyB = daily.Fingerprint(Other);
        daily.Consume(dailyA);
        daily.Consume(dailyB);
        check(daily.Check(dailyA) == ChatVerdict.DailyLimit, "Tagesdeckel griff nicht");
        check(daily.Check(dailyB) == ChatVerdict.DailyLimit, "Tagesdeckel sperrte den zweiten nicht");
        var fresh = daily.Fingerprint("192.0.2.200");
        check(daily.Check(fresh) == ChatVerdict.DailyLimit, "Tagesdeckel sperrte einen neuen Besucher nicht");

        // --- Und um Mitternacht wieder frei --------------------------------
        var rollover = new FakeClock();
        var nextDay = new ChatLimits(100, 1, 2, rollover);
        var rollKey = nextDay.Fingerprint(Visitor);
        nextDay.Consume(rollKey);
        check(nextDay.Check(rollKey) == ChatVerdict.DailyLimit, "Tagesdeckel griff nicht");
        rollover.Advance(TimeSpan.FromDays(1));
        check(nextDay.Check(rollKey) == ChatVerdict.Allowed, "Tagesdeckel blieb über Mitternacht stehen");

        // --- Parallelgrenze -------------------------------------------------
        var slots = new ChatLimits(8, 300, 2, new FakeClock()).Slots;
        check(slots.Wait(TimeSpan.Zero), "erster Platz nicht vergeben");
        check(slots.Wait(TimeSpan.Zero), "zweiter Platz nicht vergeben");
        check(!slots.Wait(TimeSpan.Zero), "dritter Platz wurde vergeben — Grenze ist nicht 2");
        slots.Release();
        check(slots.Wait(TimeSpan.Zero), "Platz nach Freigabe nicht wieder vergeben");
    }

    private static void FingerprintChecks(Action<bool, string> check)
    {
        var limits = new ChatLimits(8, 300, 2, new FakeClock());
        var a = limits.Fingerprint(Visitor);
        var b = limits.Fingerprint(Visitor);
        var c = limits.Fingerprint(Other);

        check(a == b, "gleiche Adresse ergab verschiedene Schlüssel");
        check(a != c, "verschiedene Adressen ergaben denselben Schlüssel");

        // Die prüfbare Datenschutz-Zusage: der Zustand trägt einen Abdruck, nicht die
        // Adresse. Wäre der Schlüssel die Adresse, stünde sie im Arbeitsspeicher.
        check(a != Visitor, "der Schlüssel ist die Adresse selbst");
        check(!a.Contains(Visitor, StringComparison.Ordinal), "der Schlüssel enthält die Adresse");
        check(!a.Contains("203.0.113", StringComparison.Ordinal), "der Schlüssel enthält Adressteile");
    }

    /// <summary>Eine steuerbare Uhr — deshalb ist <see cref="TimeProvider"/> injiziert.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
