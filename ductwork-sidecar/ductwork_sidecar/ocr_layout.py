"""Reads a text-less sheet (outlined text, no text layer) by local OCR (detector family E).

Some CAD exports outline their text, so the PDF has drawings that look like text and no text at all.
This renders the page in tiles at high resolution, runs a local OCR engine on each tile, and returns a
``PageLayout`` in the same canonical space as ``pdf_layout``, so the same detectors run on it unchanged.
Nothing leaves the server. Hosted vision is a separate, explicitly approved fallback (plan section 6.12)
and is not used here.

The OCR engine is injected, so the tiling and coordinate arithmetic are tested without it installed.
Everything this produces is a proposal for a person to verify; OCR misreads characters, which is why
its detections carry lower confidence than text read from a live text layer.

Supports unrotated pages only for now (``/Rotate`` 0). A rotated text-less sheet raises
``NotImplementedError`` rather than returning coordinates that would be wrong.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from typing import Protocol, Sequence

from .pdf_layout import (
    Box,
    PageLayout,
    TextLine,
    TextWord,
    _find_border,
    _is_triangle,
    point_in_triangle,
    triangle_corners,
)

# One OCR result: a quadrilateral in tile pixels, the text, and a score in [0, 1].
OcrResult = tuple[Sequence[Sequence[float]], str, float]


class OcrEngine(Protocol):
    def __call__(self, image) -> Sequence[OcrResult] | None: ...


class Recognizer(Protocol):
    """Reads one pre-cropped image of a short string and returns (text, score), or None."""

    def __call__(self, image) -> tuple[str, float] | None: ...


@dataclass(frozen=True)
class OcrSettings:
    scale: float = 8.0          # pixels per PDF point; 8 reads 3 pt text from a letter-size print
    tile_pt: float = 110.0      # tile side in points
    overlap_pt: float = 14.0    # overlap so a label cut by a tile edge is whole in the next tile
    min_score: float = 0.5
    dedupe_pt: float = 4.0


def rapidocr_engine() -> OcrEngine:
    """The default local engine. Imported lazily; install with the ``ocr`` extra."""
    from rapidocr_onnxruntime import RapidOCR

    engine = RapidOCR()

    def run(image):
        result, _elapsed = engine(image)
        return result or []

    return run


def rapidocr_recognizer() -> Recognizer:
    """Recognition only (no detection), for images already cropped to one short string."""
    from rapidocr_onnxruntime import RapidOCR

    engine = RapidOCR()

    def run(image):
        result, _elapsed = engine(image, use_det=False, use_cls=False, use_rec=True)
        return (str(result[0][0]), float(result[0][1])) if result else None

    return run


def load_ocr_layout(
    path: str,
    page_index: int = 0,
    engine: OcrEngine | None = None,
    settings: OcrSettings = OcrSettings(),
    cache_path: str | None = None,
    recognizer: Recognizer | None = None,
) -> PageLayout:
    import pymupdf

    with pymupdf.open(path) as doc:
        page = doc[page_index]
        if page.rotation != 0:
            raise NotImplementedError("OCR of a rotated text-less sheet is not supported yet.")

        drawings = page.get_drawings()
        scale = max(page.rect.width, page.rect.height)
        boxes_by_text = _read_cache(cache_path)
        if boxes_by_text is None:
            boxes_by_text = ocr_page(page, engine or rapidocr_engine(), settings, pymupdf)
            _write_cache(cache_path, boxes_by_text)

        lines = [TextLine(t, b) for t, b in boxes_by_text]
        words = _split_words(boxes_by_text)
        triangles = [_box(d["rect"]) for d in drawings if _is_triangle(d, scale)]
        border = _find_border([_box(d["rect"]) for d in drawings], page.rect.width * page.rect.height)

        if recognizer is not None:
            words = merge_symbol_digits(words, read_flag_digits(drawings, scale, recognizer, pymupdf, settings.min_score))

        lines.sort(key=lambda l: (round(l.box.y0), l.box.x0))
        return PageLayout(page.rect.width, page.rect.height, 0, lines, words, triangles, border, text_source="ocr")


_GLYPH_SCALE = 40.0
_DIGITS = re.compile(r"\d{1,2}")


def read_flag_digits(drawings: list[dict], sheet_scale: float, recognizer: Recognizer, pymupdf,
                     min_score: float = 0.5) -> list[tuple[Box, str]]:
    """Reads the number inside each flag triangle from the vector glyph paths, not from the raster.

    On a text-less sheet the digit is outlined paths sitting inside the triangle. Reading the page
    image makes the triangle's own edges dominate the crop. Instead only the paths inside the triangle
    are re-drawn alone on a blank page, which leaves a clean digit for the recognizer. Returns
    (triangle box, digits) for each triangle whose digits were read with enough confidence.
    """
    found: list[tuple[Box, str]] = []
    for tri in (d for d in drawings if _is_triangle(d, sheet_scale)):
        tri_rect = pymupdf.Rect(tri["rect"])
        corners = triangle_corners(tri)
        if len(corners) < 3:
            continue
        glyphs = [
            g for g in drawings
            if g is not tri
            and tri_rect.contains(pymupdf.Rect(g["rect"]) + (-0.1, -0.1, 0.1, 0.1))
            and pymupdf.Rect(g["rect"]).width < tri_rect.width * 0.8
            and point_in_triangle((g["rect"].x0 + g["rect"].x1) / 2, (g["rect"].y0 + g["rect"].y1) / 2, corners)
        ]
        if not glyphs:
            continue
        image = _render_paths(glyphs, pymupdf)
        read = recognizer(image)
        if read is None:
            continue
        text = re.sub(r"\s+", "", read[0])
        if _DIGITS.fullmatch(text) and read[1] >= min_score:
            found.append((_box(tri_rect), text))
    return found


def merge_symbol_digits(words: list[TextWord], symbol_digits: list[tuple[Box, str]]) -> list[TextWord]:
    """Symbol-read digits replace any OCR digit tokens inside the same triangle, so a flag is counted once."""
    result = list(words)
    for tri, text in symbol_digits:
        result = [w for w in result if not (_DIGITS.fullmatch(w.text) and tri.contains_point(w.box.cx, w.box.cy, margin=3))]
        result.append(TextWord(text, Box(tri.cx - 1, tri.cy - 1, tri.cx + 1, tri.cy + 1)))
    return result


def _render_paths(glyphs: list[dict], pymupdf):
    """Draws only the given vector paths, in black on white, and returns an RGB image array."""
    import numpy as np

    rect = pymupdf.Rect(glyphs[0]["rect"])
    for g in glyphs[1:]:
        rect |= pymupdf.Rect(g["rect"])
    rect += (-0.4, -0.4, 0.4, 0.4)
    doc = pymupdf.open()
    try:
        page = doc.new_page(width=rect.width, height=rect.height)
        shape = page.new_shape()
        off = pymupdf.Point(-rect.x0, -rect.y0)
        for g in glyphs:
            for item in g["items"]:
                kind = item[0]
                if kind == "l":
                    shape.draw_line(item[1] + off, item[2] + off)
                elif kind == "c":
                    shape.draw_bezier(item[1] + off, item[2] + off, item[3] + off, item[4] + off)
                elif kind == "re":
                    shape.draw_rect(pymupdf.Rect(item[1]) + (off.x, off.y, off.x, off.y))
                elif kind == "qu":
                    q = item[1]
                    shape.draw_quad(pymupdf.Quad(q.ul + off, q.ur + off, q.ll + off, q.lr + off))
            shape.finish(fill=(0, 0, 0), color=(0, 0, 0), width=max(g.get("width") or 0, 0.05), closePath=False)
        shape.commit()
        pix = page.get_pixmap(matrix=pymupdf.Matrix(_GLYPH_SCALE, _GLYPH_SCALE), colorspace=pymupdf.csRGB, alpha=False)
        image = np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width, 3)
        return np.pad(image, ((40, 40), (40, 40), (0, 0)), constant_values=255)
    finally:
        doc.close()


def ocr_page(page, engine: OcrEngine, settings: OcrSettings, pymupdf) -> list[tuple[str, Box]]:
    """Runs the engine over overlapping tiles and returns de-duplicated (text, box in points)."""
    import numpy as np

    found: list[tuple[str, Box, float]] = []
    for clip in tile_rects(page.rect.width, page.rect.height, settings.tile_pt, settings.overlap_pt):
        pix = page.get_pixmap(matrix=pymupdf.Matrix(settings.scale, settings.scale),
                              clip=pymupdf.Rect(*clip), colorspace=pymupdf.csRGB, alpha=False)
        image = np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width, 3)
        if image.std() < 3:  # blank tile
            continue
        for quad, text, score in engine(image) or []:
            if float(score) < settings.min_score or not str(text).strip():
                continue
            found.append((str(text).strip(), tile_box_to_page(quad, clip, settings.scale), float(score)))
    return dedupe(found, settings.dedupe_pt)


def tile_rects(width: float, height: float, tile: float, overlap: float) -> list[tuple[float, float, float, float]]:
    """Overlapping tiles that cover the page. Every point of the page is inside at least one tile."""
    step = tile - overlap
    if step <= 0:
        raise ValueError("The overlap must be smaller than the tile.")
    tiles = []
    y = 0.0
    while y < height:
        x = 0.0
        while x < width:
            tiles.append((x, y, min(x + tile, width), min(y + tile, height)))
            x += step
        y += step
    return tiles


def tile_box_to_page(quad: Sequence[Sequence[float]], clip: tuple[float, float, float, float], scale: float) -> Box:
    """Maps an OCR quadrilateral in tile pixels to a box in page points."""
    xs = [p[0] for p in quad]
    ys = [p[1] for p in quad]
    return Box(clip[0] + min(xs) / scale, clip[1] + min(ys) / scale, clip[0] + max(xs) / scale, clip[1] + max(ys) / scale)


def dedupe(found: list[tuple[str, Box, float]], tolerance: float) -> list[tuple[str, Box]]:
    """Drops the copy of a label that overlapping tiles both read, keeping the higher score."""
    kept: list[tuple[str, Box, float]] = []
    for text, box, score in sorted(found, key=lambda f: -f[2]):
        if any(text == k[0] and abs(box.cx - k[1].cx) <= tolerance and abs(box.cy - k[1].cy) <= tolerance for k in kept):
            continue
        kept.append((text, box, score))
    return [(t, b) for t, b, _s in kept]


def _split_words(lines: list[tuple[str, Box]]) -> list[TextWord]:
    """Words with boxes apportioned by character count, so a digit inside a triangle can be located."""
    words: list[TextWord] = []
    for text, box in lines:
        parts = text.split()
        total = sum(len(p) for p in parts) + max(0, len(parts) - 1)
        x = box.x0
        for part in parts:
            width = box.width * len(part) / max(total, 1)
            words.append(TextWord(part, Box(x, box.y0, x + width, box.y1)))
            x += box.width * (len(part) + 1) / max(total, 1)
    return words


def _box(rect) -> Box:
    return Box(rect.x0, rect.y0, rect.x1, rect.y1)


def _read_cache(path: str | None):
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return [(t, Box(*b)) for t, b in json.load(f)]
    return None


def _write_cache(path: str | None, boxes: list[tuple[str, Box]]) -> None:
    if path:
        with open(path, "w", encoding="utf-8") as f:
            json.dump([[t, [b.x0, b.y0, b.x1, b.y1]] for t, b in boxes], f)
