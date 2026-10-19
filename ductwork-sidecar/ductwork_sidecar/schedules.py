"""Reads equipment schedules from a sheet's text (detector family A, plan section 6.7).

A schedule is a table under a title like "EXHAUST FAN SCHEDULE": a header row that begins with MARK (or TAG), then
one row per piece of equipment. It is read from where the text sits, not from ruled lines, so a table drawn without
rules reads the same as one drawn with them, and nothing has to be rendered or analysed as line-work (which is slow
on a sheet with a hundred thousand paths).

A schedule gives what each mark IS (type, maker, model, capacity), not how many there are: that is how many times
the mark appears on the plans. So the schedule's rows are read here, the marks are counted on the plan sheets by
``count_marks``, and a quantity is proposed only where the drawing gives one. Everything is a proposal.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from .pdf_layout import SheetFacts, Span

# A mark: letters, then dashed parts: EF-1, SSAC-S2-1, JF-XX-#, AHU-1. A few schedules use FCU1 style.
MARK_RE = re.compile(r"^[A-Z]{1,6}[A-Z0-9]*(?:-[A-Z0-9#.]+)+$|^[A-Z]{1,5}\d{1,3}[A-Z]?$")
_MARK_HEADER = re.compile(r"^(TYPE MARK|MARK|TAG|SYMBOL|UNIT|ID|EQUIP(?:MENT)?\.?\s*(?:TAG|NO\.?|MARK|ID))$", re.I)
_TITLE = re.compile(r"\bSCHEDULES?\b|\bLIST\b", re.I)
_QTY_HEADER = re.compile(r"^(QTY\.?|QUANTITY|NO\. OF UNITS|QTY\.? REQ'?D)$", re.I)


@dataclass
class ScheduleRow:
    mark: str
    fields: dict[str, str]
    box: tuple[float, float, float, float]
    description: str = ""
    location: str = ""
    manufacturer: str = ""
    model: str = ""
    model2: str = ""
    capacity: str = ""
    size: str = ""                       # neck, panel or overall size, as printed
    quantity: float | None = None        # only when the schedule has a quantity column


@dataclass
class Schedule:
    sheet: str
    page_no: int
    title: str
    headers: list[str]
    rows: list[ScheduleRow] = field(default_factory=list)


def read_schedules(facts: SheetFacts, sheet: str) -> list[Schedule]:
    """Every schedule on the page, in reading order."""
    spans = [s for s in facts.spans if not s.vertical]
    body = _body(spans)
    titles = sorted((s for s in spans if _TITLE.search(s.text) and s.size >= body * 1.25 and len(s.text) <= 70), key=lambda s: (s.box.y0, s.box.x0))
    marks = [s for s in spans if _MARK_HEADER.match(s.text.strip())]

    schedules = []
    for band in _bands(titles):
        for title, left_mark, right in _tables_in(band, marks, spans, titles):
            schedule = _read_one(spans, titles, title, left_mark, right, sheet, facts.page_no)
            if schedule and schedule.rows:
                schedules.append(schedule)
    schedules.sort(key=lambda sc: (sc.rows[0].box[1], sc.rows[0].box[0]))
    return schedules


def _bands(titles: list[Span]) -> list[list[Span]]:
    """Titles on one line of the page (side-by-side tables), left to right."""
    bands: list[list[Span]] = []
    for t in titles:
        if bands and abs(t.box.y0 - bands[-1][0].box.y0) < 12:
            bands[-1].append(t)
        else:
            bands.append([t])
    return [sorted(b, key=lambda s: s.box.x0) for b in bands]


def _tables_in(band: list[Span], marks: list[Span], spans: list[Span], all_titles: list[Span]):
    """Pairs each title in a band with the mark column that starts its table, and says where the table ends on the right.

    The mark header words just under the titles are the starts of columns. A title is centred over its own table, so the
    marks are grouped, in order, one group per title, in the way that puts each title nearest the middle of its group's
    header row. (One title over two MARK words is one table with a second mark column, such as an outdoor unit's mark.)"""
    top = max(t.box.y1 for t in band)
    others = [t for t in all_titles if t not in band]
    near = [m for m in marks if 0 < m.box.y0 - top < 320 and not any(top < t.box.y0 < m.box.y0 for t in others)]
    if not near:
        return []
    row_y = min(m.box.y0 for m in near)
    row = sorted((m for m in near if abs(m.box.y0 - row_y) < 8), key=lambda m: m.box.x0)
    if not row:
        return []

    best, best_cost = None, float("inf")
    for cuts in _partitions(len(row), len(band)):
        groups = [row[a:b] for a, b in cuts]
        extents = [_extent(spans, g[0], row[cuts[i][1]] if cuts[i][1] < len(row) else None) for i, g in enumerate(groups)]
        cost = sum(abs(t.box.cx - (e[0] + e[1]) / 2) for t, e in zip(band, extents))
        if cost < best_cost:
            best, best_cost = list(zip(band, groups, extents)), cost
    return [(t, g[0], e[1]) for t, g, e in best]


def _partitions(n: int, k: int):
    """Every way to cut n marks, in order, into k non-empty consecutive groups, as (start, end) pairs."""
    if k > n:
        k = n
    if k == 1:
        yield [(0, n)]
        return
    for first in range(1, n - k + 2):
        for rest in _partitions(n - first, k - 1):
            yield [(0, first)] + [(a + first, b + first) for a, b in rest]


def _extent(spans: list[Span], first: Span, next_start: Span | None) -> tuple[float, float]:
    """Left and right edge of a table whose header row begins at ``first`` and stops where the next table's begins or the row has a wide gap."""
    # The header block (which may be several lines, and whose cells are not all on the MARK line) and the first row of data.
    row = sorted((s for s in spans if first.box.y0 - 40 <= s.box.y0 <= first.box.y0 + 60 and s.box.x0 >= first.box.x0 - 45), key=lambda s: s.box.x0)
    limit = next_start.box.x0 - 4 if next_start is not None else float("inf")
    right = first.box.x1
    for s in row:
        if s.box.x0 >= limit:
            break
        if s.box.x0 - right > 420:
            break
        right = max(right, s.box.x1)
    return first.box.x0, right


def _read_one(spans: list[Span], titles: list[Span], title: Span, mark: Span, right: float, sheet: str, page_no: int) -> Schedule | None:
    # The table ends at the next title below it over the same columns, or the bottom of the sheet.
    stop = min((t.box.y0 for t in titles if t.box.y0 > mark.box.y0 + 5 and mark.box.x0 - 50 <= t.box.cx <= right + 50), default=float("inf"))
    left = mark.box.x0 - 45
    region = [s for s in spans if left <= s.box.x0 <= right + 60 and s.box.y0 < stop - 2 and s.box.y0 > title.box.y1 + 2]
    lines = _lines(region)

    # The data starts at the first line below the header row whose first cell is a mark.
    data_from = next((i for i, line in enumerate(lines) if line[0].box.y0 > mark.box.y0 + 2 and _is_mark_line(line, mark)), None)
    if data_from is None:
        return None

    data_lines = []
    misses = 0
    for line in lines[data_from:]:
        if _is_mark_line(line, mark):
            data_lines.append((line, True))
            misses = 0
        elif data_lines and not _first_cell(line, mark) and line[0].box.y0 - data_lines[-1][0][0].box.y0 < 24:
            data_lines.append((line, False))       # a cell that wrapped onto a second line
        else:
            misses += 1
            if misses >= 2:
                break
    if not data_lines:
        return None

    first_data_y = min(s.box.y0 for s in lines[data_from])
    header_spans = [s for s in region if s.box.y0 < first_data_y - 2]
    columns = _columns([line for line, _ in data_lines], header_spans, mark)
    if len(columns) < 3:
        return None

    schedule = Schedule(sheet, page_no, " ".join(title.text.split()), [c.name for c in columns])
    pending: dict[int, str] | None = None
    box = None
    rows: list[tuple[dict[int, str], tuple[float, float, float, float]]] = []
    for line, is_mark in data_lines:
        cells = _assign(line, columns)
        if is_mark:
            rows.append((cells, _box(line)))
        elif rows:
            for c, text in cells.items():
                rows[-1][0][c] = (rows[-1][0].get(c, "") + " " + text).strip()
    for cells, box in rows:
        row = _to_row(columns, cells, box)
        if row:
            schedule.rows.append(row)
    return schedule


def _lines(spans: list[Span]) -> list[list[Span]]:
    """Spans grouped into lines by their top edge, top to bottom, each line left to right."""
    lines: list[list[Span]] = []
    for s in sorted(spans, key=lambda s: (s.box.y0, s.box.x0)):
        if lines and abs(s.box.y0 - lines[-1][0].box.y0) < 3.5:
            lines[-1].append(s)
        else:
            lines.append([s])
    return [sorted(line, key=lambda s: s.box.x0) for line in lines]


def _first_cell(line: list[Span], mark: Span) -> str:
    cell = [s for s in line if s.box.x0 < mark.box.x0 + 70]
    return "".join(" ".join(s.text.split()) for s in cell).replace(" ", "").upper()


def _is_mark_line(line: list[Span], mark: Span) -> bool:
    first = _first_cell(line, mark)
    return bool(first) and (bool(MARK_RE.match(first)) or bool(re.fullmatch(r"[A-Z]{1,6}-[A-Z0-9#.]+(-[A-Z0-9#.]+)*", first)))


def _columns(data_lines: list[list[Span]], header_spans: list[Span], mark: Span) -> list[_Column]:
    """Columns are the bands of the page that hold data in some row, separated by gaps that hold none, named by the header text over them."""
    bands: list[list[float]] = []
    for s in sorted((s for line in data_lines for s in line), key=lambda s: s.box.x0):
        if bands and s.box.x0 <= bands[-1][1] + 2.5:
            bands[-1][1] = max(bands[-1][1], s.box.x1)
        else:
            bands.append([s.box.x0, s.box.x1])
    columns = [_Column("", b[0], b[1]) for b in bands]

    names: list[list[tuple[float, str]]] = [[] for _ in columns]
    for h in header_spans:
        hit = [i for i, c in enumerate(columns) if min(h.box.x1, c.x1 + 3) - max(h.box.x0, c.x0 - 3) > 0]
        if not hit:
            hit = [min(range(len(columns)), key=lambda i: abs(columns[i].cx - (h.box.x0 + h.box.x1) / 2))]
        text = " ".join(h.text.split())
        words = text.split(" ")
        if len(hit) > 1 and len(words) == len(hit):
            for i, w in zip(hit, words):                       # "LOCATION CONFIGURATION" over two columns: one word each
                names[i].append((h.box.y0, w))
        else:
            for i in hit[:1] if len(hit) > 1 else hit:
                names[i].append((h.box.y0, text))
    for c, parts in zip(columns, names):
        # Stacked header lines read top to bottom: "HEATING" over "CAPACITY (KW)".
        c.name = " / ".join(t for _, t in sorted(parts, key=lambda p: p[0])).strip()
    for i, c in enumerate(columns):
        if not c.name:
            c.name = "MARK" if i == 0 else f"col{i}"
    columns[0].name = columns[0].name or "MARK"
    return columns


def _box(line: list[Span]) -> tuple[float, float, float, float]:
    return (min(s.box.x0 for s in line), min(s.box.y0 for s in line), max(s.box.x1 for s in line), max(s.box.y1 for s in line))


def _assign(line: list[Span], columns: list[_Column]) -> dict[int, str]:
    cells: dict[int, list[str]] = {}
    for s in sorted(line, key=lambda s: s.box.x0):
        best = max(range(len(columns)), key=lambda i: (_overlap(s, columns[i]), -abs(s.box.cx - columns[i].cx)))
        cells.setdefault(best, []).append(" ".join(s.text.split()))
    return {c: " ".join(t).strip() for c, t in cells.items()}


def _overlap(s: Span, h: _Column) -> float:
    return max(0.0, min(s.box.x1, h.x1 + 1) - max(s.box.x0, h.x0 - 1))


def _to_row(headers: list[_Column], cells: dict[int, str], box) -> ScheduleRow | None:
    mark = cells.get(0, "").replace(" ", "").upper()
    if not mark:
        return None
    fields = {}
    for i, h in enumerate(headers):
        key = h.name if h.name not in fields else f"{h.name} ({i})"
        fields[key] = cells.get(i, "")

    names = [h.name for h in headers]
    values = [cells.get(i, "") for i in range(len(headers))]
    row = ScheduleRow(mark, fields, tuple(round(v, 1) for v in box))
    row.description = _find(names, values, r"(^|/ )TYPE$", r"CONFIG", r"DESCRIPTION", r"SERVICE")
    row.location = _find(names, values, r"SERVES", r"LOCATION", r"AREA SERVED", r"ROOM")
    row.manufacturer = _find(names, values, r"MANUFACTURER|MFGR|MFR")
    models = [v for n, v in zip(names, values) if re.search(r"MODEL", _flat(n), re.I) and v and v != "-"]
    if models:
        row.model = models[0]
        if len(models) > 1:
            row.model2 = models[1]
    row.capacity = _capacity(names, values)
    row.size = _find(names, values, r"NECK SIZE", r"\bSIZE\b", r"DIMENSIONS|\(WXDXH\)|\(LXWXH\)")

    qty_col = next((i for i, n in enumerate(names) if _QTY_HEADER.search(n.split(" / ")[-1].strip())), None)
    if qty_col is not None:
        m = re.match(r"(\d+(?:\.\d+)?)", values[qty_col] or "")
        if m:
            row.quantity = float(m.group(1))
    return row


def _flat(name: str) -> str:
    return " ".join(name.replace(" / ", " ").split())


def _find(names: list[str], values: list[str], *patterns: str) -> str:
    for p in patterns:
        for n, v in zip(names, values):
            if v and v != "-" and re.search(p, _flat(n), re.I):
                return v
    return ""


def _capacity(names: list[str], values: list[str]) -> str:
    parts = []
    for pattern, unit in ((r"AIR ?FLOW|\bCFM\b|MAX(IMUM)? FLOW", "CFM"), (r"NOMINAL CAPACITY \(TONS\)", "tons"), (r"TOTAL COOLING.*\(BTUH\)", "BTUH clg"),
                          (r"TOTAL COOLING \(MBH\)", "MBH clg"), (r"HEATING CAPACITY \(KW\)|\(KW\)|WATTS", "kW")):
        for n, v in zip(names, values):
            if v and v != "-" and re.search(pattern, _flat(n), re.I) and re.fullmatch(r"[\d,.]+(\s?(CFM|W|KW|MBH|BTUH|TONS?))?", v.strip(), re.I):
                parts.append(v if re.search(r"[A-Za-z]", v) else f"{v} {unit}")
                break
    return "; ".join(parts[:3])


# ---------------------------------------------------------------- how many times each mark is drawn on the plans


def mark_regex(mark: str) -> re.Pattern:
    """A scheduled mark with placeholders (XX, #) as a pattern: JF-XX-# matches JF-A2-17."""
    out, i = "", 0
    while i < len(mark):
        if mark.startswith("XXX", i):
            out, i = out + r"[A-Z0-9.]+", i + 3
        elif mark.startswith("XX", i):
            out, i = out + r"[A-Z0-9.]+", i + 2
        elif mark[i] == "#":
            out, i = out + r"\d+", i + 1
        else:
            out, i = out + re.escape(mark[i]), i + 1
    return re.compile("^" + out + "$", re.I)


@dataclass(frozen=True)
class MarkCount:
    mark: str
    sheet: str
    page_no: int
    count: int
    box: tuple[float, float, float, float]    # where the first one is


_SUFFIX = re.compile(r"^[A-Z0-9][A-Z0-9.\-]{0,14}$")


def count_marks(facts: SheetFacts, sheet: str, marks: list[str]) -> list[MarkCount]:
    """How many times each scheduled mark is written on this plan page.

    A tag is a short label beside a symbol, written on one line (EF-L1-2) or stacked in a bubble with the prefix over the rest
    (EF over L1-2). A sentence in a note that names the mark is not a tag, so only short pieces of text are read."""
    patterns = {m: mark_regex(m) for m in marks}
    prefixes = {m.split("-", 1)[0].upper() for m in marks if "-" in m and re.fullmatch(r"[A-Za-z]{1,6}", m.split("-", 1)[0])}
    found: dict[str, list[tuple[float, float, float, float]]] = {m: [] for m in marks}

    def hit(tag: str, box: tuple[float, float, float, float]) -> None:
        for m, rx in patterns.items():
            if rx.match(tag):
                found[m].append(box)

    spans = [s for s in facts.spans if len(s.text) <= 40 and len(s.text.split()) <= 6]
    for s in spans:
        for token in re.split(r"[\s,;()/]+", s.text.strip()):
            token = token.strip(".:")
            if token:
                hit(token, (s.box.x0, s.box.y0, s.box.x1, s.box.y1))

    # Stacked tags: a prefix with the rest of the tag directly below it, centred under it.
    suffixes = [s for s in spans if not s.vertical and _SUFFIX.match(s.text.strip().upper()) and re.search(r"\d", s.text) and len(s.text.strip()) <= 15]
    taken: set[int] = set()
    for p in spans:
        if p.vertical or p.text.strip().upper() not in prefixes:
            continue
        candidates = []
        for i, q in enumerate(suffixes):
            if i in taken:
                continue
            down = q.box.cy - p.box.cy
            along = abs(q.box.cx - p.box.cx)
            h = max(p.size, q.size)
            if 0 < down <= 2.2 * h + 3 and along <= max(p.box.width, q.box.width) / 2 + 4:
                candidates.append((down, i, q))
        if candidates:
            _, i, q = min(candidates)
            taken.add(i)
            hit(f"{p.text.strip().upper()}-{q.text.strip().upper()}", (min(p.box.x0, q.box.x0), min(p.box.y0, q.box.y0), max(p.box.x1, q.box.x1), max(p.box.y1, q.box.y1)))

    result = []
    for m, boxes in found.items():
        if boxes:
            first = min(boxes, key=lambda b: (b[1], b[0]))
            result.append(MarkCount(m, sheet, facts.page_no, len(boxes), first))
    return result

# ---------------------------------------------------------------- what this reader is, for the result's producer


ALGORITHM_NAME = "schedule-reader"
ALGORITHM_VERSION = "0.1.0"
TASK_TYPE = "read-schedules"


def params_hash() -> str:
    """A hash of everything that changes what the reader emits: its version and the patterns it recognises things by."""
    import hashlib
    import json

    material = {
        "version": ALGORITHM_VERSION, "mark": MARK_RE.pattern, "header": _MARK_HEADER.pattern, "title": _TITLE.pattern, "qty": _QTY_HEADER.pattern,
        "titleFactor": 1.25, "gap": 420,
    }
    return hashlib.sha256(json.dumps(material, sort_keys=True).encode("utf-8")).hexdigest()


def producer() -> dict:
    return {"kind": "Algorithm", "algorithmName": ALGORITHM_NAME, "algorithmVersion": ALGORITHM_VERSION, "paramsHash": params_hash()}


def schedule_to_report(schedule: Schedule) -> dict:
    return {
        "sheet": schedule.sheet, "page": schedule.page_no, "title": schedule.title, "headers": schedule.headers,
        "rows": [
            {
                "mark": r.mark, "description": r.description, "location": r.location, "manufacturer": r.manufacturer, "model": r.model, "model2": r.model2,
                "capacity": r.capacity, "size": r.size, "quantity": r.quantity, "box": list(r.box),
            }
            for r in schedule.rows
        ],
    }


def _body(spans: list[Span]) -> float:
    """The usual text size on the page, so titles can be told from body text by being larger."""
    sizes = sorted(round(s.size, 1) for s in spans)
    return sizes[len(sizes) // 2] if sizes else 9.0


@dataclass
class _Column:
    name: str
    x0: float
    x1: float

    @property
    def cx(self) -> float:
        return (self.x0 + self.x1) / 2
