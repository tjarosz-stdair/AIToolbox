"""Finds the views on a sheet: each plan, section or detail drawn on the page, with its title, number and scale (plan section 6.7).

A CAD sheet often carries several views, each with its own scale, so the scale has to belong to the view and not to the
sheet. Every view is announced by a scale label ("1/8" = 1'-0"", or NTS) with its title directly above it and often a
number in a bubble beside the title, so the labels are the anchors. A sheet with one label is one view over the whole
drawing area. A sheet with several is divided: labels are grouped into columns by position, each row runs from one label
to the next, and the edge between two columns is the widest empty stretch of line-work between them.

Everything here is a proposal. A person can rename a view, redraw its area or change its scale on the Setup tab.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from .pdf_layout import TITLE_STRIP_FROM, SheetFacts, Span

SCALE_RE = re.compile(
    r"""(?P<paper>\d+(?:/\d+)?(?:\.\d+)?)\s*(?:"|IN\.?|INCH)\s*=\s*(?P<ft>\d+)\s*'\s*-?\s*(?P<inch>\d+)?\s*(?:"|IN)?""", re.I)
NTS_RE = re.compile(r"^(?:SCALE:?\s*)?(NTS|N\.T\.S\.?|NO SCALE|NOT TO SCALE)$", re.I)
_SCALE_WORD = re.compile(r"^SCALE:?$", re.I)


def parse_scale(text: str) -> float | None:
    """Real length per drawn length: 1/4" = 1'-0" is 48 (a quarter inch on paper stands for a foot, which is 48 quarter inches)."""
    m = SCALE_RE.search(text)
    if not m:
        return None
    p = m.group("paper")
    paper_inches = (int(p.split("/")[0]) / int(p.split("/")[1])) if "/" in p else float(p)
    feet = int(m.group("ft")) + (int(m.group("inch")) / 12 if m.group("inch") else 0)
    if paper_inches <= 0 or feet <= 0:
        return None
    return round(12.0 * feet / paper_inches, 4)


@dataclass(frozen=True)
class DetectedView:
    number: str | None
    title: str
    view_type: str                           # Plan, Section, Elevation, Detail, Schedule, Riser or Other
    scale_text: str | None                   # as printed, or None
    ratio: float | None                      # None for NTS or when it cannot be read
    region: tuple[float, float, float, float] | None   # x0, y0, x1, y1 in canonical space; None means the whole drawing area
    title_found: bool


@dataclass(frozen=True)
class _Anchor:
    x: float
    y0: float
    y1: float
    size: float
    text: str
    ratio: float | None


def find_views(facts: SheetFacts, default_type: str = "Plan") -> list[DetectedView]:
    anchors = _anchors(facts)
    if not anchors:
        return []

    titles = [_title(facts, a) for a in anchors]
    if len(anchors) == 1:
        regions: list[tuple[float, float, float, float] | None] = [None]
    else:
        regions = _partition(facts, anchors, titles)

    views = []
    for anchor, (title, number, top), region in zip(anchors, titles, regions):
        views.append(DetectedView(number, title or "", _view_type(title, default_type), anchor.text, anchor.ratio, region, title is not None))
    # Reading order: top to bottom, left to right.
    order = sorted(range(len(views)), key=lambda i: (round(anchors[i].y0 / 300), anchors[i].x))
    named = [views[i] for i in order]
    return [v if v.title else DetectedView(v.number, f"View {n}", v.view_type, v.scale_text, v.ratio, v.region, False) for n, v in enumerate(named, 1)]


def default_view_type(sheet_kind: str) -> str:
    """The kind of view a sheet's kind implies, for a view whose own title names none."""
    return {"Plan": "Plan", "Detail": "Detail", "Schedule": "Schedule", "Riser": "Riser"}.get(sheet_kind, "Other")


def to_report(views: list[DetectedView]) -> list[dict]:
    """The camelCase shape the server takes: the area as a four-corner polygon in canonical points, or null for the whole drawing area."""
    return [
        {
            "title": v.title, "number": v.number, "type": v.view_type, "scaleRatio": v.ratio,
            "polygon": None if v.region is None else [[v.region[0], v.region[1]], [v.region[2], v.region[1]], [v.region[2], v.region[3]], [v.region[0], v.region[3]]],
        }
        for v in views
    ]


def _anchors(facts: SheetFacts) -> list[_Anchor]:
    strip = facts.width * TITLE_STRIP_FROM
    spans = [s for s in facts.spans if not s.vertical and s.box.x0 < strip]
    found: list[_Anchor] = []
    for s in spans:
        text = s.text.strip()
        ratio = parse_scale(text)
        nts = bool(NTS_RE.match(text))
        if ratio is None and not nts:
            continue
        # "NTS" as a word in a table of abbreviations is followed by its meaning on the same line. A scale label stands alone.
        if ratio is None and any(
                o is not s and abs(o.box.y0 - s.box.y0) < 4 and not _SCALE_WORD.match(o.text.strip())
                and (0 <= o.box.x0 - s.box.x1 < 60 or 0 <= s.box.x0 - o.box.x1 < 60) for o in spans):
            continue
        x = s.box.x0
        # "SCALE:" is often its own run just left of the scale itself, and the label begins at the word.
        for w in spans:
            if _SCALE_WORD.match(w.text.strip()) and abs(w.box.y0 - s.box.y0) < 6 and 0 < s.box.x0 - w.box.x0 < 80:
                x = min(x, w.box.x0)
        if any(abs(a.x - x) < 3 and abs(a.y0 - s.box.y0) < 3 for a in found):
            continue
        found.append(_Anchor(x, s.box.y0, s.box.y1, s.size, text, ratio))
    return found


