"""Ausgabe des Messpunkts.

Getrennt, damit die Fachmodule (`peers`, `flows`) keine Ausgabe kennen und
nichts auf stdout schreiben, was die Messung verfälschen könnte.

`info` geht nach stdout (gehört zum Protokoll), `warn` nach stderr — so bleiben
Meldungen und Messwerte in `--dump` trennbar.
"""

from __future__ import annotations

import sys


def info(message: str) -> None:
    print(f"[meter] {message}", flush=True)


def warn(message: str) -> None:
    print(f"[meter] WARNUNG {message}", file=sys.stderr, flush=True)
