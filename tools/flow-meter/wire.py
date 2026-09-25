"""Frames und pcap-Strom — die unterste Schicht, ohne jede Kenntnis von Gästen.

Nimmt Bytes, gibt `Packet` zurück oder `None`. Keine Namensauflösung, keine
Zählregel, keine Ausgabe: was hier durchfällt, fällt durch.

**Byte-Zählung kommt aus `orig_len`**, nicht aus `incl_len` — siehe `PcapReader`.
"""

from __future__ import annotations

import ipaddress
import struct
from typing import Iterator, NamedTuple, Optional

ETH_HDR_LEN = 14
PCAP_GLOBAL_LEN = 24
PCAP_RECORD_LEN = 16
ETHERTYPE_IPV4 = 0x0800
VLAN_TPIDS = (0x8100, 0x88A8, 0x9100)
#: 96 Byte reichen für Ethernet + IPv4 + die Transport-Ports (max. Offset 86).
SNAPLEN = 96

MAGIC_US = 0xA1B2C3D4
MAGIC_NS = 0xA1B23C4D

IPPROTO_ICMP = 1
IPPROTO_TCP = 6
IPPROTO_UDP = 17
PROTO_NAMES = {IPPROTO_TCP: "tcp", IPPROTO_UDP: "udp", IPPROTO_ICMP: "icmp"}


class Packet(NamedTuple):
    """Ein geparster IPv4-Frame. Adressen und MACs als int — das spart die
    Umwandlung pro Paket und macht den MAC-Vergleich von sich aus
    schreibweisen-unabhängig (`BC:24:11:…` == `bc:24:11:…`)."""

    src_ip: int
    dst_ip: int
    src_mac: int
    dst_mac: int
    proto: str
    sport: int
    dport: int


def parse_frame(frame: bytes) -> Optional[Packet]:
    """Ethernet → IPv4 → TCP/UDP/ICMP. `None`, wenn es uns nicht angeht.

    Der `ip`-Filter von tcpdump lässt nur IPv4 durch; hier fliegt zusätzlich
    IPv6, ARP und alles ohne IPv4-Kopf raus.
    """
    n = len(frame)
    if n < ETH_HDR_LEN:
        return None

    ethertype = int.from_bytes(frame[12:14], "big")
    offset = ETH_HDR_LEN
    if ethertype in VLAN_TPIDS:
        if n < ETH_HDR_LEN + 4:
            return None
        ethertype = int.from_bytes(frame[16:18], "big")
        offset = ETH_HDR_LEN + 4
    if ethertype != ETHERTYPE_IPV4 or n < offset + 20:
        return None

    version_ihl = frame[offset]
    if version_ihl >> 4 != 4:
        return None
    ihl = (version_ihl & 0x0F) * 4
    if ihl < 20 or n < offset + ihl:
        return None

    proto_num = frame[offset + 9]
    src_ip = int.from_bytes(frame[offset + 12 : offset + 16], "big")
    dst_ip = int.from_bytes(frame[offset + 16 : offset + 20], "big")
    src_mac = int.from_bytes(frame[6:12], "big")
    dst_mac = int.from_bytes(frame[0:6], "big")
    proto = PROTO_NAMES.get(proto_num, "other")

    # Nicht-Erst-Fragmente tragen an den Transport-Offsets Nutzdaten, keine
    # Ports. Ohne diese Sperre würden Füllbytes als Portnummer gelesen.
    if int.from_bytes(frame[offset + 6 : offset + 8], "big") & 0x1FFF:
        return Packet(src_ip, dst_ip, src_mac, dst_mac, proto, 0, 0)

    if proto_num in (IPPROTO_TCP, IPPROTO_UDP):
        toff = offset + ihl
        if n < toff + 4:
            return Packet(src_ip, dst_ip, src_mac, dst_mac, proto, 0, 0)
        return Packet(
            src_ip,
            dst_ip,
            src_mac,
            dst_mac,
            proto,
            int.from_bytes(frame[toff : toff + 2], "big"),
            int.from_bytes(frame[toff + 2 : toff + 4], "big"),
        )

    return Packet(src_ip, dst_ip, src_mac, dst_mac, proto, 0, 0)


class PcapReader:
    """Liest den pcap-Strom, den tcpdump auf stdout schreibt.

    Byte-Zählung kommt aus `orig_len` (echte Framelänge auf der Leitung), nicht
    aus `incl_len` — bei `-s 96` ist der Kopf gekürzt, und ein reiner TCP-ACK
    wäre sonst 0 Byte.
    """

    def __init__(self, stream):
        self._fh = stream
        header = _read_exact(stream, PCAP_GLOBAL_LEN)
        if header is None:
            raise ValueError("pcap-Kopf unvollständig")
        self.endian, self.nanoseconds = _sniff_endianness(header[:4])
        self.snaplen = struct.unpack(self.endian + "I", header[16:20])[0]
        self.linktype = struct.unpack(self.endian + "I", header[20:24])[0]

    def __iter__(self) -> Iterator[tuple[float, int, bytes]]:
        """-> (zeitstempel, orig_len, frame). `orig_len` ist die echte Länge."""
        while True:
            record = _read_exact(self._fh, PCAP_RECORD_LEN)
            if record is None:
                return
            ts_sec, ts_frac, incl_len, orig_len = struct.unpack(
                self.endian + "IIII", record
            )
            if incl_len > 262_144:  # kaputter Strom — lieber neu starten
                raise ValueError("unplausible incl_len")
            body = _read_exact(self._fh, incl_len)
            if body is None:
                return
            ts = ts_sec + (ts_frac / 1e9 if self.nanoseconds else ts_frac / 1e6)
            yield ts, orig_len, body[: min(incl_len, SNAPLEN + 64)]


def _read_exact(stream, count: int) -> Optional[bytes]:
    """`read(n)` auf einer Pipe darf kurz zurückkommen — hier wird aufgefüllt."""
    if count == 0:
        return b""
    chunks = []
    remaining = count
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            return None
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def _sniff_endianness(raw: bytes) -> tuple[str, bool]:
    for endian in ("<", ">"):
        magic = struct.unpack(endian + "I", raw)[0]
        if magic == MAGIC_US:
            return endian, False
        if magic == MAGIC_NS:
            return endian, True
    raise ValueError("kein pcap-Strom (magic unbekannt)")


def mac_to_int(text: str) -> Optional[int]:
    """`BC:24:11:…`, `bc-24-11-…` oder `bc2411…` → int. `None`, wenn unbrauchbar."""
    cleaned = text.strip().replace(":", "").replace("-", "")
    if len(cleaned) != 12:
        return None
    try:
        return int(cleaned, 16)
    except ValueError:
        return None


def addr_to_int(text: str) -> Optional[int]:
    """`192.0.2.5/24` → int. `dhcp` und Leeres ergeben `None`."""
    text = text.strip().split("/")[0]
    if not text or text == "dhcp":
        return None
    try:
        return int(ipaddress.IPv4Address(text))
    except (ipaddress.AddressValueError, ValueError):
        return None
