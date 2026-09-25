"""Wer ist wer — Gastnamen, Knotennamen, und die Zählregel.

Zwei Tabellen, die sich zusammenfügen: die Gast-Configs liefern `MAC → Name`
(vollständig, auch für DHCP-Gäste, denn der MAC steht überall drin), der
laufende Verkehr liefert `IP → MAC`. Zusammen ergibt das `IP → Name` auch für
Gäste ohne `ip=` in der Config — ohne QEMU-Guest-Agent.

Enthält außerdem die Dedupe-Regel (`should_count`), weil sie ohne die
Peertabelle nicht zu beantworten ist.
"""

from __future__ import annotations

import ipaddress
import json
import re
import subprocess
from dataclasses import dataclass, field
from glob import glob
from typing import Iterable, Iterator, Optional

from console import warn
from wire import addr_to_int, mac_to_int

#: Glob über die *Knotenschiene*. `/etc/pve/lxc` ist ein relativer Symlink auf
#: `nodes/<lokaler-knoten>/lxc` — ein Glob dort sähe nur die lokalen Gäste.
GUEST_CONF_GLOBS = (
    "/etc/pve/nodes/*/lxc/*.conf",
    "/etc/pve/nodes/*/qemu-server/*.conf",
)
COROSYNC_CONF = "/etc/pve/corosync.conf"

#: Adressbereiche, die niemals „extern" sind — RFC-Bereiche, keine Hausadressen.
NEVER_EXTERNAL = tuple(
    ipaddress.ip_network(c)
    for c in (
        "0.0.0.0/8",        # unspecified / this network
        "10.0.0.0/8",       # RFC 1918
        "100.64.0.0/10",    # CGNAT (Tailscale)
        "127.0.0.0/8",      # loopback
        "169.254.0.0/16",   # link-local
        "172.16.0.0/12",    # RFC 1918
        "192.168.0.0/16",   # RFC 1918
        "224.0.0.0/4",      # multicast
        "240.0.0.0/4",      # reserviert / broadcast
    )
)

#: Obergrenze für aus dem Verkehr gelernte IP→MAC-Zuordnungen.
LEARN_MAX = 4096


@dataclass
class Peers:
    """Wer ist wer — aus den Gast-Configs und corosync, ergänzt um das, was der
    Verkehr verrät.

    Zwei Tabellen, die sich zusammenfügen: die Configs liefern `MAC → Name`
    (vollständig, auch für DHCP-Gäste, denn `hwaddr=` steht überall), der
    laufende Tap liefert `IP → MAC`. Zusammen ergibt das `IP → Name` auch für
    Gäste ohne `ip=` in der Config.
    """

    ip_name: dict[int, str] = field(default_factory=dict)
    ip_node: dict[int, str] = field(default_factory=dict)
    mac_name: dict[int, str] = field(default_factory=dict)
    mac_node: dict[int, str] = field(default_factory=dict)
    guest_ips: set[int] = field(default_factory=set)
    lan: tuple = ()
    learned_ip_mac: dict[int, int] = field(default_factory=dict)

    def learn(self, ip: int, mac: int) -> None:
        if ip and mac and len(self.learned_ip_mac) < LEARN_MAX:
            self.learned_ip_mac.setdefault(ip, mac)

    def is_guest(self, ip: int) -> bool:
        if ip in self.guest_ips:
            return True
        mac = self.learned_ip_mac.get(ip)
        return mac is not None and mac in self.mac_name

    def owner(self, ip: int) -> Optional[str]:
        node = self.ip_node.get(ip)
        if node:
            return node
        mac = self.learned_ip_mac.get(ip)
        if mac is not None:
            return self.mac_node.get(mac)
        return None

    def name_of(self, ip: int) -> str:
        name = self.ip_name.get(ip)
        if name:
            return name
        mac = self.learned_ip_mac.get(ip)
        if mac is not None and mac in self.mac_name:
            return self.mac_name[mac]
        # Bindestrich statt Leerzeichen und Klammern: der Name wandert in die
        # öffentliche Antwort, und deren Vertrag lässt nur [a-zA-Z0-9._-] zu. Ein
        # `unbekannt (lan)` würde dort als **kaputter Eintrag** verworfen — das
        # kostet echte Bytes aus der Summe und markiert das Fenster zusätzlich als
        # unvollständig, wodurch die Anzeige aufhört, gemessene Stille als 0 zu
        # zeigen. Die Aussage bleibt dieselbe: nicht extern, sondern unbekannt.
        return "extern" if _is_external(ip, self.lan) else "unbekannt-lan"


