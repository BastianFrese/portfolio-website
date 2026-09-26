"""Das Messmodell: Fenster sammeln, Paare kanonisieren, Nutzlast bauen.

Hier liegt die Rechenregel, die die Anzeige trägt: **kanonische Paarung** nach
Adresse (kleinere zuerst) und **Dienstport = kleinerer der beiden Ports**. Beides
ist reine Arithmetik, damit zwei Knoten ohne Absprache dasselbe Ergebnis
bekommen — sonst würden sie dieselbe Konversation unterschiedlich benennen.
"""

from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Optional
from urllib.parse import urlsplit

from peers import Peers, should_count
from wire import Packet, addr_to_int

#: Obergrenze für Paare je Fenster. Was darüber liegt, wird gezählt, nicht gesendet.
MAX_FLOWS = 400

#: Ports für URLs ohne Portangabe.
_SCHEME_PORTS = {"http": 80, "https": 443}


def ingest_endpoint(url: str) -> Optional[tuple[int, int]]:
    """Der eigene Push als `(IP, Port)` — abgeleitet aus der Ziel-URL.

    Warum abgeleitet und nicht konfiguriert: das Ziel steht **schon** in der URL.
    Ein zweiter Wert dafür kann davon abweichen, und die Abweichung ist still —
    dann zählt der Meter seinen eigenen Push als Gastverkehr mit, und die Anzeige
    führt das Messwerkzeug als Datenquelle. Genau das ist passiert: konfiguriert
    war `<eigene-knoten-ip>:<port>`, aber die Prüfung in `add()` verlangt IP und
    Port auf **derselben** Paketseite. Die Quell-IP des Knotens trifft den
    Ziel-Port der App nie — es wurde also nie etwas ausgeschlossen.

    Kein DNS: ist der Host kein Adresstext, kommt `None` zurück, und der Meter
    sagt beim Start, dass der eigene Push nicht ausgeschlossen werden kann.
    """
    parts = urlsplit(url)
    if not parts.hostname:
        return None
    ip = addr_to_int(parts.hostname)
    if ip is None:
        return None
    try:
        port = parts.port or _SCHEME_PORTS.get(parts.scheme.lower(), 0)
    except ValueError:
        return None
    return (ip, port) if port else None


