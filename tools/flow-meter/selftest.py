"""Der Selbsttest — synthetische Frames im Speicher, keine Netzwerkzugriffe.

Das ist der einzige echte Unit-Test dieses Werkzeugs. Er deckt die fummeligste
Logik ab: Frame-Parsing inklusive VLAN und Fragmenten, die pcap-Byte-Zählung aus
`orig_len`, die MAC-Brücke für DHCP-Gäste, die Dedupe-Regel, die kanonische
Paarung, die Obergrenze — und dass keine Adresse in der Nutzlast landet.

Läuft überall, auch auf einem Windows-Arbeitsplatz: `python meter.py --selftest`.

Alle Adressen sind TEST-NET-Bereiche (RFC 5737, `192.0.2.0/24`,
`198.51.100.0/24`, `203.0.113.0/24`) — **im Repo steht bewusst keine
Hausadresse**.
"""

from __future__ import annotations

import io
import ipaddress
import json
import re
import struct

from flows import MAX_FLOWS, Window, build_payload
from peers import Peers, should_count
from wire import (
    ETHERTYPE_IPV4,
    IPPROTO_ICMP,
    IPPROTO_TCP,
    IPPROTO_UDP,
    MAGIC_US,
    SNAPLEN,
    VLAN_TPIDS,
    PcapReader,
    parse_frame,
)

_T_A, _T_B = "192.0.2.10", "198.51.100.20"
_T_DHCP = "203.0.113.30"
_MAC_A = 0xBC2411000011
_MAC_B = 0xBC2411000022
_MAC_DHCP = 0xBC2411000033


def _eth(src_mac: int, dst_mac: int, ethertype: int, payload: bytes) -> bytes:
    return (
        dst_mac.to_bytes(6, "big")
        + src_mac.to_bytes(6, "big")
        + ethertype.to_bytes(2, "big")
        + payload
    )


def _ipv4(
    src: str, dst: str, proto: int, payload: bytes, frag_off: int = 0, vlan: bool = False
) -> bytes:
    total = 20 + len(payload)
    header = bytearray(20)
    header[0] = 0x45
    header[2:4] = total.to_bytes(2, "big")
    header[6:8] = frag_off.to_bytes(2, "big")
    header[9] = proto
    header[12:16] = ipaddress.IPv4Address(src).packed
    header[16:20] = ipaddress.IPv4Address(dst).packed
    return bytes(header) + payload


def _ports(sport: int, dport: int) -> bytes:
    return sport.to_bytes(2, "big") + dport.to_bytes(2, "big") + b"\x00" * 16


def _pcap_bytes(records: list[tuple[int, bytes]]) -> bytes:
    """Baut einen pcap-Strom im Speicher — µs, little endian, Ethernet."""
    out = bytearray(struct.pack("<IHHiIII", MAGIC_US, 2, 4, 0, 0, SNAPLEN, 1))
    for index, frame in enumerate(records):
        out += struct.pack("<IIII", 1_700_000_000 + index, 0, len(frame), len(frame) + 20)
        out += frame
    return bytes(out)


def _io_bytes(blob: bytes):
    return io.BytesIO(blob)


def _peers_for_test() -> Peers:
    peers = Peers()
    a, b, dhcp = (int(ipaddress.IPv4Address(x)) for x in (_T_A, _T_B, _T_DHCP))
    peers.ip_name[a] = "gast-a"
    peers.ip_node[a] = "knoten-1"
    peers.guest_ips.add(a)
    peers.mac_name[_MAC_A] = "gast-a"
    peers.mac_node[_MAC_A] = "knoten-1"

    peers.ip_name[b] = "gast-b"
    peers.ip_node[b] = "knoten-1"
    peers.guest_ips.add(b)
    peers.mac_name[_MAC_B] = "gast-b"
    peers.mac_node[_MAC_B] = "knoten-1"

    # DHCP-Gast: nur über die MAC bekannt — genau der Fall aus Phase 0.
    peers.mac_name[_MAC_DHCP] = "gast-dhcp"
    peers.mac_node[_MAC_DHCP] = "knoten-1"
    peers.lan = (ipaddress.ip_network("192.0.2.0/24"),)
    return peers