def _is_external(ip: int, lan: tuple) -> bool:
    """Nur global erreichbare Adressen heißen „extern".

    Ein Multicast-Ziel als „extern" zu bezeichnen wäre schlicht falsch — und
    genau darum geht es hier.
    """
    addr = ipaddress.IPv4Address(ip)
    for network in lan:
        if addr in network:
            return False
    for network in NEVER_EXTERNAL:
        if addr in network:
            return False
    return True


def load_peers(
    globs: Iterable[str] = GUEST_CONF_GLOBS,
    corosync_path: str = COROSYNC_CONF,
    lan: Optional[tuple] = None,
    statuses: Optional[dict] = None,
) -> Peers:
    """Baut die Peertabelle aus den Configs aller Knoten (pmxcfs, clusterweit).

    `statuses` ist eine optionale `{(kind, vmid): "running"}`-Zuordnung; sie
    entscheidet bei einer doppelt vergebenen IP zugunsten des laufenden Gastes.
    """
    peers = Peers()
    claimed: dict[int, list[tuple[str, str]]] = {}  # ip -> [(name, node)]

    for pattern in globs:
        for path in sorted(glob(pattern)):
            parts = path.split("/")
            if len(parts) < 4:
                continue
            node, kind, filename = parts[-3], parts[-2], parts[-1]
            vmid = filename[:-5]
            _absorb_guest_conf(peers, claimed, path, node, kind, vmid)

    for name, addr in _load_corosync(corosync_path):
        ip = addr_to_int(addr)
        if ip is None:
            continue
        # Knoten werden benannt, zählen aber nicht als Gast (Regel 2) — sonst
        # zählt jeder Knoten seine eigene Verwaltung mit.
        peers.ip_name[ip] = name
        peers.ip_node[ip] = name

    _resolve_collisions(peers, claimed, statuses)

    if lan is None:
        lan = _derive_lan(peers)
    peers.lan = lan
    return peers


def _absorb_guest_conf(
    peers: Peers,
    claimed: dict[int, list[tuple[str, str]]],
    path: str,
    node: str,
    kind: str,
    vmid: str,
) -> None:
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            lines = fh.read().splitlines()
    except OSError:
        return

    name = None
    net_lines = []
    for line in lines:
        if ":" not in line or line.lstrip().startswith("#"):
            continue
        key, _, value = line.partition(":")
        key, value = key.strip(), value.strip()
        if key == "hostname" and kind == "lxc" and value:
            name = value
        elif key == "name" and kind != "lxc" and value:
            name = value
        elif key.startswith("net") or key.startswith("ipconfig"):
            net_lines.append(value)

    if name is None:
        name = f"{'lxc' if kind == 'lxc' else 'vm'}-{vmid}"

    macs = []
    for value in net_lines:
        for token in value.split(","):
            key, _, raw = token.strip().partition("=")
            if not raw:
                continue
            if key == "ip":
                ip = addr_to_int(raw)
                if ip is None:
                    continue
                peers.ip_name[ip] = name
                peers.ip_node[ip] = node
                peers.guest_ips.add(ip)
                claimed.setdefault(ip, []).append((name, node))
                continue
            # Der MAC-Schlüssel hängt am NIC-Modell: LXC schreibt `hwaddr=`,
            # VMs `virtio=` / `e1000=` / `vmxnet3=` … — darum am **Wert**
            # erkennen und nicht am Schlüssel. Sonst fehlen genau die VMs, und
            # mit ihnen `media`, `truenas` und `cloud-storage`.
            mac = mac_to_int(raw)
            if mac is not None:
                macs.append(mac)

    for mac in macs:
        peers.mac_name[mac] = name
        peers.mac_node[mac] = node


