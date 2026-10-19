"""The only module that touches the PDF library.

Everything it returns is in canonical space: the page as displayed (after /Rotate), origin at the
top-left, units of 1/72 inch. PyMuPDF reports text and drawing coordinates in *unrotated* page space,
so every coordinate is mapped through the page's rotation matrix here and nowhere else. Forgetting
that on a rotated sheet silently puts every label in the wrong place, which is why it is one function.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Iterable


@dataclass(frozen=True)
class Box:
    x0: float
    y0: float
    x1: float
    y1: float

    @property
    def width(self) -> float:
        return self.x1 - self.x0

    @property
    def height(self) -> float:
        return self.y1 - self.y0

    @property
    def cx(self) -> float:
        return (self.x0 + self.x1) / 2

    @property
    def cy(self) -> float:
        return (self.y0 + self.y1) / 2

    @property
    def area(self) -> float:
        return max(0.0, self.width) * max(0.0, self.height)

    def contains_point(self, x: float, y: float, margin: float = 0.0) -> bool:
        return self.x0 - margin <= x <= self.x1 + margin and self.y0 - margin <= y <= self.y1 + margin

    def to_contract(self) -> dict:
        return {"x0": round(self.x0, 2), "y0": round(self.y0, 2), "x1": round(self.x1, 2), "y1": round(self.y1, 2)}


@dataclass(frozen=True)
class TextLine:
    text: str
    box: Box


@dataclass(frozen=True)
class TextWord:
    text: str
    box: Box


@dataclass
class PageLayout:
    """Text, symbols and the sheet border of one page, in canonical space."""

    width: float
    height: float
    rotation: int
    lines: list[TextLine]
    words: list[TextWord]
    triangles: list[Box]
    border: Box | None
    # Where the text came from: the PDF's own text layer, or OCR. OCR can misread, so detectors trust it less.
    text_source: str = "text-layer"

    @property
    def has_text(self) -> bool:
        return bool(self.words)


def load_page_layout(path: str, page_index: int = 0) -> PageLayout:
    # Imported here so the rest of the package imports without the PDF library installed.
    import pymupdf

    with pymupdf.open(path) as doc:
        page = doc[page_index]
        return _layout_from_page(page, pymupdf)


def _layout_from_page(page, pymupdf) -> PageLayout:
    matrix = page.rotation_matrix

    def to_box(rect) -> Box:
        r = pymupdf.Rect(rect) * matrix
        r.normalize()
        return Box(r.x0, r.y0, r.x1, r.y1)

    lines: list[TextLine] = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            text = "".join(span["text"] for span in line["spans"]).strip()
            if text:
                lines.append(TextLine(text, to_box(line["bbox"])))

    words = [TextWord(w[4], to_box(w[:4])) for w in page.get_text("words")]
    lines.sort(key=lambda l: (round(l.box.y0), l.box.x0))

    drawings = page.get_drawings()
    scale = max(page.rect.width, page.rect.height)
    triangles = [to_box(d["rect"]) for d in drawings if _is_triangle(d, scale)]
    border = _find_border([to_box(d["rect"]) for d in drawings], page.rect.width * page.rect.height)

    # page.rect is already the displayed (rotated) rectangle, so it must not go through the matrix again.
    return PageLayout(
        width=page.rect.width,
        height=page.rect.height,
        rotation=page.rotation,
        lines=lines,
        words=words,
        triangles=triangles,
        border=border,
    )


# Flag triangles are 0.6% to 6% of the long side of the sheet (15 to 160 pt on a 36 in sheet). Sizes are
# relative so the same plan printed smaller, for example in a letter-size bound set, is still found.
_TRIANGLE_MIN = 0.0058
_TRIANGLE_MAX = 0.062


def _is_triangle(drawing: dict, sheet_scale: float = 2592.0) -> bool:
    """A closed or nearly closed path of three straight segments with exactly three distinct corners."""
    items = drawing["items"]
    if not 3 <= len(items) <= 4 or any(item[0] != "l" for item in items):
        return False
    corners = set()
    for item in items:
        corners.add((round(item[1].x), round(item[1].y)))
        corners.add((round(item[2].x), round(item[2].y)))
    rect = drawing["rect"]
    lo, hi = _TRIANGLE_MIN * sheet_scale, _TRIANGLE_MAX * sheet_scale
    return len(corners) == 3 and lo < rect.width < hi and lo < rect.height < hi


def triangle_corners(drawing: dict) -> list[tuple[float, float]]:
    """The three distinct corners of a triangle drawing, in the page's stored coordinates."""
    corners: list[tuple[float, float]] = []
    for item in drawing["items"]:
        for p in (item[1], item[2]):
            if not any(abs(p.x - q[0]) < 0.3 and abs(p.y - q[1]) < 0.3 for q in corners):
                corners.append((p.x, p.y))
    return corners[:3]