def cli_wiring(parser) -> list[str]:
    """Die Optionen, die keinen rohen String erwarten, müssen konvertiert ankommen.

    `--exclude` war genau hier kaputt: `parse_exclude` war geschrieben, aber nie
    verdrahtet, also kam der rohe String `<knoten-ip>:5000` in `Window.add()`
    an und riss beim Entpacken `ex_ip, ex_port = self._exclude` **jedes** Paket ab
    — der Leser startete endlos neu, tcpdump starb im Takt, und gepusht wurde nie
    etwas. Unbemerkt blieb es, weil der 15-Minuten-Diagnoselauf `--dump` ohne
    `--exclude` läuft. Diese Prüfung ist der Grund, warum es das nicht wieder tut.
    """
    failures: list[str] = []
    expected_ip = int(ipaddress.IPv4Address(_T_A))

    args = parser.parse_args(["--exclude", f"{_T_A}:5000"])
    if not isinstance(args.exclude, tuple) or len(args.exclude) != 2:
        failures.append(f"--exclude kommt nicht als Paar an: {args.exclude!r}")
    elif args.exclude != (expected_ip, 5000):
        failures.append(f"--exclude falsch geparst: {args.exclude!r}")

    # Ohne Angabe muss None herauskommen — `add()` prüft genau darauf.
    if parser.parse_args([]).exclude is not None:
        failures.append("--exclude ist ohne Angabe nicht None")

    # `--lan` erwartet Tupel von Netzen, nicht den Text.
    args = parser.parse_args(["--lan", "192.0.2.0/24"])
    if not isinstance(args.lan, tuple) or not args.lan:
        failures.append(f"--lan kommt nicht als Netz-Tupel an: {args.lan!r}")
    elif str(args.lan[0]) != "192.0.2.0/24":
        failures.append(f"--lan falsch geparst: {args.lan!r}")

    return failures


