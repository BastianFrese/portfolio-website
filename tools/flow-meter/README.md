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
| `meter.py` | Prozess, Zeitgeber, CLI |
| `selftest.py` | synthetische Frames — der einzige echte Unit-Test |
| `cpu-check.sh` | CPU-/Verlustmessung (das Gate aus Phase 1) |

Nur Standardbibliothek — `scapy` gibt es auf den Knoten nicht.

---

## Installation auf einem Knoten

Fünf Schritte. Die einzige Stelle, die sich zwischen den Knoten unterscheidet,
ist die **dritte** — deshalb trägt die systemd-Unit keine Adresse.

```bash
# 1. Programm
mkdir -p /opt/flow-meter /etc/flow-meter
scp console.py cpu-check.sh flow-meter.service flows.py meter.py peers.py selftest.py wire.py \
    root@<knoten>:/opt/flow-meter/
ssh root@<knoten> 'cd /opt/flow-meter && chmod 0644 *.py *.service && chmod 0755 meter.py cpu-check.sh'

# 2. Secret — dieselbe Datei auf allen Knoten, Mode 600
#    (über die Leitung schieben, nie über eine Zwischenablage:)
ssh root@<quellknoten> 'cat /etc/flow-meter/secret' \
  | ssh root@<knoten> 'umask 077; cat > /etc/flow-meter/secret'

# 3. Knoteneigene Werte  ← die einzige Datei mit einer LAN-Adresse
ssh root@<knoten> 'umask 077; cat > /etc/flow-meter/flow-meter.env <<EOF
FLOW_METER_NODE=<knotenname>
FLOW_METER_URL=http://<app-host>:5000/api/flows/ingest
FLOW_METER_SECRET_FILE=/etc/flow-meter/secret
FLOW_METER_EXCLUDE=<eigene-knoten-ip>:5000
EOF
chmod 600 /etc/flow-meter/flow-meter.env'

# 4. Selbsttest — muss grün sein
ssh root@<knoten> 'cd /opt/flow-meter && python3 meter.py --selftest'

# 5. Dienst
ssh root@<knoten> 'install -m 644 /opt/flow-meter/flow-meter.service /etc/systemd/system/ \
  && systemctl daemon-reload && systemctl enable --now flow-meter'
```

`FLOW_METER_EXCLUDE` ist **nicht optional**: ohne den Wert misst der Meter seinen
eigenen Push mit (~450 B/s Selbstmessung des Messwerkzeugs).

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
