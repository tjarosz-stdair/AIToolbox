"""The only module that touches the PDF library.

Everything it returns is in canonical space: the page as displayed (after /Rotate), origin at the
top-left, units of 1/72 inch. PyMuPDF reports text and drawing coordinates in *unrotated* page space,
so every coordinate is mapped through the page's rotation matrix here and nowhere else. Forgetting
that on a rotated sheet silently puts every label in the wrong place, which is why it is one function.
"""

from __future__ import annotations

from dataclasses import dataclass
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
    triangles = [to_box(d["rect"]) for d in drawings if _is_triangle(d)]
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


def _is_triangle(drawing: dict) -> bool:
    """A closed or nearly closed path of three straight segments with exactly three distinct corners."""
    items = drawing["items"]
    if not 3 <= len(items) <= 4 or any(item[0] != "l" for item in items):
        return False
    corners = set()
    for item in items:
        corners.add((round(item[1].x), round(item[1].y)))
        corners.add((round(item[2].x), round(item[2].y)))
    rect = drawing["rect"]
    return len(corners) == 3 and 15 < rect.width < 160 and 15 < rect.height < 160


def _find_border(boxes: Iterable[Box], page_area: float) -> Box | None:
    """The sheet border: the largest drawing that still fits inside the page."""
    best: Box | None = None
    for box in boxes:
        if box.area < page_area * 0.5 or box.area > page_area * 0.995:
            continue
        if best is None or box.area > best.area:
            best = box
    return best
