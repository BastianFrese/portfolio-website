# flow-meter — echter Traffic-Messpunkt an den Knoten-Bridges

Ein passiver Mitschnitt auf `vmbr0` je Proxmox-Knoten. Pro 45-s-Fenster geht eine
Zusammenfassung an die Portfolioseite, die daraus die Traffic-Raten in der
Topologie zeichnet.

**Es verlassen nur Namen den Knoten.** Die Auflösung IP → Name passiert lokal,
der Payload kennt nur `name`, `proto` und `port`. Prüfbar am Ergebnis:

```bash
python3 /opt/flow-meter/meter.py --once --dump | grep -c '192\.168\.'   # muss 0 sein
```

In dieser Datei stehen deshalb **keine LAN-Adressen** — nur Platzhalter. Dieselbe
Regel gilt im Programm selbst.

---

## Aufbau

Jedes Modul kennt nur die unter ihm:

| Modul | Aufgabe |
|---|---|
| `console.py` | `info` / `warn` — damit die Fachmodule stumm bleiben |
| `wire.py` | Ethernet/IPv4 und der pcap-Strom → `Packet` |
| `peers.py` | Gast- und Knotennamen, MAC-Brücke, **Dedupe-Regel** |
| `flows.py` | Fenster, kanonische Paarung, Nutzlast |
| `tls.py` | TLS-Kontext für den Push — und die Regel, dass `https://` ein Cafile verlangt |
| `meter.py` | Prozess, Zeitgeber, CLI |
| `selftest.py` | synthetische Frames — der einzige echte Unit-Test |
| `cpu-check.sh` | CPU-/Verlustmessung (das Gate aus Phase 1) |

Nur Standardbibliothek — `scapy` gibt es auf den Knoten nicht.

---

## Installation auf einem Knoten

Sechs Schritte. Die einzige Stelle, die sich zwischen den Knoten unterscheidet,
ist die **vierte** — deshalb trägt die systemd-Unit keine Adresse.

```bash
# 1. Programm
mkdir -p /opt/flow-meter /etc/flow-meter
scp console.py cpu-check.sh flow-meter.service flows.py meter.py peers.py selftest.py tls.py wire.py \
    root@<knoten>:/opt/flow-meter/
ssh root@<knoten> 'cd /opt/flow-meter && chmod 0644 *.py *.service && chmod 0755 meter.py cpu-check.sh'

# 2. Secret — dieselbe Datei auf allen Knoten, Mode 600
#    (über die Leitung schieben, nie über eine Zwischenablage:)
ssh root@<quellknoten> 'cat /etc/flow-meter/secret' \
  | ssh root@<knoten> 'umask 077; cat > /etc/flow-meter/secret'

# 3. Zertifikat der App-Seite — dieselbe Datei auf allen Knoten.
#    Das ist ein öffentlicher Teil, kein Geheimnis; der private Schlüssel
#    bleibt ausschließlich auf dem App-Host. Erzeugung: siehe „TLS".
ssh root@<app-host> 'cat /etc/portfolio/tls/ingest.crt' \
  | ssh root@<knoten> 'cat > /etc/flow-meter/ingest.crt; chmod 644 /etc/flow-meter/ingest.crt'

# 4. Knoteneigene Werte  ← die einzige Datei mit einer LAN-Adresse
ssh root@<knoten> 'umask 077; cat > /etc/flow-meter/flow-meter.env <<EOF
FLOW_METER_NODE=<knotenname>
FLOW_METER_URL=https://<app-host>:5443/api/flows/ingest
FLOW_METER_SECRET_FILE=/etc/flow-meter/secret
FLOW_METER_CAFILE=/etc/flow-meter/ingest.crt
EOF
chmod 600 /etc/flow-meter/flow-meter.env'

# 5. Selbsttest — muss grün sein
ssh root@<knoten> 'cd /opt/flow-meter && python3 meter.py --selftest'

# 6. Dienst
ssh root@<knoten> 'install -m 644 /opt/flow-meter/flow-meter.service /etc/systemd/system/ \
  && systemctl daemon-reload && systemctl enable --now flow-meter'
```

### Der eigene Push wird automatisch ausgeschlossen