def _resolve_collisions(
    peers: Peers, claimed: dict[int, list[tuple[str, str]]], statuses: Optional[dict]
) -> None:
    """Zwei Gäste auf derselben Adresse: der laufende gewinnt.

    Kommt vor (eine Adresse gehört zwei Gästen, von denen einer gestoppt ist).
    Ohne Regel wäre die Zuordnung zufällig, und die Anzeige würde je nach
    Startreihenfolge einen anderen Namen nennen.
    """
    for ip, owners in claimed.items():
        if len(owners) < 2:
            continue
        winner = None
        if statuses:
            running = [n for n, _ in owners if statuses.get(n) == "running"]
            if len(running) == 1:
                winner = running[0]
        if winner is None:
            winner = sorted(n for n, _ in owners)[0]
        losers = [n for n, _ in owners if n != winner]
        peers.ip_name[ip] = winner
        peers.ip_node[ip] = dict(owners)[winner]
        warn(f"IP-Konflikt: '{winner}' gewinnt gegen {', '.join(repr(x) for x in losers)}")


def _load_corosync(path: str) -> Iterator[tuple[str, str]]:
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except OSError:
        return
    for block in re.finditer(r"\bnode\s*\{(.*?)\}", text, re.S):
        body = block.group(1)
        name = re.search(r"\bname:\s*(\S+)", body)
        addr = re.search(r"\bring0_addr:\s*(\S+)", body)
        if name and addr:
            yield name.group(1), addr.group(1)


def _derive_lan(peers: Peers) -> tuple:
    """Ohne `--lan`: das /24 um die Knotenadressen herum.

    Reicht für ein flaches Netz — und der Cluster ist genau das.
    """
    node_ips = [ip for ip, node in peers.ip_node.items() if peers.ip_name.get(ip) == node]
    if not node_ips:
        return ()
    networks = set()
    for ip in node_ips:
        packed = ipaddress.IPv4Address(ip).packed
        networks.add(ipaddress.ip_network((packed[:3] + b"\x00", 24)))
    return tuple(networks)


def load_statuses(pvesh: str = "/usr/bin/pvesh") -> dict:
    """`{name: status}` aus der Cluster-API — nur für die Konfliktentscheidung.

    Scheitert still: ohne Status wird deterministisch nach Namen entschieden.
    """
    try:
        out = subprocess.run(
            [pvesh, "get", "/cluster/resources", "--type", "vm", "--output-format", "json"],
            capture_output=True,
            text=True,
            timeout=20,
            check=False,
        )
        if out.returncode != 0:
            return {}
        return {
            entry["name"]: entry.get("status", "")
            for entry in json.loads(out.stdout)
            if isinstance(entry, dict) and entry.get("name")
        }
    except (OSError, ValueError, subprocess.SubprocessError):
        return {}


def should_count(peers: Peers, src_ip: int, dst_ip: int, node: str) -> bool:
    """Dedupe-Regel: welcher Knoten darf dieses Paar zählen?

    Ein Cross-Node-Paar sehen **beide** Bridges — die des Senders und die des
    Empfängers. Beide Knoten rechnen dieselbe Regel und kommen auf dasselbe
    Ergebnis, also zählt genau einer.
    """
    src_guest = peers.is_guest(src_ip)
    dst_guest = peers.is_guest(dst_ip)
    if not src_guest and not dst_guest:
        return False                      # Knotenverwaltung, fremde Geräte
    if src_guest and not dst_guest:
        return peers.owner(src_ip) == node
    if dst_guest and not src_guest:
        return peers.owner(dst_ip) == node
    smaller = src_ip if src_ip < dst_ip else dst_ip
    return peers.owner(smaller) == node
