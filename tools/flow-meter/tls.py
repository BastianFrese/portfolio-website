"""TLS für den Push — die Entscheidung und der Kontext.

Dieses Modul steht **unter** `meter`, damit `selftest` es prüfen kann, ohne
`meter` zu importieren: `meter` importiert `selftest`, ein Rückimport wäre ein
Zyklus. Es kennt nur `console`.

Warum das überhaupt hier steht: das Secret reist sonst alle 45 s im Klartext
über die flache L2-Strecke, und jeder Gast im LAN kann es mitlesen. Mit einem
mitgelesenen Secret lassen sich Raten fälschen — also genau die Zahlen, die
diese Anzeige als gemessen ausgibt.

Angepinnt wird **ein** Zertifikat, das auf allen Knoten liegt. Kein
Systemspezicher: das Zielzertifikat ist selbstsigniert, und ein stiller
Rückfall auf die Systemwurzeln wäre ein anderer Vertrauenskreis, dessen
Unterschied man erst merkt, wenn er zählt.
"""

from __future__ import annotations

import os
import ssl
from typing import Optional

from console import warn


def tls_error(url: str, cafile: Optional[str]) -> Optional[str]:
    """Der eine Fall, der wirklich verbietet.

    Ohne Dateisystem, damit der Selbsttest sie ohne Zertifikat prüfen kann —
    ein Test, der erst ein Zertifikat erzeugen müsste, liefe auf dem
    Windows-Arbeitsplatz nicht.
    """
    if url.lower().startswith("https://") and not cafile:
        return (
            "https:// verlangt --cafile (oder FLOW_METER_CAFILE): das Zertifikat der "
            "Gegenseite ist selbstsigniert und wird angepinnt, nicht über den "
            "Systemspeicher geprüft"
        )
    return None


def build_tls_context(url: str, cafile: Optional[str]):
    """-> (SSLContext | None, Fehlermeldung | None)"""
    if not url.lower().startswith("https://"):
        if cafile:
            warn("--cafile angegeben, aber die URL ist http:// — wird nicht benutzt")
        return None, None

    problem = tls_error(url, cafile)
    if problem:
        return None, problem

    if not os.path.isfile(cafile):
        return None, f"Cafile nicht lesbar: {cafile}"
    try:
        context = ssl.create_default_context(cafile=cafile)
    except (ssl.SSLError, OSError) as exc:
        return None, f"Cafile nicht verwendbar ({type(exc).__name__}): {cafile}"

    # Anpinnen heißt: genau dieses Zertifikat, **und** der Name muss stimmen.
    # Die Verbindung geht auf eine IP, deshalb braucht das Zertifikat einen
    # IP-Eintrag im subjectAltName — ohne ihn scheitert die Prüfung, obwohl es
    # das richtige Zertifikat ist. Das ist die Falle, die am längsten kostet.
    context.check_hostname = True
    context.verify_mode = ssl.CERT_REQUIRED
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    return context, None