Der Meter erzeugt selbst Verkehr: alle 45 s ein POST an den App-Host, und dessen
Antwort. Dieses Paar ist **kein Gastverkehr**, sondern das Messwerkzeug — es
mitzuzählen hieße, die Anzeige als Datenquelle zu führen. Das Ziel wird deshalb
**aus `FLOW_METER_URL` abgeleitet** (`flows.ingest_endpoint`) und immer
ausgeschlossen, in beiden Richtungen.

Es steht bewusst **nicht** in der Konfiguration: derselbe Wert an zwei Stellen
kann auseinanderlaufen, und die Abweichung ist still. Genau das ist passiert —
siehe „Fallstricke". `--exclude` / `FLOW_METER_EXCLUDE` gibt es weiter, aber nur
für **weitere** Ströme, die zusätzlich draußen bleiben sollen.

Ist der Host in der URL kein Adresstext (also ein Name), lässt sich nichts
ableiten; der Meter **sagt das beim Start** und misst dann seinen eigenen Push
mit, bis `--exclude` gesetzt ist.

Der `<knotenname>` muss **wortgleich** in `FLOWS__NODES` der App stehen — siehe
die Betriebsregel unten.

---

## ⚠️ Die wichtigste Betriebsregel: `FLOWS__NODES` muss mitwachsen

In `/etc/portfolio/portfolio.env` auf dem App-Host:

```
FLOWS__NODES=prox1,prox,pve
```

Die Liste ist der **Maßstab der Null-Regel**. Eine `0` in der Anzeige heißt
„gemessen, und da war nichts" und darf **nur** erscheinen, wenn *alle*
erwarteten Knoten für diesen Slot gemeldet haben. Steht dort nur `prox1`,
während drei Knoten pushen, ist diese Bedingung **nie** erfüllt → es erscheint
**nie eine 0**, und die Anzeige bleibt bei `—`.

Kurz: **Ein Messpunkt mehr heißt: erst `FLOWS__NODES` erweitern, dann den Dienst
starten.** Die Liste ist außerdem eine Erlaubnisliste — ein Push unter einem
Namen, der nicht darin steht, wird mit **403** abgewiesen.

Aktueller Stand: `prox1`, `prox` und `pve` messen, alle drei stehen in der Liste.

---

## TLS auf dem Push-Weg

**Warum.** Das Secret reist sonst alle 45 s im Klartext über die flache
L2-Strecke, und jeder Gast im LAN kann es mitlesen. Mit einem mitgelesenen Secret
lassen sich Raten **fälschen** — also genau die Zahlen, die die Anzeige als
gemessen ausgibt. TLS schließt Mitlesen *und* aktiven MITM.

**Wo.** nginx auf dem App-Host, ein zweiter Listener auf `:5443`, und dort **nur**
der Ingest-Pfad; alles andere `404`. Kestrel selbst hört nur noch auf
`127.0.0.1:5000` — der Klartextpfad existiert damit nicht mehr, statt bloß
ungenutzt zu sein. Der öffentliche Weg bleibt unverändert: Tunnel → nginx `:80` →
dieselbe App.

### Zertifikat erzeugen (auf dem App-Host)

```bash
install -d -m 700 /etc/portfolio/tls
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
  -keyout /etc/portfolio/tls/ingest.key \
  -out    /etc/portfolio/tls/ingest.crt \
  -subj "/CN=portfolio-ingest" \
  -addext "subjectAltName=IP:<app-host>" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "extendedKeyUsage=serverAuth"
chmod 600 /etc/portfolio/tls/ingest.key
```

⚠️ **Die Falle, die am längsten kostet: der IP-Eintrag im `subjectAltName`.**
Der Meter verbindet auf eine **IP**, nicht auf einen Namen. Fehlt der
`IP:`-Eintrag, scheitert die Prüfung *trotz richtigen Zertifikats*, und die
Meldung lautet `IP address mismatch` — das sieht nach einem falschen Zertifikat
aus, nicht nach einem fehlenden Feld. Nachprüfen:

```bash
openssl x509 -in /etc/portfolio/tls/ingest.crt -noout -text | grep -A1 'Alternative Name'
```

Der **private Schlüssel verlässt den App-Host nie.** Verteilt wird nur `ingest.crt`.

### Was der Meter prüft