def _title(facts: SheetFacts, a: _Anchor) -> tuple[str | None, str | None, float]:
    """(title, number, top of the title block of text) for the view a label belongs to."""
    def usable(s: Span) -> bool:
        t = s.text.strip()
        return not s.vertical and len(t) >= 3 and parse_scale(t) is None and not NTS_RE.match(t) and not _SCALE_WORD.match(t)

    above = [s for s in facts.spans if usable(s) and 0 < a.y0 - s.box.y0 <= 60 and abs(s.box.x0 - a.x) <= 40]
    if not above:
        return None, None, a.y0
    nearest = min(above, key=lambda s: a.y0 - s.box.y0)
    lines = [nearest]
    # A title may run over several lines of one size.
    while True:
        prior = [s for s in above if s not in lines and abs(s.size - nearest.size) < 0.7 and 0 < lines[0].box.y0 - s.box.y0 < 1.8 * nearest.size]
        if not prior:
            break
        lines.insert(0, max(prior, key=lambda s: s.box.y0))

    title = " ".join(s.text.strip() for s in lines)
    top = lines[0].box.y0
    bubbles = [s for s in facts.spans if 0 < len(s.text.strip()) <= 2 and s.text.strip().isalnum()
               and nearest.box.x0 - 70 <= s.box.x0 < nearest.box.x0 - 1 and abs(s.box.y0 - nearest.box.y0) <= 30]
    number = min(bubbles, key=lambda s: abs(s.box.y0 - nearest.box.y0)).text.strip() if bubbles else None
    return title, number, top


def _view_type(title: str | None, default: str) -> str:
    t = (title or "").upper()
    for word, kind in (("SECTION", "Section"), ("ELEVATION", "Elevation"), ("DETAIL", "Detail"), ("SCHEDULE", "Schedule"), ("RISER", "Riser"), ("DIAGRAM", "Riser"), ("PLAN", "Plan")):
        if word in t:
            return kind
    # A view on a detail sheet that names no kind is a detail; elsewhere it is whatever the sheet is.
    return default if default != "Other" else "Detail"


# ------------------------------------------------------------------ dividing a sheet with several views


def _partition(facts: SheetFacts, anchors: list[_Anchor], titles) -> list[tuple[float, float, float, float]]:
    strip = facts.width * TITLE_STRIP_FROM
    ink = facts.ink

    # Columns: labels whose left edges are close belong to one column.
    order = sorted(range(len(anchors)), key=lambda i: anchors[i].x)
    columns: list[list[int]] = []
    for i in order:
        if columns and anchors[i].x - anchors[columns[-1][-1]].x < 0.12 * facts.width:
            columns[-1].append(i)
        else:
            columns.append([i])
    centres = [sum(anchors[i].x for i in c) / len(c) for c in columns]

    # Does a view sit above its title or below it? Look at the strip just outside each title block: line-work hard against
    # one side means the view is on that side. (A wider window is no good: stacked views have line-work on both sides.)
    above = below = 0
    for a, (_, _, top) in zip(anchors, titles):
        for x, y in ink:
            if a.x - 20 < x < a.x + 900:
                if top - 70 < y < top - 4:
                    above += 1
                elif a.y1 + 4 < y < a.y1 + 70:
                    below += 1
    view_above_title = above >= below

    result: dict[int, tuple[float, float, float, float]] = {}
    for ci, column in enumerate(columns):
        column.sort(key=lambda i: anchors[i].y0)
        for k, i in enumerate(column):
            a, (_, _, top) = anchors[i], titles[i]
            if view_above_title:
                y0 = anchors[column[k - 1]].y1 if k > 0 else 0.0
                y1 = top
            else:
                y0 = a.y1
                y1 = titles[column[k + 1]][2] if k + 1 < len(column) else facts.height
            left = 0.0 if ci == 0 else _gap_edge(ink, y0, y1, centres[ci - 1] + 60, centres[ci] - 5, (centres[ci - 1] + centres[ci]) / 2)
            right = strip if ci == len(columns) - 1 else _gap_edge(ink, y0, y1, centres[ci] + 60, centres[ci + 1] - 5, (centres[ci] + centres[ci + 1]) / 2)
            result[i] = (round(max(0.0, left), 1), round(max(0.0, y0), 1), round(min(strip, right), 1), round(min(facts.height, y1), 1))
    return [result[i] for i in range(len(anchors))]


def _gap_edge(ink: list[tuple[float, float]], y0: float, y1: float, xa: float, xb: float, default: float, min_gap: float = 20.0) -> float:
    """The middle of the right-most empty stretch (at least ``min_gap`` wide) between two neighbouring views, in a row band."""
    if xb <= xa:
        return default
    xs = [x for x, y in ink if y0 < y < y1 and xa - 200 < x < xb + 200]
    if not xs:
        return default
    step = 5.0
    bins = int((xb - xa) / step) + 1
    counts = [0] * bins
    for x in xs:
        index = int((x - xa) / step)
        if 0 <= index < bins:
            counts[index] += 1

    best: float | None = None
    i = 0
    while i < bins:
        if counts[i] == 0:
            j = i
            while j < bins and counts[j] == 0:
                j += 1
            if (j - i) * step >= min_gap:
                best = xa + (i + j) * step / 2
            i = j
        else:
            i += 1
    return best if best is not None else default
