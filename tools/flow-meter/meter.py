#!/usr/bin/env python3
"""
flow-meter — Messpunkt für echte Traffic-Raten zwischen den Gästen des Clusters.

Läuft auf einem Proxmox-Knoten als root, liest die Bridge passiv mit
(`tcpdump` schreibt pcap auf stdout) und schickt pro 45-s-Fenster eine
Zusammenfassung an die Portfolioseite.

Grundsatz: **Es verlassen nur Namen den Knoten** — nie eine IP. Das ist nicht
Nachbereinigung, sondern strukturell: die Auflösung passiert lokal, der Payload
kennt nur `name`, `proto` und `port`. Prüfbar am **Ergebnis**:
`meter.py --dump | grep -c '192\\.168\\.'` → 0.

Nur stdlib (scapy fehlt auf den Knoten). Beispiele:

    meter.py --selftest
    meter.py --peers
    meter.py --once --dump --pcap /tmp/mitschnitt.pcap
    meter.py --url http://…:5000/api/flows/ingest --secret-file /etc/flow-meter/secret

Die Konfiguration kommt aus Flags (im Dienst: aus der systemd-Unit) — im Code
steht bewusst **keine** Adresse.

Aufbau (jedes Modul kennt nur die unter ihm):

    console   Ausgabe
    wire      Ethernet/IPv4/pcap  →  Packet
    peers     Gast- und Knotennamen, Dedupe-Regel
    flows     Fenster, kanonische Paarung, Nutzlast
    meter     dieses Modul: Prozess, Zeitgeber, CLI
    selftest  synthetische Frames
"""

from __future__ import annotations

import argparse
import ipaddress
import json
import os
import queue
import re
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from typing import Optional

from console import info, warn
from flows import Window, build_payload, iso, next_boundary
from peers import Peers, load_peers, load_statuses
from selftest import selftest
from wire import SNAPLEN, PcapReader, addr_to_int, parse_frame

#: Wie oft der Leser neu startet, wenn tcpdump stirbt (Sekunden, verdoppelt sich).
RESTART_BACKOFF = (1, 2, 5, 15, 30)

EXIT_OK = 0
EXIT_CONFIG = 2


# ------------------------------------------------------------------ Übertragung


def build_opener(url: str, secret: str):
    def post(payload: dict) -> None:
        data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(url, data=data, method="POST")
        request.add_header("Content-Type", "application/json")
        request.add_header("X-Flow-Secret", secret)
        with urllib.request.urlopen(request, timeout=15) as response:
            if response.status not in (200, 202, 204):
                raise urllib.error.HTTPError(
                    url, response.status, "unerwarteter Status", response.headers, None
                )

    return post


def push_loop(messages: queue.Queue, stop: threading.Event, post) -> None:
    """Sendet nebenläufig. Ein Fehlschlag kostet das Fenster, nie den Leser."""
    while not stop.is_set():
        try:
            payload = messages.get(timeout=0.5)
        except queue.Empty:
            continue
        if payload is None:
            return
        try:
            post(payload)
        except urllib.error.HTTPError as exc:
            warn(f"Push abgelehnt: HTTP {exc.code}")
        except (urllib.error.URLError, OSError, TimeoutError) as exc:
            warn(f"Push fehlgeschlagen: {type(exc).__name__}")
        except Exception as exc:  # noqa: BLE001 — der Leser darf nie sterben
            warn(f"Push-Fehler: {type(exc).__name__}")


def _offer(messages: queue.Queue, payload: dict) -> None:
    """Ältestes verwerfen, wenn die Warteschlange voll ist — nie blockieren."""
    try:
        messages.put_nowait(payload)
    except queue.Full:
        try:
            messages.get_nowait()
        except queue.Empty:
            pass
        try:
            messages.put_nowait(payload)
        except queue.Full:
            warn("Warteschlange voll — Fenster verworfen")


# ----------------------------------------------------------------------- Lauf


def spawn_tcpdump(iface: str, extra: Optional[list] = None) -> subprocess.Popen:
    # Kein `-p`: nur im Promiscuous Mode sieht die Bridge fremde Frames.
    # `-U` puffert paketweise, `-s 96` reicht für Ethernet+IPv4+Ports (max. 86).
    cmd = ["tcpdump", "-i", iface, "-n", "-U", "-s", str(SNAPLEN), "-w", "-", "ip"]
    if extra:
        cmd += extra
    return subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)


_DROP_RE = re.compile(r"(\d+) packets? dropped by kernel")


def drain_stderr(stream, sink: list) -> None:
    """tcpdump meldet Kernel-Verluste nur beim Beenden — also mitschreiben."""
    try:
        for raw in iter(stream.readline, b""):
            line = raw.decode("utf-8", "replace").strip()
            if line:
                sink.append(line)
    except (OSError, ValueError):
        return


def kernel_drops(lines: list) -> int:
    total = 0
    for line in lines:
        match = _DROP_RE.search(line)
        if match:
            total += int(match.group(1))
    return total