`tls.py` pinnt **genau dieses eine** Zertifikat — kein Systemspeicher. Ein stiller
Rückfall auf die Systemwurzeln wäre ein anderer Vertrauenskreis, dessen
Unterschied man erst merkt, wenn er zählt. Dazu `check_hostname=True`,
`verify_mode=CERT_REQUIRED`, mindestens TLS 1.2.

**Fail-closed:** `https://` ohne Cafile wird beim Start abgewiesen (`EXIT_CONFIG`)
— nicht beim ersten Push. Und `--cafile` bei einer `http://`-URL wird nicht
stillschweigend benutzt, sondern gemeldet.

Nachprüfen, in dieser Reihenfolge:

```bash
# 1. Handshake gegen den Listener (ohne Zertifikat MUSS es scheitern)
curl --cacert /etc/flow-meter/ingest.crt -o /dev/null -w '%{http_code}\n' \
  https://<app-host>:5443/api/flows/ingest -X POST -d '{}'     # 401 = erreichbar, TLS steht
curl -o /dev/null -w '%{http_code}\n' https://<app-host>:5443/api/flows/ingest
#   -> curl: (60) self-signed certificate   ← sonst pinnt nichts

# 2. Kommt der Push wirklich über TLS an? Das Zugriffslog nennt die Quell-IP:
tail -5 /var/log/nginx/flows-ingest.access.log
# 192.168.100.24 - - [...] "POST /api/flows/ingest HTTP/1.1" 202 0 "-" "Python-urllib/3.13"
```

### Rotieren

```bash
# 1. neues Zertifikat erzeugen (Befehl oben, gleicher Pfad)
# 2. neues Cert auf ALLE Knoten legen — Dienst noch NICHT neu starten
# 3. nginx -t && systemctl reload nginx
# 4. auf allen Knoten: systemctl restart flow-meter
```

Zwischen 3 und 4 laufen die Pushes ins Leere (die laufenden Prozesse halten noch
das **alte** Zertifikat im Kontext), der Meter warnt und versucht es im nächsten
Fenster erneut. Die Lücke ist auf einen Schritt begrenzt und heilt von selbst;
`stale` springt erst nach ~90 s Stille auf `keine daten`.

Wollte man die Lücke ganz vermeiden, müsste man eine eigene CA anlegen und
Leaf-Zertifikate ausstellen, deren **CA** auf den Knoten gepinnt ist — dann
kostet die Rotation keinen Knoten-Zugriff. Für ein Zertifikat mit zehn Jahren
Laufzeit auf drei Knoten ist das mehr Maschinerie als Nutzen; deshalb steht hier
der einfache Weg, und die Reihenfolge.

Läuft es ab, **stoppt der Push sichtbar**: `Push fehlgeschlagen:
SSLCertVerificationError` im Journal, die Anzeige geht auf `keine daten`. Nicht
still.

---

## Läuft es?

Ein gesunder Meter schreibt **je Fenster eine Zeile** — nicht nur beim Start.
Ohne diese Zeile sähe „läuft" genauso aus wie „hängt":

```bash
journalctl -u flow-meter -o cat | grep 'fenster #' | tail -3
# [meter] fenster #1 2026-09-25T22:32:15Z · 135 paare · 21104 B/s · 0 verworfen · 0 abgeschnitten · 1 in der schlange
```

| Feld | Bedeutung |
|---|---|
| `paare` | verschiedene Paare im Fenster |
| `B/s` | Summe über alle Paare |
| `verworfen` | tcpdump-Verluste im Kernel (`packets dropped by kernel`) |
| `abgeschnitten` | Paare über der Obergrenze `MAX_FLOWS = 400` |
| `in der schlange` | ungesendete Fenster — **das Frühwarnzeichen**: wächst der Wert, hängt der Sender |

`verworfen` oder `abgeschnitten` > 0 machen den Slot **unvollständig** — die App
schreibt dann in diesem Slot **keine 0**. Das ist Absicht: ein Fenster mit
Verlust ist kein ruhiges Fenster.

---

## Diagnose