def point_in_triangle(x: float, y: float, corners: list[tuple[float, float]]) -> bool:
    (ax, ay), (bx, by), (cx, cy) = corners

    def side(px, py, qx, qy, rx, ry):
        return (px - rx) * (qy - ry) - (qx - rx) * (py - ry)

    d1, d2, d3 = side(x, y, ax, ay, bx, by), side(x, y, bx, by, cx, cy), side(x, y, cx, cy, ax, ay)
    has_neg = d1 < 0 or d2 < 0 or d3 < 0
    has_pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (has_neg and has_pos)


def _find_border(boxes: Iterable[Box], page_area: float) -> Box | None:
    """The sheet border: the largest drawing that still fits inside the page."""
    best: Box | None = None
    for box in boxes:
        if box.area < page_area * 0.5 or box.area > page_area * 0.995:
            continue
        if best is None or box.area > best.area:
            best = box
    return best


# ---------------------------------------------------------------------------------------------------
# Sheet facts: what is needed to say which sheet a page is, and whether it has been seen before.


@dataclass(frozen=True)
class Span:
    """A run of text in one font and size, with its box in canonical space."""

    text: str
    box: Box
    size: float
    vertical: bool = False


@dataclass
class SheetFacts:
    page_no: int          # 1-based, as a person counts pages
    width: float
    height: float
    rotation: int
    spans: list[Span]
    has_text: bool
    content_hash: str     # SHA-256 hex of the drawing area (the title-block strip is left out)
    # Where the line-work and text are, as points (centres of shapes and words), thinned to a bounded number. Used to find the
    # empty gaps between the views on a sheet; not part of the content hash.
    ink: list[tuple[float, float]] = field(default_factory=list)


# The title block sits in a strip at one edge of the sheet. Re-issuing the same drawing under another project
# name, number or date changes that strip and nothing else, so it is left out of the content hash.
TITLE_STRIP_FROM = 0.88
MAX_INK_POINTS = 40000


def load_sheet_facts(path: str, pages: Iterable[int] | None = None, text_only: bool = False) -> list[SheetFacts]:
    """Facts for the given 0-based pages (all by default), reading the file once.

    ``text_only`` skips the line-work (and so the content hash and ink points), which is most of the time on a large sheet;
    it is for readers that need only the text, such as the schedule reader."""
    import pymupdf

    result: list[SheetFacts] = []
    with pymupdf.open(path) as doc:
        for index in (range(len(doc)) if pages is None else pages):
            result.append(_facts_from_page(doc[index], index + 1, pymupdf, text_only))
    return result


def _facts_from_page(page, page_no: int, pymupdf, text_only: bool = False) -> SheetFacts:
    import hashlib

    matrix = page.rotation_matrix
    width, height = page.rect.width, page.rect.height

    def to_box(rect) -> Box:
        r = pymupdf.Rect(rect) * matrix
        r.normalize()
        return Box(r.x0, r.y0, r.x1, r.y1)

    spans: list[Span] = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            for s in line["spans"]:
                text = " ".join(s["text"].split())
                if not text:
                    continue
                box = to_box(s["bbox"])
                spans.append(Span(text, box, float(s["size"]), vertical=box.height > 2.5 * max(box.width, 1.0) and len(text) > 2))

    if text_only:
        return SheetFacts(page_no, width, height, page.rotation, spans, bool(spans), "", [])

    digest = hashlib.sha256()
    strip = width * TITLE_STRIP_FROM
    words = sorted(
        (round(b.x0), round(b.y0), w[4]) for w in page.get_text("words") for b in [to_box(w[:4])] if b.cx <= strip
    )
    for x, y, text in words:
        digest.update(f"W|{x}|{y}|{text}\n".encode("utf-8"))

    shapes = []
    ink: list[tuple[float, float]] = [(b.cx, b.cy) for _, _, _, b in ((0, 0, 0, to_box(w[:4])) for w in page.get_text("words")) if b.cx <= strip]
    for d in page.get_drawings():
        b = to_box(d["rect"])
        if b.cx > strip:
            continue
        ink.append((b.cx, b.cy))
        fill = d.get("fill")
        shapes.append((round(b.x0), round(b.y0), round(b.x1), round(b.y1), len(d["items"]), tuple(round(c, 2) for c in fill) if fill else ()))
    shapes.sort()
    for s in shapes:
        digest.update(f"D|{s}\n".encode("utf-8"))

    step = max(1, len(ink) // MAX_INK_POINTS)
    return SheetFacts(page_no, width, height, page.rotation, spans, bool(words), digest.hexdigest(), ink[::step])
