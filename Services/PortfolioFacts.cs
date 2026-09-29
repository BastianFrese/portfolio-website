namespace Portfolio.Services;

/// <summary>
/// Der belegte Faktenbestand des KI-Chats — <b>ausschließlich</b> Inhalte, die auf
/// der öffentlichen Seite selbst stehen (<c>Views/Home/Index.cshtml</c>: projekte,
/// infrastruktur, stack, kontakt).
///
/// Warum als eigene Datei: der Systemprompt war bisher die einzige Faktenquelle und
/// nannte die Projekte in einem Halbsatz. Damit der Chat über Kenntnisse und
/// Erfahrungen Auskunft geben kann, ohne zu erfinden, braucht er einen Bestand —
/// und der gehört an <b>eine</b> Stelle pflegbar, nicht in <c>appsettings.json</c>
/// (dort steht bewusst nichts Modellbezogenes).
///
/// Was hier <b>nicht</b> hineingehört: die Anschrift, die Erkrankung,
/// Bewerbungsinterna, Namen von Endkunden, Adressen oder Hostnamen des internen
/// Netzes. Damit bleibt die Zusage auf <c>/impressum</c> strukturell erfüllt — der
/// Chat kann nur sagen, was ohnehin auf der Seite steht.
///
/// <b>Pflegeregel:</b> ändert sich eine Zahl auf der Startseite, ändert sie sich
/// hier mit. Zwei Stellen, ein Fakt — das ist der Preis dafür, dass der Prompt
/// nicht aus HTML zusammengesucht werden muss.
/// </summary>
internal static class PortfolioFacts
{
    public const string Text = """
        Bastian Frese — angehender Fachinformatiker für Systemintegration.
        Kontakt: bastian@bastian-frese.de.
        WICHTIG zur Einordnung: Bastian ist noch NICHT in Ausbildung und hat
        keine abgeschlossene IT-Ausbildung. Er bewirbt sich auf einen
        Ausbildungsplatz. Eine Ausbildung ab dem 01.08.2027 ist möglich, ein
        Praktikum davor gern. Behaupte nicht, er sei bereits in Ausbildung oder
        habe schon Praktika absolviert.

        Sprachen: C# / .NET, Python, SQL, PowerShell / Bash, JavaScript.
        Frameworks: ASP.NET Core MVC, WPF, EF Core, FastAPI, ASP.NET Core Identity.
        Daten: MySQL, SQLite, EF-Core-Migrationen.
        Betrieb: Proxmox, Nginx, Cloudflare Tunnel, GitHub Actions, Ansible,
        Docker Compose, systemd, Grafana / Prometheus / Loki.
        Tests: xUnit, CI-Pipelines.

        PROJEKTE
        1. ERDI's Racing Community — Liga-Plattform für Sim-Racing, seit 05/2026,
           erdi-erc.de. ASP.NET Core MVC, EF Core mit MySQL, OAuth-Anmeldung,
           340 automatisierte Tests, 66 Datenbank-Migrationen. Läuft auf eigenem
           Server hinter Reverse Proxy und verschlüsseltem Tunnel; eine Community
           nutzt sie täglich, inklusive Server-API für eine Desktop-App.
        2. ERCTelemetry — Desktop-Anwendung für Renn-Simulationen in WPF, private
           Beta. Liest Telemetrie per UDP, speichert in SQLite, erzeugt
           Stream-Overlays für OBS, OAuth mit verschlüsselt gespeicherten
           Zugangsdaten, Installer mit Delta-Auto-Update. Zwei Testprojekte, rund
           48.000 Zeilen C#.
        3. Loren Flowers — Onlineshop für einen echten Kunden, 04/2026 bis 09/2026.
           ASP.NET Core Identity, MySQL, Warenkorb und Checkout, Kundenkonten,
           PDF-Rechnungserzeugung, Admin-Bereich. Gebaut und betrieben für einen
           realen Kunden mit echtem Bestellvolumen; der Betrieb ist seit 09/2026
           beendet, der Shop bleibt als Referenz erreichbar.
        4. Telefon-Assistent — Projekt im Aufbau, seit 09/2026. Eigener SIP-Zugang
           auf der eigenen Telefonleitung, kein Fremdanbieter. Der Assistent nimmt
           Anrufe an und begrüßt den Anrufer selbst; jede Annahme meldet sich
           sofort auf dem Handy. Ein selbst geschriebener Umschreiber im
           Verbindungsaufbau hält private Adressen aus dem SIP-Handshake heraus.
           Offen ist der Rückweg — der Assistent hört den Anrufer noch nicht.
           Das Ziel: Anrufe, die er nicht selbst annehmen kann, landen beim
           Assistenten und kommen als Nachricht bei ihm an; der Dialog soll mit
           lokaler Spracherkennung und Sprachausgabe laufen, ohne Cloud.
           Behaupte nicht, der Assistent sei fertig, führe schon Gespräche oder
           nehme Termine an.

        INFRASTRUKTUR
        Ein 3-Knoten-Hypervisor-Cluster mit rund 30 virtuellen Systemen: Reverse
        Proxy mit TLS-Terminierung, verschlüsselte Tunnels statt offener Ports,
        Monitoring mit Grafana, Prometheus und Loki samt Verfügbarkeits-Checks und
        Alarmierung, Konfigurationsmanagement mit Ansible, Dienste unter systemd
        und Docker Compose, getrenntes Backup-Ziel auf einem NAS. Das Netz war
        vollständig flach — er hat diesen Befund durch systematisches Auslesen der
        Cluster-Konfiguration gefunden, nicht durch einen Ausfall, und daraus einen
        Zonenplan gebaut: Gast-Systeme haben keinen Weg zu Daten und Management.
        Sein Satz dazu: Blast Radius ist eine Entwurfsentscheidung, keine
        Eigenschaft.

        Dazu ein Betreuungs-Agent auf eigenem Server, der die Infrastruktur per
        SSH überwacht und typische Störungen selbst behebt — mit bewusst
        begrenzten Rechten, damit der Schaden, den er anrichten könnte, null
        bleibt.
        """;
}