def run(args, peers: Peers, messages: queue.Queue, stop: threading.Event) -> int:
    window = Window(peers, args.node, args.exclude)
    boundary = next_boundary(time.time(), args.window)
    aligned = False
    sent = 0
    dropped = 0
    attempt = 0

    while not stop.is_set():
        try:
            process = spawn_tcpdump(args.iface, args.tcpdump_args)
        except OSError as exc:
            warn(f"tcpdump nicht startbar: {exc}")
            return EXIT_CONFIG

        stderr_lines: list = []
        threading.Thread(
            target=drain_stderr, args=(process.stderr, stderr_lines), daemon=True
        ).start()

        try:
            reader = PcapReader(process.stdout)
        except ValueError as exc:
            warn(f"pcap-Strom unlesbar: {exc}")
            process.kill()
            return EXIT_CONFIG
        if reader.linktype != 1:
            warn(f"unerwarteter Link-Typ {reader.linktype} — weiter, aber ungeprüft")

        info(f"lese auf {args.iface} (knoten {args.node}), fenster {args.window}s")

        try:
            for ts, orig_len, frame in reader:
                if stop.is_set():
                    break
                packet = parse_frame(frame)
                if packet is not None:
                    window.add(packet, orig_len)
                if ts >= boundary:
                    counts, truncated = window.take()
                    if aligned:
                        window_dropped = dropped + kernel_drops(stderr_lines)
                        del stderr_lines[:]
                        _offer(
                            messages,
                            build_payload(
                                args.node,
                                boundary - args.window,
                                boundary,
                                counts,
                                window_dropped,
                                truncated,
                            ),
                        )
                        dropped = 0
                        sent += 1
                        attempt = 0
                        _dump(
                            counts,
                            boundary - args.window,
                            boundary,
                            args,
                            window_dropped,
                            truncated,
                        )
                    else:
                        # Angebrochenes Fenster beim Start: verwerfen, nicht
                        # anteilig hochrechnen. Ein halb gefülltes Fenster als
                        # 45-s-Fenster zu senden wäre eine erfundene Rate.
                        info("erstes (angebrochenes) Fenster verworfen")
                        aligned = True
                    boundary += args.window
                    if args.once and sent > 0:
                        return EXIT_OK
        except (ValueError, OSError) as exc:
            warn(f"Leser neu starten ({type(exc).__name__})")
        finally:
            _stop_process(process)

        # Teilfenster verwerfen: tcpdump endete mitten im Fenster
        if not window.is_empty():
            window.take()
            info("Teilfenster verworfen (tcpdump beendet)")
        if args.once:
            return EXIT_OK

        delay = RESTART_BACKOFF[min(attempt, len(RESTART_BACKOFF) - 1)]
        attempt += 1
        # Beim Herunterfahren (SIGTERM) endet tcpdump ebenfalls — dann ist ein
        # Neustart weder gewollt noch möglich. Ohne diese Abfrage stünde am Ende
        # jedes sauberen Laufs eine Warnung, die nach einem Fehler aussieht.
        if not stop.is_set():
            warn(f"tcpdump neu in {delay}s")
        stop.wait(delay)

    return EXIT_OK


def _stop_process(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()


def _dump(counts: dict, w_from: float, w_to: float, args, dropped: int = 0,
          truncated: int = 0) -> None:
    if not args.dump:
        return
    span = max(w_to - w_from, 1.0)
    print(
        f"\nfenster {iso(w_from)} → {iso(w_to)}   "
        f"{len(counts)} paare · {int(sum(s[0] for s in counts.values()) / span)} B/s gesamt · "
        f"{dropped} vom kernel verworfen · {truncated} abgeschnitten"
    )
    if not counts:
        print("  (kein Gast-Traffic)")
        return
    for (src, dst, proto, port), (nbytes, packets) in sorted(
        counts.items(), key=lambda kv: -kv[1][0]
    ):
        rate = int(nbytes / span)
        print(
            f"  {rate:>10} B/s  {nbytes:>9} B  {packets:>6} pkt  "
            f"{src:<18} → {dst:<18} {port}/{proto}"
        )


# ------------------------------------------------------------------ Konfiguration


def env(name: str, fallback=None):
    """Flags gewinnen, Umgebungsvariablen füllen auf — so bleibt die
    systemd-Unit lesbar und im Code steht weiterhin keine Adresse."""
    value = os.environ.get(name)
    return value if value else fallback


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="meter.py",
        description="Echte Traffic-Raten zwischen den Gästen eines Proxmox-Knotens messen.",
    )
    parser.add_argument("--iface", default=env("FLOW_METER_IFACE", "vmbr0"),
                        help="Bridge (Standard: vmbr0)")
    parser.add_argument(
        "--window", type=int, default=int(env("FLOW_METER_WINDOW", 45)),
        help="Fensterlänge in Sekunden (Standard: 45)",
    )
    parser.add_argument("--node", default=env("FLOW_METER_NODE", socket.gethostname()),
                        help="Knotenname")
    parser.add_argument("--url", default=env("FLOW_METER_URL"),
                        help="Ziel für den Push (POST, JSON)")
    parser.add_argument("--secret-file", default=env("FLOW_METER_SECRET_FILE"),
                        help="Datei mit dem Shared Secret")
    parser.add_argument(
        "--exclude", default=env("FLOW_METER_EXCLUDE"),
        help="eigener Push als IP:PORT — sonst misst der Meter sich selbst",
    )
    parser.add_argument(
        "--lan", default=env("FLOW_METER_LAN"),
        help="LAN-Netze als CIDR (Komma), sonst aus den Knotenadressen abgeleitet",
    )
    parser.add_argument("--once", action="store_true", help="ein Fenster, dann Ende")
    parser.add_argument("--dump", action="store_true", help="Fenster auf stdout zeigen")
    parser.add_argument("--pcap", help="statt live: eine pcap-Datei lesen")
    parser.add_argument("--peers", action="store_true", help="Peertabelle zusammenfassen")
    parser.add_argument("--selftest", action="store_true", help="Parser/Dedupe prüfen")
    parser.add_argument("--tcpdump-arg", action="append", dest="tcpdump_args", default=[])
    return parser


