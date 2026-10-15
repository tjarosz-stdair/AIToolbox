"""Builds synthetic residential unit-plan sheets for tests. No customer drawing is used.

The sheet mimics what the sample CAD exports look like: a border, a plan area with terminal labels
(size over CFM), fan and grille tags, numbered triangle flags, room names that contain digits, and a
"FLAG NOTES" column on the right. ``rotation`` reproduces the CAD habit of storing the sheet sideways
and setting /Rotate so it displays upright: everything is drawn in the page's *stored* coordinates,
transformed from the upright layout, with text rotated to match.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import pymupdf

W, H = 2592.0, 1728.0

DEFAULT_NOTES = {
    1: "CEILING HEIGHTS ARE 8'-0\" A.F.F. (U.N.O.)",
    2: "14x9 PASS THROUGH LOUVERED VENT, STAGGERED, ABOVE BEDROOM DOOR.",
    3: "ROUTE 4\" DIA. EXHAUST DUCT FROM CEILING MOUNTED EXHAUST FAN TOWARDS REAR OF UNIT TO ROOF JACK.",
    4: "PROGRAMMABLE THERMOSTAT. THERMOSTAT TO BE MOUNTED AT 44\" A.F.F.",
    5: "7\" DIA. FLEX PIPE (U.N.O.).",
    6: "16x8x30 DISTRIBUTION DUCT IN ATTIC. WRAP WITH R-8 FOIL-WRAPPED INSULATION.",
    7: "WASHER/DRYER OPERABLE PARTS SHALL NOT BE ABOVE 46\" IN HEIGHT.",
    8: "RECESSED DRYER VENT BOX.",
    9: "2\" COMBUSTION AIR AND 2\" FLUE VENT TO CONCENTRIC VENT UP THROUGH ROOF",
    10: "ROUTE 6\" DIA. EXHAUST DUCT FROM CEILING MOUNTED EXHAUST FAN UP THROUGH ROOF TO ROOF JACK.",
}


@dataclass
class Sheet:
    title: str = "MECH. PLANS - UNIT A"
    # (x, y, size text, cfm text or None)
    boots: list[tuple[float, float, str, str | None]] = field(default_factory=list)
    # (x, y, text)
    tags: list[tuple[float, float, str]] = field(default_factory=list)
    # (x, y, flag number)
    flags: list[tuple[float, float, int]] = field(default_factory=list)
    # (x, y, text), plain text with no symbol
    plain: list[tuple[float, float, str]] = field(default_factory=list)
    notes: dict[int, str] = field(default_factory=lambda: dict(DEFAULT_NOTES))
    with_text: bool = True


class _Canvas:
    """Draws in upright (displayed) coordinates onto a page that may be stored rotated."""

    def __init__(self, page: pymupdf.Page, rotation: int):
        self.page = page
        self.rotation = rotation
        self.inverse = ~page.rotation_matrix  # displayed -> stored

    def point(self, x: float, y: float) -> pymupdf.Point:
        return pymupdf.Point(x, y) * self.inverse

    def rect(self, x0, y0, x1, y1, fill=None):
        r = pymupdf.Rect(self.point(x0, y0), self.point(x1, y1))
        r.normalize()
        self.page.draw_rect(r, fill=fill)

    def polyline(self, pts):
        shape = self.page.new_shape()
        shape.draw_polyline([self.point(x, y) for x, y in pts])
        shape.finish(closePath=False, width=1)
        shape.commit()

    def text(self, x, y, text, size=9.0):
        # (x, y) is the upright top-left of the text. The baseline origin is one size below it.
        origin = self.point(x, y + size)
        # Text is turned by the page rotation so that it reads upright once displayed.
        self.page.insert_text(origin, text, fontsize=size, fontname="helv", rotate=self.rotation % 360)

    def label(self, x, y, text, with_text: bool):
        if with_text:
            self.text(x, y, text, 9)
        else:
            # Outlined text: filled shapes, no text objects.
            for i, ch in enumerate(text):
                if ch != " ":
                    self.rect(x + i * 5, y + 1, x + i * 5 + 3.5, y + 8, fill=(0, 0, 0))


def build(sheet: Sheet, path: str, rotation: int = 0) -> str:
    doc = pymupdf.open()
    stored_w, stored_h = (H, W) if rotation in (90, 270) else (W, H)
    page = doc.new_page(width=stored_w, height=stored_h)
    page.set_rotation(rotation)
    _draw(_Canvas(page, rotation), sheet)
    doc.save(path)
    doc.close()
    return path


def _draw(c: _Canvas, sheet: Sheet):
    c.rect(30, 30, W - 30, H - 30)
    c.rect(W - 260, H - 200, W - 30, H - 30)  # title block
    if sheet.with_text:
        c.text(W - 250, H - 190, sheet.title, 12)

    for x, y, size, cfm in sheet.boots:
        c.label(x, y, size, sheet.with_text)
        if cfm:
            c.label(x - 2, y + 13, cfm, sheet.with_text)
    for x, y, text in sheet.tags:
        c.label(x, y, text, sheet.with_text)
    for x, y, text in sheet.plain:
        c.label(x, y, text, sheet.with_text)
    for x, y, number in sheet.flags:
        c.polyline([(x, y - 24), (x - 24, y + 18), (x + 24, y + 18), (x, y - 24)])
        c.label(x - 5 * len(str(number)), y - 2, str(number), sheet.with_text)

    if sheet.with_text and sheet.notes:
        c.text(1960, 465, "FLAG NOTES - MECHANICAL", 14)
        y = 495
        for number, text in sorted(sheet.notes.items()):
            c.text(1898, y, f"{number}.", 9)
            chunks = _wrap(text, 70)
            for i, chunk in enumerate(chunks):
                c.text(1920, y + i * 12, chunk, 9)
            y += 24 + 12 * (len(chunks) - 1)


def _wrap(text: str, width: int) -> list[str]:
    words, lines, cur = text.split(), [], ""
    for w in words:
        if len(cur) + len(w) + 1 > width and cur:
            lines.append(cur)
            cur = w
        else:
            cur = f"{cur} {w}".strip()
    if cur:
        lines.append(cur)
    return lines