```bash
# Rohe Fenster ansehen, ohne etwas zu senden (braucht rund 90 s: das erste,
# angebrochene Fenster wird verworfen, nicht anteilig hochgerechnet)
python3 /opt/flow-meter/meter.py --once --dump --node <knotenname>
#   Ohne --url gibt es nichts abzuleiten, also erscheint hier auch der eigene
#   Push (Ziel:Ingest-Port). Das ist die Sicht des Messpunkts, nicht die der
#   Anzeige — kein Fehler. Mit --url verschwindet er.

# Peertabelle: wen kennt der Meter überhaupt, und welchem Knoten gehört wer?
python3 /opt/flow-meter/meter.py --peers

# eine aufgezeichnete pcap durch dieselbe Maschine schicken
python3 /opt/flow-meter/meter.py --pcap /tmp/mitschnitt.pcap --dump

# Syntaxprüfung aller Module
python3 -m py_compile /opt/flow-meter/*.py
```

Läuft tcpdump nicht, ist sein **stderr** die einzige Spur. Der Meter zeigt die
ersten Zeilen davon als `WARNUNG tcpdump: …` im Journal — genau daran hing die
Fehlersuche beim ersten Start (siehe „Fallstricke").

### Fallstricke, die schon zugeschlagen haben

- **`Couldn't change to 'tcpdump' uid=102: Operation not permitted`** —
  `CapabilityBoundingSet` ohne `CAP_SETUID`. Debian-tcpdump ist weder setuid noch
  mit Datei-Capabilities ausgestattet: es startet als root und wechselt nach dem
  Öffnen des Sockets auf den Benutzer `tcpdump`. Gelöst mit `-Z root` (der
  Rechteabfall schützt hier nichts, weil `-w -` keine Pakete zerschneidet) statt
  mit vier zusätzlichen Capabilities.
- **`RestrictAddressFamilies` ohne `AF_PACKET`** — tcpdump startet nicht, und der
  Fehler sieht nach „keine Pakete" aus, nicht nach einem Rechteproblem.
- **Programm unter `/root`** — `ProtectHome=yes` macht `/root` für den Dienst
  leer; der Fehler sieht nach einem fehlenden Programm aus. Deshalb `/opt`.
- **Ohne Promiscuous Mode sieht die Bridge keine fremden Frames.** In
  `spawn_tcpdump` steht bewusst **kein** `-p`.
- **Der eigene Push wurde mitgemessen — seit dem ersten Tag.** Der Ausschluss
  prüft IP **und** Port auf *derselben* Paketseite (`flows.py`), konfiguriert war
  aber `<eigene-knoten-ip>:<port>`: die Quell-IP des Knotens trifft den Ziel-Port
  der App **nie**. Es wurde also nie etwas ausgeschlossen, und jeder Knoten hat
  seinen eigenen Push als Gastverkehr gemeldet — drei Paare
  `portfolio-ws → <knoten> sonstiges` mit 139–407 B/s. Gefunden nicht durch
  Hinsehen, sondern beim Nachrechnen, welche Paare es überhaupt geben kann.
  Behoben, indem der Ausschluss aus `FLOW_METER_URL` **abgeleitet** wird: ein
  zweiter Wert kann nicht mehr davon abweichen. Der Selbsttest prüft seither
  beide Richtungen **und** die Gegenprobe (ohne Ausschluss müssen beide Paare
  durchkommen — sonst wäre der Test grün, ohne etwas zu beweisen).

---

## Secret rotieren

Dasselbe Secret auf allen Knoten **und** in der App — in dieser Reihenfolge,
sonst reißt der Push ab:

```bash
neu=$(openssl rand -hex 32)

# 1. App:       FLOWS__INGESTSECRET in /etc/portfolio/portfolio.env ersetzen
# 2. Knoten:    je Knoten  printf '%s' "$neu" | ssh root@<knoten> 'umask 077; cat > /etc/flow-meter/secret'
# 3. Knoten:    je Knoten  systemctl restart flow-meter
```

Zwischen Schritt 1 und 3 laufen Pushes ins Leere (401) — der Meter verwirft sie
und macht weiter, die Anzeige bleibt kurz bei `keine daten`. Kein Datenverlust,
nur eine Lücke.

---

## Rollback

```bash
systemctl disable --now flow-meter
```

Das ist das **Rollback-Akzeptanzkriterium**: die Anzeige springt von selbst auf
`keine daten`, alle Zeilen auf `—` — **ohne UI-Deploy**. Die App erkennt den
Ausfall an der Wanduhr (`stale`), sie muss nicht angefasst werden. Ein einzelner
Knoten kann genauso stillgelegt werden; die anderen messen weiter, und die
Paare, die nur er zählen durfte, erscheinen als `—` statt als falsche Zahl.

Rückstandsfrei entfernen:

```bash
systemctl disable --now flow-meter
rm -f /etc/systemd/system/flow-meter.service
rm -rf /opt/flow-meter /etc/flow-meter
systemctl daemon-reload
```

### Zurück auf Klartext-http

Geht **nicht** mehr allein über die Knoten: Kestrel hört nur auf `127.0.0.1:5000`,
die LAN-Adresse gibt es nicht mehr. Wer den Klartextweg zurückwill, muss ihn auf
dem App-Host wieder öffnen und `FLOW_METER_URL` auf den alten Port ziehen (den
Ausschluss des eigenen Pushs leitet der Meter daraus selbst ab):

```bash
# App-Host
sed -i 's|^Environment=ASPNETCORE_URLS=http://127.0.0.1:5000$|Environment=ASPNETCORE_URLS=http://+:5000|' \
  /etc/systemd/system/portfolio.service
systemctl daemon-reload && systemctl restart portfolio
```

Das ist der **Notausgang**, nicht der Normalfall — er stellt das Mitlesen wieder
her. Bleibt der `:5443`-Listener daneben stehen, ist das unschädlich; er ist der
bessere Weg.

---

## Was bewusst nicht gemessen wird

- **IPv6.** Der Filter ist `ip` (nur IPv4). Tunnel-Verkehr nach Cloudflare läuft
  teilweise über IPv6 und fehlt damit. Bewusst: IPv6-Parsing würde die
  Parserfläche verdoppeln für wenig Aussage.
- **Knotenverwaltung ohne Gast-Beteiligung** (corosync, apt, NTP). Ein Datensatz
  zählt nur, wenn **mindestens ein Endpunkt ein bekannter Gast** ist.
- **Physische Geräte** (Switch, BMC, Router) sind keine Gäste und erscheinen als
  `unbekannt-lan`.

---

## Der Dedupe-Beweis (Phase 4.2, zum Wiederholen)

Ein knotenübergreifendes Paar **sehen beide Bridges** — die des Senders und die
des Empfängers. Ohne Regel würde es doppelt gezählt. Die Regel
(`peers.py: should_count`) wendet jeder Knoten **selbst** an, damit der Server
nichts deduplizieren muss:

| Fall | Wer zählt |
|---|---|
| Genau ein Endpunkt ist Gast | der Knoten, dem dieser Gast gehört |
| Beide Endpunkte sind Gäste | der Knoten der **numerisch kleineren IP** |
| Kein Endpunkt ist Gast | niemand |

⚠️ „Kleinere IP" ist **numerisch** gemeint (`flows.py` vergleicht
`int(IPv4Address(...))`), **nicht** lexikografisch. Lexikografisch wäre
`…100.228` kleiner als `…100.71` — dann käme das Gegenteil heraus. Beide Knoten
rechnen dieselbe Ganzzahl, deshalb sind sie sich einig.