def parse_exclude(text: Optional[str]) -> Optional[tuple]:
    if not text:
        return None
    host, _, port = text.rpartition(":")
    ip = addr_to_int(host)
    if ip is None or not port.isdigit():
        raise SystemExit("--exclude muss die Form IP:PORT haben")
    return ip, int(port)


def parse_lan(text: Optional[str]) -> Optional[tuple]:
    if not text:
        return None
    try:
        return tuple(ipaddress.ip_network(part.strip()) for part in text.split(",") if part.strip())
    except ValueError as exc:
        raise SystemExit(f"--lan unbrauchbar: {exc}")


def _read_secret(path: str) -> str:
    with open(path, "r", encoding="utf-8") as fh:
        secret = fh.read().strip()
    if not secret:
        raise SystemExit(f"{path} ist leer")
    return secret


# ------------------------------------------------------------------------- Main


def main(argv: Optional[list] = None) -> int:
    args = build_parser().parse_args(argv)

    if args.selftest:
        return selftest()

    peers = load_peers(
        lan=parse_lan(args.lan),
        statuses={} if args.pcap else load_statuses(),
    )
    if not peers.guest_ips and not peers.mac_name:
        warn("keine Gäste gefunden — läuft das auf einem Cluster-Knoten?")
        return EXIT_CONFIG

    if args.peers:
        guests = sorted(set(peers.mac_name.values()) | set(peers.ip_name.values()))
        print(f"{len(peers.mac_name)} Gäste in der MAC-Tabelle, "
              f"{len(peers.guest_ips)} feste Adressen, {len(peers.ip_name)} Namen")
        print("  " + ", ".join(guests))
        return EXIT_OK

    if args.pcap:
        return replay(args, peers)

    if args.url:
        if not args.secret_file:
            warn("--secret-file fehlt (oder FLOW_METER_SECRET_FILE setzen)")
            return EXIT_CONFIG
        post = build_opener(args.url, _read_secret(args.secret_file))
    elif args.dump:
        # Reiner Diagnoselauf: messen und zeigen, nichts senden.
        post = lambda payload: None  # noqa: E731
    else:
        warn("--url fehlt (oder FLOW_METER_URL setzen)")
        return EXIT_CONFIG

    messages: queue.Queue = queue.Queue(maxsize=4)
    stop = threading.Event()

    def on_signal(signum, frame):
        info("beende — Teilfenster wird verworfen")
        stop.set()

    for signal_name in ("SIGTERM", "SIGINT"):
        if hasattr(signal, signal_name):
            signal.signal(getattr(signal, signal_name), on_signal)

    threading.Thread(target=push_loop, args=(messages, stop, post), daemon=True).start()
    return run(args, peers, messages, stop)


def replay(args, peers: Peers) -> int:
    """Eine pcap-Datei durch dieselbe Maschine schicken — für die Prüfung echter
    Rohdaten, bevor irgendetwas live läuft."""
    with open(args.pcap, "rb") as fh:
        reader = PcapReader(fh)
        window = Window(peers, args.node, args.exclude)
        first = last = None
        total = 0
        for ts, orig_len, frame in reader:
            if first is None:
                first = ts
            last = ts
            packet = parse_frame(frame)
            if packet is not None:
                window.add(packet, orig_len)
                total += 1
        counts, truncated = window.take()
    span = max((last or 0) - (first or 0), 1.0)
    info(f"{total} IPv4-Pakete über {span:.1f}s, {len(counts)} Paare, {truncated} abgeschnitten")
    _dump(counts, first or 0, last or 0, args, 0, truncated)
    if not args.dump:
        payload = build_payload(args.node, first or 0, last or 0, counts, 0, truncated)
        print(json.dumps(payload, indent=2, ensure_ascii=False))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
