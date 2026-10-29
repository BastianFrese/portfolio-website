# DESIGN STATE

> Project-specific memory for the design agent. Read this before visual work.
> Keep facts current, cite repository evidence, and update after each completed unit.
> Mark claims confirmed, inferred, or unknown. The working rules live in the design-system skill.

Last updated: 2026-10-24
Updated by: agent session (Werdegang-Block, Schul-IT-Satz, Navigation auf schmalen Geräten)

> **Stand 24.10.2026 — was sich geändert hat (Bewerbungsrunde, vor dem Versand):**
> - **Neuer Abschnitt `#werdegang`** direkt nach dem Hero: links ein Zeitstrahl
>   (`.cv`/`.cv-row`/`.cv-date`/`.cv-text`/`.cv-note`), rechts die Karte „Für die
>   Ausbildung". Grund: die Seite war ein Portfolio ohne jeden Bezug zur
>   Bewerbung — `werdegang`, `ausbildung`, `verfügbar`, `bewerbung` kamen im
>   gesamten View **null Mal** vor. Ein Recruiter, der nur die URL bekommt, sah
>   Technik, aber keinen Bewerber.
> - **Schul-IT-Satz** ergänzt (die Ausschreibung nennt „Praxisphasen in der IT der
>   Verwaltung sowie in der **Schul-IT des WMK**" wörtlich; der Brief behauptet
>   Benutzerschulung, die Seite belegte sie nicht).
> - **Navigation auf schmalen Geräten repariert.** Vorher: acht Links, 549 px Inhalt
>   bei 184 px Platz — **2 von 8 sichtbar**, der Kontakt-CTA lag außerhalb des
>   Bildes. Jetzt: eigener Streifen unter der Brand, Kontakt-CTA `position: sticky`
>   am rechten Rand und damit **immer sichtbar**.
> - **Horizontaler Seiten-Überlauf bei 320 px behoben** (vorbestehend, live gemessen:
>   369 px Inhalt bei 320 px Viewport). Ursache: `minmax(320px, 1fr)` erzwang
>   320 px Spaltenbreite, wo nur 272 px Platz sind. Fix: `minmax(min(320px, 100%), 1fr)`.
>
> **Vorherige Stände:**
> - **29.10.2026:** ERDI-Zahlen neu gemessen: **731 Tests** (`dotnet test`, 29.10.2026,
>   731 bestanden / 0 Fehler / 0 übersprungen) und **71 Migrationen**
>   (`Erdi-ERC/Migrations/`, 71 Klassen `: Migration`). Vorher standen dort 679/67 aus
>   der Messung vom 19.10.2026; dieselben Zahlen stehen in Anschreiben-Unterlagen,
>   `Services/PortfolioFacts.cs` und der Terminalzeile in `wwwroot/js/site.js`.
>   Alle vier Stellen wurden zusammen gezogen, damit Seite und PDF nicht auseinanderlaufen.
> - **19.10.2026:** Alle Projektzahlen auf den gemessenen Stand: ERDI **679 Tests / 67
>   Migrationen** (`dotnet test --list-tests`, 19.10.2026), ERCTelemetry **882 Tests**,
>   Agent-Kalender **80 Tests je Variante** (Chronik 15.10.2026), Infrastruktur **35
>   laufende Systeme** (`pvesh`, 16.10.2026). Die Telefon-Karte beschreibt wieder den
>   echten Stand: nimmt an, versteht mit lokaler Spracherkennung, führt den Dialog,
>   trägt Termine ein. **Rollenabgrenzung zu `bastian-frese.info`:** Projektzahlen
>   leben nur hier; die Laborsseite beschreibt den Betrieb. Gegenlinks in
>   `#infrastruktur`, `#diese-seite` und im Fuß (`Views/Shared/_Layout.cshtml`).

---

## 1. PROJECT

- **Name:** bastian-frese.de (Repository `portfolio-website`)
- **Project category:** persönliche Portfolioseite eines Bewerbers (Single-Page + Impressum)
- **What it is:** Eine Bewerbungsseite, die sich als laufendes Systemintegrationsprojekt
  zeigt. Sie stellt drei betriebene Anwendungen und ein Projekt im Aufbau vor, liest
  Live-Daten aus dem eigenen Proxmox-Cluster, betreibt ein Besucher-Terminal und einen
  lokalen KI-Chat.