def selftest(parser=None) -> int:
    failures: list[str] = []

    def check(condition: bool, label: str) -> None:
        if not condition:
            failures.append(label)

    # --- Parser ---------------------------------------------------------
    cases = {
        "tcp": (
            _eth(_MAC_A, _MAC_B, ETHERTYPE_IPV4, _ipv4(_T_A, _T_B, IPPROTO_TCP, _ports(41000, 22))),
            ("tcp", 41000, 22),
        ),
        "udp": (
            _eth(_MAC_A, _MAC_B, ETHERTYPE_IPV4, _ipv4(_T_A, _T_B, IPPROTO_UDP, _ports(53, 40001))),
            ("udp", 53, 40001),
        ),
        "vlan": (
            _eth(
                _MAC_A,
                _MAC_B,
                VLAN_TPIDS[0],
                b"\x00\x64" + ETHERTYPE_IPV4.to_bytes(2, "big")
                + _ipv4(_T_A, _T_B, IPPROTO_TCP, _ports(30000, 443)),
            ),
            ("tcp", 30000, 443),
        ),
        "icmp": (
            _eth(_MAC_A, _MAC_B, ETHERTYPE_IPV4, _ipv4(_T_A, _T_B, IPPROTO_ICMP, b"\x08\x00")),
            ("icmp", 0, 0),
        ),
        "fragment": (
            _eth(
                _MAC_A,
                _MAC_B,
                ETHERTYPE_IPV4,
                _ipv4(_T_A, _T_B, IPPROTO_TCP, _ports(22, 41000), frag_off=185),
            ),
            ("tcp", 0, 0),
        ),
        "unbekanntes-proto": (
            _eth(_MAC_A, _MAC_B, ETHERTYPE_IPV4, _ipv4(_T_A, _T_B, 47, b"\x00" * 8)),
            ("other", 0, 0),
        ),
    }
    for label, (frame, expected) in cases.items():
        packet = parse_frame(frame)
        check(packet is not None, f"{label}: nicht geparst")
        if packet:
            check(
                (packet.proto, packet.sport, packet.dport) == expected,
                f"{label}: {packet.proto}/{packet.sport}/{packet.dport} != {expected}",
            )
            check(
                packet.src_ip == int(ipaddress.IPv4Address(_T_A)),
                f"{label}: Quelladresse falsch",
            )

    # IPv6 und ARP müssen rausfallen.
    check(parse_frame(_eth(_MAC_A, _MAC_B, 0x86DD, b"\x60" + b"\x00" * 39)) is None,
          "IPv6 nicht übersprungen")
    check(parse_frame(_eth(_MAC_A, _MAC_B, 0x0806, b"\x00" * 28)) is None,
          "ARP nicht übersprungen")
    check(parse_frame(b"\x00" * 8) is None, "Kurzframe nicht übersprungen")

    # --- pcap-Strom: Bytes kommen aus orig_len --------------------------
    frames = [case[0] for case in cases.values()]
    stream = _pcap_bytes(frames)
    read_back = list(PcapReader(_io_bytes(stream)))
    check(len(read_back) == len(frames), "pcap: falsche Paketzahl")
    if len(read_back) == len(frames):
        check(
            sum(item[1] for item in read_back) == sum(len(f) + 20 for f in frames),
            "pcap: orig_len nicht verwendet",
        )
        check(
            all(item[1] > len(item[2]) for item in read_back),
            "pcap: gekürzte Frames nicht als solche erkannt",
        )
        check(read_back[0][2] == frames[0], "pcap: Frame-Inhalt verschoben")

    for broken in (b"", b"\x00" * 10):
        try:
            PcapReader(_io_bytes(broken))
            failures.append("pcap: kaputter Kopf akzeptiert")
        except ValueError:
            pass

    # --- MAC-Brücke -----------------------------------------------------
    peers = _peers_for_test()
    window = Window(peers, "knoten-1")
    dhcp_frame = _eth(
        _MAC_DHCP,
        _MAC_B,
        ETHERTYPE_IPV4,
        _ipv4(_T_DHCP, _T_B, IPPROTO_TCP, _ports(41000, 11434)),
    )
    window.add(parse_frame(dhcp_frame), 500)
    counts, _ = window.take()
    names = {key[0] for key in counts} | {key[1] for key in counts}
    check("gast-dhcp" in names, f"MAC-Brücke: DHCP-Gast nicht aufgelöst ({names})")
    check("11434" not in names, "MAC-Brücke: Port als Name missverstanden")

    # --- Ehrlichkeit des Fallbacks --------------------------------------
    multicast = int(ipaddress.IPv4Address("224.0.0.251"))
    unknown_lan = int(ipaddress.IPv4Address("192.168.7.7"))
    public = int(ipaddress.IPv4Address("1.1.1.1"))
    check(peers.name_of(multicast) == "unbekannt-lan", "Multicast als extern gelabelt")
    check(peers.name_of(unknown_lan) == "unbekannt-lan", "LAN-Gerät nicht als LAN gelabelt")
    check(peers.name_of(public) == "extern", "öffentliche Adresse nicht als extern gelabelt")

    # Jedes Label, das den Knoten verlässt, muss den Namensvertrag der App
    # erfüllen — sonst verwirft sie den Eintrag als kaputt: echte Bytes fehlen in
    # der Summe, und das Fenster gilt zusätzlich als unvollständig. Beides wäre
    # still. `unbekannt (lan)` mit Leerzeichen ist genau dieser Fall.
    contract = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,62}\Z")
    for label in ("unbekannt-lan", "extern"):
        check(contract.match(label) is not None,
              f"Fallback-Label {label!r} passt nicht zum Namensvertrag der App")

    # --- Dedupe-Regel ---------------------------------------------------
    ip_a = int(ipaddress.IPv4Address(_T_A))
    ip_b = int(ipaddress.IPv4Address(_T_B))
    check(should_count(peers, ip_a, ip_b, "knoten-1"), "beide Gäste auf eigenem Knoten verworfen")
    check(not should_count(peers, ip_a, ip_b, "knoten-2"), "beide Gäste doppelt gezählt")
    check(
        should_count(peers, ip_a, public, "knoten-1"),
        "Gast→extern auf dem Gastknoten verworfen",
    )
    check(
        not should_count(peers, ip_a, public, "knoten-2"),
        "Gast→extern auf dem falschen Knoten gezählt",
    )
    check(not should_count(peers, public, public + 1, "knoten-1"), "fremder Verkehr gezählt")

    # --- Kanonische Paarung ---------------------------------------------
    window = Window(peers, "knoten-1")
    forward = _eth(_MAC_A, _MAC_B, ETHERTYPE_IPV4, _ipv4(_T_A, _T_B, IPPROTO_TCP, _ports(41000, 22)))
    backward = _eth(_MAC_B, _MAC_A, ETHERTYPE_IPV4, _ipv4(_T_B, _T_A, IPPROTO_TCP, _ports(22, 41000)))
    window.add(parse_frame(forward), 100)
    window.add(parse_frame(backward), 50)
    counts, _ = window.take()
    check(len(counts) == 1, f"beide Richtungen getrennt gezählt ({len(counts)} Einträge)")
    if len(counts) == 1:
        key, slot = next(iter(counts.items()))
        check(slot[0] == 150 and slot[1] == 2, f"Bytes nicht summiert: {slot}")
        check(key[3] == 22, f"Dienstport nicht gewählt: {key[3]}")
        check(key[0] == "gast-a" and key[1] == "gast-b", f"kanonische Ordnung falsch: {key[:2]}")

    # --- Obergrenze -----------------------------------------------------
    window = Window(peers, "knoten-1")
    for port in range(1, MAX_FLOWS + 40):
        frame = _eth(
            _MAC_A, _MAC_B, ETHERTYPE_IPV4, _ipv4(_T_A, _T_B, IPPROTO_TCP, _ports(40000, port))
        )
        window.add(parse_frame(frame), 10)
    counts, truncated = window.take()
    check(len(counts) == MAX_FLOWS, f"Obergrenze nicht gehalten ({len(counts)})")
    check(truncated == 39, f"Abschneiden nicht gezählt ({truncated})")

    # --- Nutzlast -------------------------------------------------------
    payload = build_payload("knoten-1", 1_700_000_000, 1_700_000_045, counts, 0, truncated)
    blob = json.dumps(payload)
    check("192.0.2." not in blob and "198.51.100." not in blob, "IP in der Nutzlast")
    check(payload["window"]["from"] == "2023-11-14T22:13:20Z", "Zeitstempel nicht UTC-ISO")
    check(all(f["bytes"] > 0 for f in payload["flows"]), "leere Ströme in der Nutzlast")

    if parser is not None:
        failures.extend(cli_wiring(parser))

    if failures:
        print("SELFTEST FEHLGESCHLAGEN:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print("selftest ok — Parser, pcap-Bytes, MAC-Brücke, Dedupe, Paarung, Grenzen, CLI")
    return 0
