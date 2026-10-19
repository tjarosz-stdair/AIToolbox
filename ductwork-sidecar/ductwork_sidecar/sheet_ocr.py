"""Identifies a text-less sheet by reading its title block as an image (detector family E).

Some CAD exports outline their text, so the page has no text layer and ``sheet_id`` has nothing to read.
Only the title-block strip is rendered and read, not the whole sheet, so this costs seconds, not minutes.
The strip is read twice: upright, which finds the sheet number (set large and horizontal), and turned a
quarter turn, which finds a title that is written along the edge of the sheet, as many title blocks are.
The narrow strip is tried first because a wider one takes in the notes column, whose text outshouts the title.

The OCR engine is injected, so the arithmetic is tested without it installed. Everything returned is a
proposal; OCR misreads characters and drops spaces, so the result says it came from OCR and a person can
correct it.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Sequence

from .sheet_id import SHEET_NO, SheetIdentity, _NOT_A_SHEET, _NOISE, _DATE, _PHONE, _TITLE_WORDS, _discipline, _kind

OcrResult = tuple[Sequence[Sequence[float]], str, float]

# (text, height in pixels, score) for one line the engine found.
Line = tuple[str, float, float]


@dataclass(frozen=True)
class StripSettings:
    narrow_from: float = 0.90      # the title block proper
    wide_from: float = 0.74        # takes in a notes column beside it, tried only if the narrow strip gave nothing
    scale: float = 6.0             # pixels per point
    min_score: float = 0.5


# Words that make up sheet titles. OCR of tightly set capitals often drops the spaces between them.
_SPLIT_WORDS = ("MECHANICAL", "ELECTRICAL", "PLUMBING", "SCHEDULES", "SCHEDULE", "DETAILS", "DETAIL", "GENERAL", "LEGENDS", "LEGEND", "NOTES", "RISER",
                "SECTIONS", "SECTION", "DIAGRAM", "SYMBOLS", "ABBREVIATIONS", "SPECIFICATIONS", "SPECS", "FLOOR", "LEVEL", "UNIT", "ROOF", "SITE", "PLANS", "PLAN", "AND")


def _split_known(token: str) -> list[str] | None:
    """Splits a run-together token into known title words, longest first, or None if it is not made only of them."""
    if not token:
        return []
    for word in sorted(_SPLIT_WORDS, key=len, reverse=True):
        if token.startswith(word):
            rest = _split_known(token[len(word):])
            if rest is not None:
                return [word, *rest]
    return None


def respace(text: str) -> str:
    """Puts spaces back in a title the OCR ran together: around dashes, and inside tokens made only of known title words."""
    out: list[str] = []
    for piece in re.split(r"\s*[-\u2013]\s*", text.upper().strip()):
        words: list[str] = []
        for token in piece.split():
            parts = _split_known(token) if len(token) > 12 else None
            words.extend(parts if parts else [token])
        out.append(" ".join(words))
    return " - ".join(p for p in out if p)


def identify_text_less(path: str, page_index: int, engine, settings: StripSettings = StripSettings()) -> SheetIdentity:
    import numpy as np
    import pymupdf

    with pymupdf.open(path) as doc:
        page = doc[page_index]
        width, height = page.rect.width, page.rect.height

        def read_strip(from_x: float) -> tuple[list[Line], list[Line]]:
            clip = pymupdf.Rect(width * from_x, 0, width, height)
            pix = page.get_pixmap(matrix=pymupdf.Matrix(settings.scale, settings.scale), clip=clip, colorspace=pymupdf.csRGB, alpha=False)
            image = np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width, 3)
            # A quarter turn counter-clockwise puts text that runs up the edge of the sheet upright.
            return _read(engine, image, settings), _read(engine, np.ascontiguousarray(np.rot90(image, 1)), settings)

        upright, turned = read_strip(settings.narrow_from)
        result = interpret(upright, turned)
        if result.sheet_number is None or result.title is None:
            wide_upright, wide_turned = read_strip(settings.wide_from)
            wide = interpret(wide_upright, wide_turned)
            result = SheetIdentity(result.sheet_number or wide.sheet_number, result.title or wide.title,
                                   _discipline(result.sheet_number or wide.sheet_number, result.title or wide.title),
                                   _kind(result.title or wide.title), "ocr" if (result.sheet_number or wide.sheet_number) else "none", result.notes)
        return result


def _read(engine, image, settings: StripSettings) -> list[Line]:
    found: list[Line] = []
    for quad, text, score in engine(image) or []:
        text = " ".join(str(text).split())
        if text and float(score) >= settings.min_score:
            ys = [p[1] for p in quad]
            found.append((text, max(ys) - min(ys), float(score)))
    return found


def _title_from(lines: list[Line]) -> str | None:
    usable = [
        (t, h) for t, h, _ in lines
        if len(t) >= 4 and not _NOISE.match(t) and not _DATE.search(t) and not _PHONE.search(t)
        and sum(c.isdigit() for c in t) <= 0.3 * len(t) and not re.search(r"\.com|www\.", t, re.I)
        # A title is a short phrase. A sentence ending in a full stop is a note.
        and len(t) <= 48 and not t.rstrip().endswith(".")
    ]
    titled = [(t, h) for t, h in usable if any(w in t.upper() for w in _TITLE_WORDS)]
    # Among lines that read like a title, the longest is the title: the others are fragments of notes or a project name.
    return respace(max(titled, key=lambda x: (len(x[0]), x[1]))[0]) if titled else None


def interpret(upright: list[Line], turned: list[Line]) -> SheetIdentity:
    """The sheet number from the upright reading, the title from the turned one (or else the upright one)."""
    numbers = [(t.upper(), h) for t, h, _ in upright if SHEET_NO.match(t.upper()) and not _NOT_A_SHEET.match(t.upper())]
    number = max(numbers, key=lambda n: n[1])[0] if numbers else None
    title = _title_from(turned) or _title_from(upright)
    return SheetIdentity(number, title, _discipline(number, title), _kind(title), "ocr" if number else "none", ("Read from the title block by OCR; check it.",))