class Window:
    """Sammelt ein Fenster und gibt es als Ganzes wieder her.

    Der Leser füllt, der Zeitgeber leert — deshalb die Sperre. Sie ist
    unbestritten billig und wird nur um `add`/`take` gehalten.
    """

    def __init__(self, peers: Peers, node: str, excludes=()):
        self._peers = peers
        self._node = node
        #: `(IP, Port)`-Paare, die der Messaufbau selbst erzeugt — siehe
        #: `ingest_endpoint`. Eine Menge, weil das Ingest-Ziel dazukommt und
        #: `--exclude` weitere nennen darf.
        self._excludes = frozenset(excludes or ())
        self._lock = threading.Lock()
        self._counts: dict[tuple[str, str, str, int], list[int]] = {}
        self._truncated = 0

    def add(self, packet: Packet, nbytes: int) -> None:
        peers, node = self._peers, self._node
        peers.learn(packet.src_ip, packet.src_mac)
        peers.learn(packet.dst_ip, packet.dst_mac)

        # IP **und** Port müssen auf derselben Seite des Pakets stehen — deshalb
        # wird hier gegen das Ziel (App-Host:Ingest-Port) geprüft, in beiden
        # Richtungen. Eine Knoten-IP mit einem fremden Port kann das nie treffen.
        for ex_ip, ex_port in self._excludes:
            if (packet.dst_ip == ex_ip and packet.dport == ex_port) or (
                packet.src_ip == ex_ip and packet.sport == ex_port
            ):
                return

        if not should_count(peers, packet.src_ip, packet.dst_ip, node):
            return

        # Kanonische Paarung: kleinere IP zuerst. Richtungsunabhängig, damit
        # eine Konversation einen Eintrag ergibt statt zwei — und identisch auf
        # allen Knoten, weil es reine Arithmetik ist.
        if packet.src_ip <= packet.dst_ip:
            first, second = packet.src_ip, packet.dst_ip
        else:
            first, second = packet.dst_ip, packet.src_ip

        # Der Dienstport ist der kleinere der beiden. In allen Fällen, die die
        # gepflegte Liste beschreibt, ist das der Dienst (22, 53, 445, 3306,
        # 8006, 9100, 11434) und nicht der Client. Grenzfall: zwei hohe Ports
        # (Client 3000 → Dienst 5432) — dann steht der Clientport da, und das
        # Label heißt ehrlich `sonstiges`.
        #
        # **Nicht immer richtig:** ein NFS-Client mit reserviertem Quellport
        # verbindet `727 → 2049`, und dann gewinnt 727 — ein Port ohne Label.
        # Deshalb reist das Gegenstück als `port2` mit, und **die App**
        # entscheidet: sie kennt die Dienst-Tabelle, der Meter nicht (sie steht
        # bewusst nur einmal). Die Paarung selbst bleibt der kleinere Port —
        # daran hängt, dass eine Konversation **einen** Eintrag ergibt und beide
        # Knoten dieselbe Zeile rechnen.
        if packet.sport and packet.dport:
            port = min(packet.sport, packet.dport)
            port2 = max(packet.sport, packet.dport)
        else:
            port = packet.sport or packet.dport
            port2 = 0

        key = (peers.name_of(first), peers.name_of(second), packet.proto, port)
        with self._lock:
            if key in self._counts:
                slot = self._counts[key]
                slot[0] += nbytes
                slot[1] += 1
            elif len(self._counts) < MAX_FLOWS:
                # `port2` steht für die ganze Gruppe: das Gegenstück des kleinen
                # Ports, wie es das erste Paket zeigte. Bei mehreren Verbindungen
                # mit gleichem kleinem Port (der Normalfall: ein Dienst, viele
                # Clients) greift in der App ohnehin der kleinere, gelabelte
                # Port — dort ist die Wahl also ohne Wirkung.
                self._counts[key] = [nbytes, 1, port2]
            else:
                self._truncated += 1

    def take(self) -> tuple[dict, int]:
        with self._lock:
            counts, truncated = self._counts, self._truncated
            self._counts, self._truncated = {}, 0
            return counts, truncated

    def is_empty(self) -> bool:
        with self._lock:
            return not self._counts


def build_payload(
    node: str,
    window_from: float,
    window_to: float,
    counts: dict,
    dropped: int,
    truncated: int,
) -> dict:
    flows = [
        {
            "from": key[0],
            "to": key[1],
            "port": key[3],
            # Das Gegenstück des kleinen Ports. Die App wählt daraus das Label,
            # weil nur sie die Dienst-Tabelle kennt — ohne dieses Feld hieße
            # jeder NFS-Strom mit reserviertem Clientport `sonstiges`.
            "port2": slot[2],
            "proto": key[2],
            "bytes": slot[0],
            "packets": slot[1],
        }
        for key, slot in counts.items()
    ]
    flows.sort(key=lambda f: -f["bytes"])
    return {
        "node": node,
        "window": {"from": iso(window_from), "to": iso(window_to)},
        "flows": flows,
        # `dropped` = was der Kernel verloren hat, `truncated` = was über der
        # Obergrenze lag. Beides ist Verlust — aber ungleich teuer, darum
        # getrennt. Die App warnt bei beidem.
        "dropped": dropped,
        "truncated": truncated,
    }


def iso(ts: float) -> str:
    return datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def next_boundary(now: float, window: int) -> float:
    """Nächste Fenstergrenze auf der Wanduhr — ohne Absprache identisch auf allen
    Knoten, weil die Unix-Epoche die gemeinsame Referenz ist."""
    return (int(now // window) + 1) * window