Nachprüfen, ohne etwas zu verändern — drei unabhängige Wege:

1. **Auf dem Knoten, der verwerfen muss.** `meter.py --once --dump` zeigt das
   Paar **nicht**, obwohl der Knoten die Frames sieht und beide Gäste kennt.
2. **Byte-Gegenprobe.** Am Herzschlag ausgerichtet die Zähler von `vmbr0` lesen
   (`rx_bytes+tx_bytes`) und gegen die Metersumme × 45 s stellen. Erwartung:
   **kleiner oder gleich**, typisch ~99 % (IPv6/ARP/fremde Frames fehlen
   bewusst). **Größer wäre der Alarm** — dann wird doppelt gezählt.
3. **PVE-Zähler als grober Maßstab.** `netIn + netOut` eines der beiden Gäste aus
   `/api/fleet` gegen die Paar-Rate stellen: sie muss in derselben Größenordnung
   liegen (~1,2×), **nicht** beim Doppelten (~2×). Nur ein grober Maßstab — die
   Zähler werden über ein anderes Intervall gemittelt, Abweichungen um 25 % sind
   normal und bedeuten nichts.

Gemessener Stand vom 2026-09-26: Weg 2 ergab 99,4 %, die Paar-Rate lag bei 1,19×
der Gastsumme — eine Verdopplung ist damit ausgeschlossen.