- **Primary audience:** Ausbildungsbetriebe und Recruiter für Fachinformatik
  Systemintegration. **Inferred**, gestützt auf zwei Stellen: `Services/PortfolioFacts.cs:26-32`
  („bewirbt sich auf einen Ausbildungsplatz") und `Views/Home/Index.cshtml:347`
  („Fragen zum Portfolio, zu einem Projekt oder zu einer Stelle — schreiben Sie mir.").
- **Primary user need or job:** In wenigen Minuten beurteilen, ob der Bewerber
  betriebliche Verantwortung übernommen hat — nicht, ob er etwas bauen kann.
- **Primary conversion or goal:** Kontaktaufnahme per E-Mail (`#kontakt`, `mailto:` +
  Kopier-Knopf, `Views/Home/Index.cshtml:349-351`).
- **Brand voice:** sachlich, ohne Superlative, Schwächen werden benannt statt verdeckt —
  „offen ist der Rückweg" beim Telefon-Assistenten (`Index.cshtml:147`), „Betrieb 09/2026
  beendet" bei Loren Flowers (`Index.cshtml:99`). **Confirmed** aus dem sichtbaren Text.
- **Locales / languages:** Deutsch (`<html lang="de">`, `Views/Shared/_Layout.cshtml:2`),
  Kleinschreibung als Stilmittel in Überschriften und Navigationslabels.
- **Repository:** `github.com/BastianFrese/portfolio-website` (privat), live auf LXC 402.

### Evidence and confidence

| Claim | Status | Evidence path | Confidence |
| --- | --- | --- | --- |
| Project purpose | confirmed | `README.md:1-9` | high |
| Audience and need | inferred | `Services/PortfolioFacts.cs:26-32`, `Views/Home/Index.cshtml:347` | medium-high |
| Primary goal | confirmed | `Views/Home/Index.cshtml:349-351` | high |
| Brand voice | confirmed | `Views/Home/Index.cshtml:99,147`; `Services/PortfolioFacts.cs:28-32` | high |

## 2. STACK

- **Language:** C#
- **Framework:** ASP.NET Core MVC, `net10.0`
- **Rendering model:** MPA (server-gerendert), zwei Routen, dazu JSON-Endpunkte
- **Styling approach:** handgeschriebenes CSS, kein Framework, kein Präprozessor,
  zwei Ebenen — `site.css` (System) und `fx.css` (Effekte, lädt danach und benutzt
  dessen Tokens)
- **Build tool:** `dotnet` SDK
- **Package manager:** keiner. `Portfolio.csproj` hat **null** `PackageReference`
- **Dev command:** `dotnet run` (Port 5000)
- **Build command:** `dotnet publish -c Release -r linux-x64`
- **Lint command:** keiner
- **Test command:** keiner im Repository. `tools/ui-check/cspguard.js` und
  `tools/ui-check/sparktest.js` sind Handwächter (Node), **nicht** in der CI
- **Type check command:** keiner (Compiler übernimmt das)

## 3. ENTRY POINTS

| Concern | File |
| --- | --- |
| Root layout | `Views/Shared/_Layout.cshtml` (45 Zeilen, enthält Nav **und** Footer) |
| Primary page | `Views/Home/Index.cshtml` (355 Zeilen, der komplette Seiteninhalt) |
| Navigation | `Views/Shared/_Layout.cshtml:19-32` — 7 Anker, alle auf derselben Seite |
| Footer | `Views/Shared/_Layout.cshtml:34-40` |
| Global stylesheet | `wwwroot/css/site.css` (701 Zeilen) |
| Effect layer | `wwwroot/css/fx.css` (228 Zeilen) |
| Token layer | `:root` in `wwwroot/css/site.css:28-79` — **einzige** Token-Quelle |
| Theme / dark mode | Nur dunkel. Kein `prefers-color-scheme`-Zweig, kein Umschalter |
| Second page | `Views/Home/Impressum.cshtml` (43 Zeilen), Route `/impressum` |

## 4. DESIGN DIRECTION

- **Visual profile:** `console-house-style` — **ausdrücklich gewählt**, nicht abgeleitet.
  Der Betreiber hat den Konsolen-Look am 2026-10-06 als Hausstil für dieses Projekt
  bestätigt („den konsolen-look als ausnahme eintragen"). Die Skill verlangt genau diesen
  ausdrücklichen Eintrag; ohne ihn darf das Profil nicht angewandt werden.
- **Direction in one sentence:** Ein Instrumentenbrett — ruhige Graphit-Flächen, eine
  einzige Signal-Orange als Bedeutungsträger, Daten in Monospace, Text in Sans, jeder
  Terminal-Baustein trägt echten Inhalt statt Kulisse.
- **Why it fits:** Ein Bewerber, der erklärtermaßen betreibt statt zu bauen, zeigt seine
  Arbeitsweise am überzeugendsten, indem die Seite selbst ein betriebenes System ist. Der
  Look ist hier kein Kostüm, sondern die Behauptung in Form gebracht. Das deckt sich mit
  der Zielgruppe (Betriebe, die Verantwortung beurteilen) und mit der Marke (sachlich,
  Schwächen benannt).
- **Three adjectives:** sachlich · betrieben · dicht
- **Three impressions to avoid:** Spielerei · Selbstdarstellung · Kulisse ohne Messwert
- **Reference points (as principles):** Statusseiten und Betriebs-Cockpits, die eine Zahl
  nur zeigen, wenn sie gemessen ist; technische Dokumentation, die vor der Antwort die
  Bedingung nennt.
- **Rationale evidence paths:** `wwwroot/css/site.css:1-5` (Selbstbeschreibung
  „Instrumentenbrett. Graphit + Signal-Orange. Daten in Mono, Text in Sans"),
  `Views/Home/Index.cshtml:218-219` (Live-Anspruch an die Daten).
- **Confidence:** high für die Richtung, medium für die Zielgruppe (kein Dokument nennt
  sie explizit — sie ist aus dem Text erschlossen).
- **Current maturity:** `polished` auf `main`; der Optik-Zweig schiebt einzelne Flächen
  weiter Richtung `refined`.

### Was das Profil hier ausdrücklich **nicht** erlaubt

Aus `skills/console-house-style/SKILL.md`, damit dieser Eintrag nicht als Freibrief
gelesen wird: keine dekorative Glut, keine Neon-Ränder, kein Glassmorphism als
Standardfläche, kein Farbverlauf in Überschriften, keine Bewegung ohne Zustandsänderung.
**Keine erfundenen Statuszeilen, Versionsnummern, Testzahlen oder Laufzeiten.** Alles
bleibt bei 320 px benutzbar, semantische Steuerelemente bleiben erhalten.

⚠️ **Bekannter Verstoß gegen „keine erfundene Zahl":** `Index.cshtml:65` nennt
„626 automatisierte Tests" für ERDI. In `Services/PortfolioFacts.cs:44` steht hingegen
**340**. Die Optik-Runde auf `lokal/optik-30d8ac3` korrigiert beides auf die gemessenen
**670**; auf `main` widersprechen sich Seite und KI-Chat also weiterhin. Siehe §10.

## 5. TOKENS

Token-Quelle: `:root` in `wwwroot/css/site.css:28-79`. **Vollständig vorhanden** für
Farbe, Raum, Radius, Bewegung und Schriften. Namensschema weicht von der Skill-Empfehlung
ab (`--signal`, `--steel`, `--fg-3` statt `--color-…`) — **nicht umbenennen**: das wäre
ein Umbau des halben Stylesheets ohne sichtbaren Gewinn.

### Colour

| Token | Value | Role |
| --- | --- | --- |
| `--bg` | `#0c1015` | Grundfläche |
| `--bg-2` | `#10161d` | zweite Fläche (`.section-alt`) |
| `--panel` | `#161c24` | Karte |
| `--panel-2` | `#1b222c` | Karte, zweite Stufe |
| `--line` | `#262e38` | sichtbare Linie |
| `--line-2` | `#1a222b` | zurückgenommene Linie |
| `--fg` | `#edf1f5` | Text |
| `--fg-2` | `#9ba7b4` | sekundärer Text |
| `--fg-3` | `#74808d` | tertiär / Beschriftung — **siehe §10, Kontrast** |
| `--signal` | `#ff8a3d` | der Akzent. Bedeutung: der eine hervorgehobene Zustand |
| `--signal-ink` / `--signal-dim` | `#1a0d04` / `#4a2a13` | Text auf Signal / zurückgenommener Signal-Rand |
| `--steel` / `--steel-dim` | `#7fa8c9` / `#2b3d4d` | zweite, kühle Nebenfarbe |
| `--ok` / `--ok-dim` | `#3ddc97` / `#1e5c48` | Status: in Ordnung |
| `--warn` | `#e3b341` | Status: Warnung |
| `--bad` | `#e5534b` | Status: Fehler |
| `--glass` / `--glass-2` | `rgba(22,28,36,.55)` / `rgba(22,28,36,.78)` | Glasflächen |
| `--glass-hi` / `--hair` / `--hair-2` | `rgba(255,255,255,.04/.08/.15)` | Haarlinien und Aufhellung |
| `--blur` | `blur(20px) saturate(180%)` | Rückwärtsfilter der Glasflächen |

- **Background tiers:** 4 (`--bg`, `--bg-2`, `--panel`, `--panel-2`)
- **Text tiers:** 3 (`--fg`, `--fg-2`, `--fg-3`)
- **Accent:** `--signal` `#ff8a3d`, **eine** Bedeutung (hervorgehobener Zustand / aktive
  Marke). Grün ist laut `site.css:44` ausdrücklich **nur noch Status**, kein Akzent
- **Semantic states:** vollständig (`ok` / `warn` / `bad` + `ok-dim`)

### Typography

| Token | Value | Role |
| --- | --- | --- |
| `--sans` | `"IBM Plex Sans", …` | Text |
| `--mono` | `"IBM Plex Mono", …` | Daten, Labels, Terminal |

- **Families:** IBM Plex Sans (400, 600) und IBM Plex Mono (400, 500), **self-hosted**
  als woff2 in `wwwroot/fonts/`, `font-display: swap`, zwei davon per `<link rel=preload>`
  vorgeladen (`_Layout.cshtml:9-10`)
- **Scale type:** **keine Skala.** 13 feste px-Werte in 0,5er-Schritten (9,5 · 10 · 10,5 ·
  11 · 11,5 · 12 · 12,5 · 13 · 13,5 · 14 · 14,5 · 16 · 17) plus 6 `clamp()`-Werte.
  Die Skill (§9) verlangt eine wiederverwendete Skala mit Verhältnis ~1,125–1,333. Siehe §10.

### Space, radius, border, shadow

| Token | Value | Role |
| --- | --- | --- |
| `--s1 … --s8` | `8 · 16 · 24 · 32 · 64 · 128px` | 8px-Raster. **`--s3` fehlt nicht, es gibt kein `--s5`/`--s7`** — bewusst lückenhaft |
| `--r-sm` / `--r` / `--r-lg` | `8px` / `12px` / `16px` | Radien |
| `--wrap` | `1120px` | Inhaltsbreite (`.wrap`) |
| `--hair` / `--hair-2` | s. o. | Ränder als Haarlinien statt Rahmen |
| (keine) | — | **Kein Schatten-Token.** Tiefe entsteht über Glas + `backdrop-filter` |

### Motion

| Token | Value | Role |
| --- | --- | --- |
| `--ease` | `cubic-bezier(.22,1,.36,1)` | Auslauf für Text |
| `--spring` | `cubic-bezier(.34,1.56,.64,1)` | Feder für Flächen |
| `--t1` / `--t2` | `.16s` / `.28s` | Dauer kurz / lang |

### Breakpoints

| Name | Value | Used for |
| --- | --- | --- |
| (namenlos) | `max-width: 560px` | Karten und Kacheln einspaltig; `.cv-row` einspaltig (24.10.2026) |
| (namenlos) | `max-width: 620px` | kleinere Anpassungen |
| (namenlos) | `max-width: 700px` | Navigation: eigene Zeile, Kontakt-CTA sticky (24.10.2026) |
| (namenlos) | `max-width: 860px` | Raster |
| (namenlos) | `max-width: 900px` | Raster |
| (namenlos) | `max-width: 940px` | `.hero-grid` und `.grid-werdegang` einspaltig (`site.css:150`) |

Sechs Breakpoints, keine benannt, kein gemeinsames Raster. Bei einem Umbau auf gemeinsame
Werte achten.

### Z-index layers

| Layer | Value | Used for |
| --- | --- | --- |
| `.fx-aurora` | `-2` | Grund hinter allem |
| `body::after` | `-1` | weicher Schein oben (`site.css:96-100`) |
| Hero-Canvas | `0` / `1` | `#hero-net` hinter `.hero > .wrap` |
| `.nav` | `50` | klebende Navigation (`site.css:114`) |
| `.fx-vignette` | `69` | Vignette |
| `.fx-grain` | `70` | Korn |
| `#fx-progress` | `80` | Scroll-Fortschritt |
| `#boot-overlay` | `200` | Startüberlagerung (`fx.css:64-70`) |

## 6. COMPONENTS

Alle in `wwwroot/css/site.css` (S) oder `wwwroot/css/fx.css` (F).

| Component | Path | Reused on | States implemented | Notes / fragility |
| --- | --- | --- | --- | --- |
| `.nav` / `.nav-links` | S:113-153 | beide Seiten | hover, `:focus-visible` (global), `.cta` | `overflow-x: auto` bei schmaler Breite — horizontal scrollender Streifen. **Ab 700 px eigene Zeile, `.cta` per `position: sticky` am rechten Rand** (24.10.2026) — vorher lag der Kontakt-CTA außerhalb des Bildes |
| `.cv` / `.cv-row` / `.cv-date` / `.cv-text` / `.cv-note` | S:346-372 | #werdegang | statisch | Zeitstrahl des Werdegangs. `.cv-row` ist ein Grid (9,5 rem Termin + Text), unter 560 px einspaltig. `.grid-werdegang` = 1,05fr/0,95fr, unter 940 px einspaltig |
| `.hero` + `.hero-grid` | S:148-150 | Startseite | statisch | 2 Spalten, ab 940 px einspaltig |
| `#hero-net` (Canvas) | F:99-103, `fx.js` | Startseite | pointer, `prefers-reduced-motion` → `display:none` | Tiefensortierung; **Kanten an Welt-Distanz verknüpfen**, nicht an projizierter (sonst Flackern) |
| `.term` / `.term-bar` / `.term-out` / `.term-in` | S, `site.js` | Hero-Boot, Terminal, KI-Chat | Eingabe, Scroll, hover-Rahmen (Zweig) | **Drei Verwendungen mit unterschiedlicher Funktion** — wiederverwendet, nicht verallgemeinert |
| `.metrics` | S:262-266 | Startseite | Werte leer `—` bis geladen | leerer Zustand ist nur ein Strich, kein erklärter Zustand |
| `.card` / `.card-shot` / `.card-index` / `.pill` | S, F:133-148 | Projekte, Infrastruktur, KI | hover (Zoom + Rand + Spotlight), `.fx-live` | `.card::before` braucht `--mx`/`--my` per CSSOM — **`style="…"` wird von der CSP verworfen** |
| `.topo*` (Topologie) | S:357-444 | #fleet | live / keine Daten | ~15 Klassen, dichteste Stelle des Blatts |
| `.nbar*`, `.tile*` | S | #fleet | live | Knoten- und Gastkacheln |
| `.pipe*` (Request-Pipeline) | S:489 | #diese-seite | animierter Paketlauf | rein dekorativ, unter `prefers-reduced-motion` nicht abgeschaltet (nur via Lenis betroffen) → **siehe §10** |
| `.chip*`, `.badge*` | S | #stack, #diese-seite | statisch | |
| `.health-chip` | S | #projekte | ok / down / unbekannt | ms-Anzeige in 11 px, `--fg-3` |
| `.chat-*` | S, `site.js` | #ki | Eingabe, tippend, Antwort, Sprachausgabe | maxlength 500, Deckel 8/10 min |
| Überlagerungen | F | beide | `.gone` | `#fx-progress`, `.fx-aurora`, `.fx-grain`, `.fx-vignette`, `#boot-overlay` |

## 7. PAGES

| Route / page | Path | Purpose | Status |
| --- | --- | --- | --- |
| `/` | `Views/Home/Index.cshtml`, `Controllers/HomeController.cs:7-8` | Startseite: Hero, **werdegang**, projekte, infrastruktur, fleet, der-weg, ki, stack, kontakt | live |
| `/impressum` | `Views/Home/Impressum.cshtml`, `Controllers/HomeController.cs:10-11` | Impressum und Datenschutz | live |
| `/health/live` | `Program.cs:129` | Smoke-Test der Pipeline | live |
| `/api/status`, `/api/fleet`, `/api/fleet/stream` | `Program.cs:130,136,151` | Betriebs-/Fleet-Daten | live |
| `/api/health`, `/api/flows`, `/api/flows/ingest`, `/api/deploys`, `/api/chat` | `Program.cs:173,187,192,232,256` | Health, Flüsse, Releases, KI-Chat | live |

## 8. PROTECTED HOOKS

Von `wwwroot/js/site.js` und `wwwroot/js/fx.js` gelesen. **Nicht umbenennen, nicht
entfernen** — jeder davon bricht still, wenn er fehlt (die Skripte steigen absichtlich
ohne Fehler aus).

| Hook | Depended on by | File |
| --- | --- | --- |
| `#hero-net` | Hero-Canvas | `fx.js` |
| `#fx-progress`, `#boot-overlay`, `.fx-aurora`, `.fx-grain`, `.fx-vignette` | Effekt-Layer | `fx.js` |
| `#boot-term` | Boot-Terminal im Hero | `site.js` |
| `#status-chip`, `#health-board` | Status- und Health-Anzeige | `site.js` |
| `#m-ttfb`, `#m-fleet` | Messkacheln | `site.js` |
| `#fleet-meta`, `#fleet-grid`, `#node-stats`, `#topo` | Fleet-Bereich | `site.js` |
| `#topo-flows`, `#topo-flows-head`, `#topo-flows-extra` | von `site.js` **erzeugt und danach wieder gelesen** | `site.js` |
| `#term-out`, `#term-in-input` | Besucher-Terminal | `site.js` |
| `#chat-out`, `#chat-input`, `#chat-voice` | KI-Chat | `site.js` |
| `#copy-mail` | Kopier-Knopf im Kontakt | `site.js` |
| `.reveal` | Scroll-Einblendung, gestaffelt über `--fx-delay` | `site.js`, `fx.css:171` |
| `.section` | Scanline-Sweep (`.fx-sweep`) | `fx.js` |
| `.grid`, `.chip-groups`, `.fleet-grid`, `.metrics`, `.pipe` | Reveal-Gruppen (`:scope > .reveal`) | `site.js` |
| `.card` | Spotlight und Kippung, `--mx`/`--my` per CSSOM | `fx.js` |
| `.topo-flow`, `.topo-flow-rate`, `.topo-flow-spark` | von `site.js` erzeugt | `site.js` |
| `.term-out`, `.chat-out`, `.t-prompt`, `.ln` | Reveal-/Terminal-Verhalten | `site.js` |
| `svg[data-draw]` | Zeichnen der Diagramme | `fx.js` |
| `[data-count]` | Zahlen zählen hoch | `fx.js` |
| `path, line, rect, polyline` **innerhalb** `.fx-draw` | Zeichenanimation, `--len` pro Element | `fx.js`, `fx.css:161` |

Nur zwei `data`-Hooks im ganzen Projekt: `data-count`, `data-draw`.

## 9. CONSTRAINTS

- **Browser support:** modern (CSSOM, `clamp`, `backdrop-filter`, `animation-timeline` nur
  innerhalb `@supports`). Kein Transpiler, kein Autoprefixer
- **Performance budget:** implizit — die Seite verspricht Besuchern „0 cookies · 0 tracking
  · 0 externe requests" (`_Layout.cshtml:36`). Hero-Canvas gemessen bei 6,1 ms Median je
  Frame (Quelle: Commit `30d8ac3`, nicht in diesem Audit nachgemessen)
- **Accessibility target:** WCAG 2.2 AA (Skill §14). Erreicht wird es **nicht vollständig** — §10
- **CMS or content limits:** keine. Inhalte stehen direkt im Razor-Markup
- **Third-party embeds or widgets:** keine. Lenis liegt als eigene Datei unter
  `wwwroot/vendor/lenis.min.js`, Schriften self-hosted
- **Assets that must not change:** `wwwroot/img/{erdi.jpeg,telemetry.png,loren.jpeg}` und
  `wwwroot/favicon.svg` (Letzteres behebt einen sonst offenen 404)
- ⚠️ **Live-CSP auf LXC 402 steht nicht im Repository** (`/etc/nginx/sites-available/portfolio`):
  ```
  default-src 'none'; style-src 'self'; script-src 'self'; img-src 'self' data:;
  font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'
  ```
  - `style-src 'self'` **ohne** `'unsafe-inline'` → `style="…"`, `setAttribute('style',…)`
    und `.style.cssText` werden **still** verworfen. Erlaubt ist nur CSSOM
    (`el.style.setProperty('--mx', …)`)
  - `script-src 'self'` → **jedes CDN-Skript lädt still nicht**
  - Kein `worker-src` → fällt auf `default-src 'none'`, Worker und `blob:` blockiert
  - Nach **jeder neuen Ressourcenart** (Font, Media, Frame, Worker) die CSP mitprüfen
  - Wächter: `node tools/ui-check/cspguard.js` (prüft `site.js` + alle Views, **nicht** in der CI,
    und prüft **nur** style-Attribute, nicht die übrige CSP-Fläche)

## 10. KNOWN ISSUES

| # | Issue | Location | Severity | Suggested fix |
| --- | --- | --- | --- | --- |
| 1 | `--fg-3` erreicht auf Kartenflächen **kein AA**: 4,25:1 auf `--panel`, 3,98:1 auf `--panel-2` (Grenze 4,5). Auf `--bg` sind es nur knapp 4,74:1. Verstärkt dadurch, dass `--fg-3` fast ausschließlich in 9,5–11,5 px steht | `wwwroot/css/site.css:42`, verwendet u. a. in `:357, :379, :406, :425, :560, :629` | **major** | `--fg-3` auf einen Wert anheben, der auf `--panel-2` 4,5:1 hält (rechnerisch ≈ `#828e9c`), **oder** `--fg-3` auf Nicht-Karten-Flächen beschränken. Vorher mit einem echten Kontrastwerkzeug nachmessen — die Zahlen hier sind gerechnet, nicht gemessen |
| 2 | Schriftgrößen unter 12 px für inhaltstragenden Text: 9,5 px (`.topo-flow-via`, `.topo-chip-type`), 10 px (`.topo-flows-head em`, SVG-`.zSmall`), 10,5 px (`.topo-pill em`, `.topo-guests`), 11 px (`.health-chip .ms`, `.tile-sub span`, `.topo-count`) | `site.css:357,379,406,410,425,435,444,560,597,606,629` | **major** | Untere Grenze auf 12 px anheben und die betroffenen Stellen über die neue Skala neu verteilen. Wenn Dichte verloren geht: erst prüfen, ob die Karte die Angabe überhaupt braucht |
| 3 | **Widersprüchliche Testzahl** auf der veröffentlichten Seite: Karte „626 automatisierte Tests", KI-Chat „340" | `Views/Home/Index.cshtml:65` vs. `Services/PortfolioFacts.cs:44` | **major** | Messen (`dotnet test` im Erdi-Repo), dann **beide** Stellen auf denselben Wert. Der Zweig `lokal/optik-30d8ac3` hat das bereits auf 670 getan — Änderung übernehmen oder neu messen |
| 4 | Keine Typhografie-Skala: 13 feste Größen in 0,5er-Schritten | `wwwroot/css/site.css`, ganzes Blatt | **major** | Skala mit ~6 Stufen und Verhältnis ~1,2 entwerfen, alle Vorkommen zuordnen. Eigene Einheit, nicht nebenbei |
| 5 | `.pipe` (Paketlauf) ist rein dekorativ und wird von `prefers-reduced-motion` **nicht** abgeschaltet | `wwwroot/css/site.css:489`, `Views/Home/Index.cshtml:237-238` | **minor** | In den Abschalter in `fx.css:214-228` aufnehmen oder die Animation als Zustandsanzeige begründen |
| 6 | Leerer Zustand der Messkacheln ist ein Strich (`—`) ohne Erklärung | `Views/Home/Index.cshtml:24,40` | **minor** | Zustand benennen („wird gemessen …" bzw. „nicht verfügbar") statt `—` |
| 7 | Fünf namenlose Breakpoints (560/620/860/900/940) ohne gemeinsames Raster | `wwwroot/css/site.css` | **minor** | Auf 3–4 benannte Werte zusammenziehen, wenn ohnehin umgebaut wird |
| 8 | Spacing-Token (`--s1…--s8`) existieren, werden aber nicht überall benutzt (Literale `12px`, `34px`, `38px`, `7px`, `14px`) | `wwwroot/css/site.css` | **minor** | Beim Umbau der betroffenen Regel auf den nächstliegenden Token ziehen |
| 9 | **Die Health-Anzeige meldet „down" für erreichbare Dienste.** Am 24.10.2026 gemessen: `/api/health` liefert für **alle vier** Prüfungen `ok:false, ms:0, status:0` — obwohl `erdi-erc.de` und `loren-flowers.shop` live HTTP 200 liefern und der **gleiche `HealthService`-Quelltext**, im selben Container gebaut und ausgeführt, `200/200/200` meldet. `ms:0` heißt: die Exception wird geworfen, bevor die Zeit gemessen wird. Der `catch` verschluckt sie **ohne Log** — im Journal steht nichts. Betroffen ist auch der eigene Chip `bastian-frese.de down` auf der Seite, die sonst behauptet, alles laufe. | `Services/HealthService.cs:44-49` (der `catch`), `Program.cs:173-183` | **major** | Exception im `catch` protokollieren (`app.Logger.LogWarning`) statt zu verschlucken; danach die Ursache messen. **Nicht geklärt**, warum die laufende Instanz (Deploy 20.10.2026) sich vom Nachbau unterscheidet — der Nachbau lief als `www-data` aus `/var/www/portfolio/current` mit derselben env-Datei |
| 10 | `cspguard.js` meldet drei `style="marker-end: …"`-Attribute im SVG (`.flowTel`) | `Views/Home/Index.cshtml` (Zeilen 207, 213, 217) | **minor** | **Vorbestehend** (am 24.10.2026 per `git stash` gegen `HEAD` gegengeprüft: gleiche drei Fehlschläge, nur andere Zeilennummern). Die CSP verwirft `style`-Attribute still — der Marker `arrVor` fehlt in der veröffentlichten Grafik. Fix: den Marker per CSSOM setzen oder die Linien anders zeichnen |

**Nicht geprüft (ehrlich):** kein Browser, keine Messung. Breakpoints, Kontrast,
Tastaturweg und 320-px-Verhalten sind hier **statisch** beurteilt, nicht gerendert.
Für Punkt 1 und 2 ist `/responsive-audit` mit Browser der nächste Schritt.

## 11. BACKLOG — CANDIDATE UNITS OF WORK

| Priority | Unit of work | Target files | Depends on |
| --- | --- | --- | --- |
| 1 | Typoskala entwerfen und einführen (§10.4) — Grundlage für 2 | `wwwroot/css/site.css` | Entscheidung des Betreibers |
| 2 | `--fg-3` AA-fest machen oder in seiner Rolle beschränken (§10.1) | `wwwroot/css/site.css:42` + Verwendungen | Messung mit Kontrastwerkzeug |
| 3 | Kein inhaltstragender Text unter 12 px (§10.2) | `wwwroot/css/site.css` | 1 |
| 4 | Testzahl auf einen gemessenen Wert bringen (§10.3) | `Views/Home/Index.cshtml:65`, `Services/PortfolioFacts.cs:44` | `dotnet test` im Erdi-Repo |
| 5 | `.pipe` in den Reduced-Motion-Abschalter (§10.5) | `wwwroot/css/fx.css:214-228` | keine |
| 6 | Leere Zustände benennen (§10.6) | `Views/Home/Index.cshtml`, `site.js` | keine |
| 7 | Offene Entscheidung: `main` oder `lokal/optik-30d8ac3` als Ausgangsstand | — | **Rückfrage beim Betreiber**, siehe §14 |

## 12. DECISION LOG

| Date | Decision | Reason | Alternatives rejected |
| --- | --- | --- | --- |
| 2026-10-06 | Visuelles Profil `console-house-style` ausdrücklich gewählt | Betreiberwunsch; die Seite ist bereits genau das, und die Skill verlangt einen ausdrücklichen Eintrag statt Ableitung aus Beruf oder Repository | Profil `adaptive` (würde den bestehenden Look verwerfen) |
| 2026-10-06 | Token-Namen (`--signal`, `--fg-3`, `--s1…--s8`) bleiben | Das System ist in sich stimmig und die Skill verbietet ein zweites System daneben; eine Umbenennung wäre ein Umbau ohne sichtbaren Gewinn | Umbenennung auf `--color-*` / `--space-*` nach Skill-Vorbild |
| 2026-10-06 | Audit umfasst **beide** Stände | Betreiberentscheidung; er will aus beiden etwas behalten können | Nur `main` (veröffentlicht) oder nur der Optik-Zweig |
| 2026-10-06 | `main` ist der veröffentlichte Stand, die Optik-Arbeit liegt auf Zweigen | `main` = `origin/main` = `c68fc30`, Arbeitsverzeichnis sauber. Die vier Commits sind auf `lokal/optik-30d8ac3` und `wip/optik-2026-10-06` gesichert | — |

## 13. CHANGELOG

Append-only. One row per unit of work, newest first.

| Date | Unit | Files touched | Tokens added or changed | Verified by |
| --- | --- | --- | --- | --- |
| 2026-10-24 | **Bewerbungsrunde** — `#werdegang`-Abschnitt, Schul-IT-Satz, Navigation auf schmalen Geräten, 320-px-Überlauf behoben | `Index.cshtml` (+49), `_Layout.cshtml` (+3/−2), `site.css` (+45/−3) | **keine neuen** — nur vorhandene (`--signal`, `--fg-2`, `--fg-3`, `--hair`, `--s2`, `--s3`, `--panel`, `--r-sm`) | `dotnet publish` grün; CDP-Messung bei 320/390/700/900/1440 px: **kein Seiten-Überlauf**, CTA immer sichtbar, keine toten Anker, genau ein `h1`, Reveal feuert (opacity 1); `cspguard.js` unverändert 9/3 (3 Fehlschläge **vorbestehend**, mit `git stash` gegengeprüft) |
| 2026-10-05 | Optik-Runde (Stand `main`) — Schriften self-hosted, Lenis, fx-Layer | `site.css`, `fx.css`, `fx.js`, `site.js`, `_Layout.cshtml` | Tokens eingeführt (dieses `:root`) | Commit `c68fc30` |
| 2026-10-06 | Optik-Runde 2 (Stand `30d8ac3`, **nicht** auf `main`) | `fx.js` +170, `site.js` +175, `fx.css` +47, `site.css` +36, `Index.cshtml` 20, `_Layout.cshtml` 2, `PortfolioFacts.cs` 2 | **keine** — Token unverändert | Commit `30d8ac3`, nicht gepusht |

## 14. UNKNOWNS AND OPEN QUESTIONS

| # | Question | Blocks | Asked on |
| --- | --- | --- | --- |
| 1 | Welcher Stand ist der Ausgangspunkt des Umbaus — `main` (`c68fc30`, veröffentlicht) oder `lokal/optik-30d8ac3` (neuer, nicht live)? | Backlog 1–6 alle | 2026-10-06, offen |
| 2 | Soll `--fg-3` angehoben oder in seiner Rolle beschränkt werden? | Backlog 2 | offen |
| 3 | Zielgruppe „Ausbildungsbetriebe und Recruiter" ist aus dem Text erschlossen, von keinem Dokument bestätigt | Richtung, nicht blockierend | offen |
| 4 | Kontrastwerte in §10.1 sind **gerechnet**, nicht mit Werkzeug gemessen | Backlog 2 | offen |
| 5 | Verhalten bei 320 px und 200 % Zoom wurde **nicht gerendert geprüft** | Backlog 2, 3 | offen |

## 15. SESSION LOG

| Date | Session | Summary | Left open |
| --- | --- | --- | --- |
| 2026-10-06 | `/design-audit` (Agent) | Projekt, Stack, Tokens, Komponenten, Hooks, beide Stände erfasst; Kontrast gerechnet; 8 Befunde belegt | Ausgangsstand (§14.1), Kontrastmessung (§14.4) |

## 16. ZUSTAND — ZWEI STÄNDE IM VERGLEICH

Der Betreiber wollte ausdrücklich **beide** Stände erfasst haben. Umfang laut
`git diff --stat main 30d8ac3`:

```
Services/PortfolioFacts.cs  |   2 +-
Views/Home/Index.cshtml     |  20 ++---
Views/Shared/_Layout.cshtml |   2 +-
tools/ui-check/sparktest.js |  16 +++-
wwwroot/css/fx.css          |  47 ++++++++++++
wwwroot/css/site.css        |  36 +++++++--
wwwroot/js/fx.js            | 170 +++++++++++++++++++++++++++++++-----------
wwwroot/js/site.js          | 175 +++++++++++++++++++++++++++++++-----------
8 files changed, 354 insertions(+), 114 deletions(-)
```

| | `main` = `c68fc30` (05.10.) | `lokal/optik-30d8ac3` = `30d8ac3` (06.10.) |
| --- | --- | --- |
| Veröffentlicht | **ja** (identisch mit `origin/main`) | nein |
| **Token-Ebene** | vollständig, dieses `:root` | **unverändert** — keine Zeile `--…` kam hinzu oder ging |
| Hero-Canvas | Knoten, Kanten, Pakete, Bounce, `dpr`, Pointer | **zusätzlich eine Tiefe je Knoten** (`z ∈ [0,1]`, einmalig fern→nah sortiert, Zentralprojektion), Kamera weicht dem Zeiger aus |
| Karten/Flächen | Kippung + Spotlight, zwei getrennte Mechaniken | **eine** gemeinsame Mechanik `spotlight(el, tilt)` für Projektkarten **und** Messkacheln |
| Messkacheln | statisch | `::before`/`::after`-Rahmen, `.fx-live` |
| Hero | statisch | Parallaxe über `animation-timeline: scroll()` — nur innerhalb `@supports` |
| Terminal | `term-bar` | + `.term:hover`, Terminal-Kontur, eigene Scrollbalken |
| Cursor im Boot-Terminal | **blinkt nicht** — Regel galt nur für `h1 .cursor` | **behoben** (`.cursor`) |
| Fußzeile | „0 externe pakete" | „0 nuget-pakete · schriften und scroll-bibliothek selbst gehostet" (genauer) |
| Testzahlen | 626 auf der Karte, 340 im Chat — **widersprüchlich** | beide auf gemessene **670** korrigiert |

**Was daraus folgt:** Der visuelle Kern ist auf beiden Ständen derselbe — gleiche Tokens,
gleiche Namen, gleiche Flächenlogik. Der Optik-Zweig ist **kein anderes Design**, sondern
eine Verdichtung: Tiefe, eine zusammengeführte Zeiger-Mechanik, ein echter Parallax und
drei Korrekturen. Ein Umbau, der auf `main` aufsetzt, kann diese vier Punkte einzeln
nachziehen, ohne den Rest zu übernehmen.

**Nicht verglichen:** Die Wirkung im Browser. Der Unterschied ist aus Quelltext und
Commit-Beschreibung abgeleitet, nicht gerendert gesehen — die Zahlen „6,1 ms Median je
Frame" stammen aus der Commit-Beschreibung von `30d8ac3` und wurden hier **nicht**
nachgemessen.
