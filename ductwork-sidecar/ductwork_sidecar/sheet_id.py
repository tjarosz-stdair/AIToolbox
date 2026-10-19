"""Says which sheet a page is: its number, title, discipline and kind, from the title block (plan section 6.7).

Our own step, not the wrapped engine's: the engine's title-block rules assumed the block is unrotated at
the bottom right, and treated electrical sheets as plans. This works in canonical space (after /Rotate), so
a sheet stored sideways is read the same as an upright one, and it names the discipline so that only
mechanical sheets are counted.

Everything here is a proposal. A person can correct a sheet number, title or kind on the Drawings tab.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from .pdf_layout import SheetFacts, Span

# A sheet number is letters then digits, with the separators designers use: M1.6, M1-S1, M0-00, MD101, E0.2.
SHEET_NO = re.compile(r"^[A-Z]{1,3}\d{0,2}[-.]?[A-Z]?\d{1,3}(?:[.-]\d{1,3})?[A-Z]?$")

# Things that look like sheet numbers but are not: insulation values and equipment tags.
_NOT_A_SHEET = re.compile(r"^(R-?\d+|EF-?\d+|RG-?\d+|SD-?\d+|FD-?\d+|HP-?\d+|CO2|PC-?\d+)$")

TITLE_STRIP_FROM = 0.78
BOTTOM_BAND_FROM = 0.86

_NOISE = re.compile(
    r"^(REVISIONS?|PROJECT\s*(#|NO\.?|NUMBER)?:?|DATE:?|SCALE:?|DRAWN( BY)?:?|CHECKED( BY)?:?|SHEET( NO\.?| NUMBER| TITLE)?:?|NO\.?|DESCRIPTION|BY|ISSUED?( FOR)?:?|SEAL|"
    r"CONSTRUCTION DOCUMENTS|PERMIT|BULLETIN.*|ADDENDUM.*|KEYNOTES|MARK|COPYRIGHT.*)$", re.I)
_DATE = re.compile(r"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b|\b[A-Z]{3,9}\s+\d{1,2},?\s+\d{4}\b", re.I)
_PHONE = re.compile(r"\d{3}[.\- ]\d{3}[.\- ]\d{4}")
_ADDRESS = re.compile(r"\b(ST|STREET|RD|ROAD|AVE|AVENUE|DR|DRIVE|BLVD|SUITE|STE|LN|LANE|HWY|PKWY)\b\.?|\b[A-Z]{2}\s+\d{5}\b|,\s*[A-Z]{2}\b", re.I)

_TITLE_WORDS = ("PLAN", "SCHEDULE", "DETAIL", "LEGEND", "NOTES", "RISER", "SECTION", "DIAGRAM", "SYMBOLS", "REPORT", "ABBREVIATION", "ELEVATION", "COVER", "INDEX", "CONTROLS")

_DISCIPLINE_BY_PREFIX = {
    "M": "Mechanical", "MD": "Mechanical", "MH": "Mechanical", "H": "Mechanical", "HV": "Mechanical", "HVAC": "Mechanical",
    "E": "Electrical", "EL": "Electrical", "P": "Plumbing", "PL": "Plumbing", "A": "Architectural", "AD": "Architectural", "AI": "Architectural",
    "S": "Structural", "C": "Civil", "L": "Landscape", "G": "General", "T": "General", "F": "Fire Protection", "FP": "Fire Protection",
    "FA": "Fire Protection", "I": "Interiors", "ID": "Interiors", "Q": "Equipment",
}


@dataclass(frozen=True)
class SheetIdentity:
    sheet_number: str | None
    title: str | None
    discipline: str | None
    kind: str
    confidence: str          # "title-block", "bottom-band", "page", or "none"
    notes: tuple[str, ...] = ()


def identify(facts: SheetFacts) -> SheetIdentity:
    if not facts.has_text and not facts.spans:
        return SheetIdentity(None, None, None, "Other", "none", ("The page has no text layer, so it cannot be identified without reading it as an image.",))

    number_span, confidence = _find_number(facts)
    title = _find_title(facts, number_span)
    number = number_span.text if number_span else None
    return SheetIdentity(number, title, _discipline(number, title), _kind(title), confidence if number_span else "none")


def _norm(span: Span, facts: SheetFacts) -> tuple[float, float]:
    return span.box.x0 / facts.width, span.box.y0 / facts.height


def _find_number(facts: SheetFacts) -> tuple[Span | None, str]:
    def candidates(pred) -> list[Span]:
        return [s for s in facts.spans if not s.vertical and SHEET_NO.match(s.text) and not _NOT_A_SHEET.match(s.text) and pred(*_norm(s, facts))]

    for confidence, pred in (
        ("title-block", lambda x, y: x >= TITLE_STRIP_FROM),
        ("bottom-band", lambda x, y: y >= BOTTOM_BAND_FROM),
        ("page", lambda x, y: True),
    ):
        found = candidates(pred)
        if found:
            # The sheet number is the largest such text in the title block: designers make it the biggest thing there.
            return max(found, key=lambda s: (s.size, s.box.y0)), confidence
    return None, "none"


def _usable(span: Span, facts: SheetFacts) -> bool:
    t = span.text
    if span.vertical or len(t) < 3 or _NOISE.match(t) or _DATE.search(t) or _PHONE.search(t):
        return False
    if sum(c.isdigit() for c in t) > 0.4 * len(t):
        return False
    return not (_ADDRESS.search(t) and any(c.isdigit() for c in t))


def _find_title(facts: SheetFacts, number: Span | None) -> str | None:
    strip = [s for s in facts.spans if _norm(s, facts)[0] >= TITLE_STRIP_FROM and _usable(s, facts) and s is not number]
    if not strip and number is not None:
        strip = [s for s in facts.spans if _norm(s, facts)[1] >= BOTTOM_BAND_FROM and _usable(s, facts) and s is not number]
    if not strip:
        return None

    # Group stacked lines of one size into one block of text: "SUBGRADE LEVEL S1" / "FLOOR PLAN -" / "MECHANICAL".
    groups: list[list[Span]] = []
    for span in sorted(strip, key=lambda s: (round(s.box.y0), s.box.x0)):
        last = groups[-1][-1] if groups else None
        if (last is not None and abs(span.size - last.size) < 0.7 and (span.box.y0 - last.box.y1) < 1.6 * span.size
                and (abs(span.box.x0 - last.box.x0) < 0.04 * facts.width or abs(span.box.cx - last.box.cx) < 0.02 * facts.width)):
            groups[-1].append(span)
        else:
            groups.append([span])

    def score(group: list[Span]) -> float:
        text = " ".join(s.text for s in group).upper()
        value = 100.0 if any(w in text for w in _TITLE_WORDS) else 0.0
        value += group[0].size
        if number is not None:
            # The title sits just above the sheet number, so nearness above it counts for more than size.
            gap = (number.box.y0 - group[-1].box.y1) / facts.height
            value -= 40.0 * gap if gap >= 0 else 60.0 + 40.0 * -gap
        return value

    best = max(groups, key=score)
    return " ".join(s.text for s in best)


def _discipline(number: str | None, title: str | None) -> str | None:
    if number:
        prefix = re.match(r"^[A-Z]+", number).group(0)
        for length in (len(prefix), 2, 1):
            found = _DISCIPLINE_BY_PREFIX.get(prefix[:length])
            if found:
                return found
    upper = (title or "").upper()
    for word, discipline in (("MECHANICAL", "Mechanical"), ("HVAC", "Mechanical"), ("ELECTRICAL", "Electrical"), ("PLUMBING", "Plumbing"), ("ARCHITECTURAL", "Architectural")):
        if word in upper:
            return discipline
    return None


def _kind(title: str | None) -> str:
    t = (title or "").upper()
    if not t:
        return "Other"
    # Demolition sheets show what is coming out, not what is being supplied, so they are not plans to count.
    if "DEMOLITION" in t or re.search(r"\bDEMO\b", t):
        return "Other"
    if "SCHEDULE" in t:
        return "Schedule"
    if "LEGEND" in t or "SYMBOL" in t or "ABBREVIATION" in t:
        return "Legend"
    if "RISER" in t:
        return "Riser"
    if "DETAIL" in t or "SECTION" in t:
        return "Detail"
    if "NOTES" in t:
        return "Notes"
    if "PLAN" in t or "LAYOUT" in t:
        return "Plan"
    return "Other"


def to_page_report(facts: SheetFacts, identity: SheetIdentity, views: list[dict] | None = None) -> dict:
    """The camelCase shape the worker sends to the server's sheet registration."""
    return {
        "pageNo": facts.page_no, "widthPts": round(facts.width, 2), "heightPts": round(facts.height, 2), "rotation": facts.rotation,
        "sheetNumber": identity.sheet_number, "title": identity.title, "discipline": identity.discipline, "kind": identity.kind,
        "contentHash": facts.content_hash, "hasTextLayer": facts.has_text, "views": views or [],
    }
