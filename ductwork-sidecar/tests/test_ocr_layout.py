import pymupdf
import pytest

from ductwork_sidecar.ocr_layout import (
    OcrSettings,
    dedupe,
    load_ocr_layout,
    tile_box_to_page,
    tile_rects,
)
from ductwork_sidecar.pdf_layout import Box
from ductwork_sidecar.residential import detect

from sheets import Sheet, build


def test_tiles_cover_the_whole_page_with_the_requested_overlap():
    tiles = tile_rects(792, 612, tile=110, overlap=14)

    # Every sampled point of the page is inside at least one tile.
    for x in range(0, 792, 13):
        for y in range(0, 612, 13):
            assert any(t[0] <= x <= t[2] and t[1] <= y <= t[3] for t in tiles), (x, y)
    # Tiles never leave the page.
    assert all(0 <= t[0] < t[2] <= 792 and 0 <= t[1] < t[3] <= 612 for t in tiles)
    # Neighbouring tiles overlap by the overlap.
    first, second = tiles[0], tiles[1]
    assert first[2] - second[0] == pytest.approx(14)


def test_an_overlap_as_large_as_the_tile_is_refused():
    with pytest.raises(ValueError):
        tile_rects(100, 100, tile=50, overlap=50)


def test_a_box_in_tile_pixels_maps_to_page_points():
    quad = [(80, 16), (160, 16), (160, 48), (80, 48)]
    box = tile_box_to_page(quad, clip=(200.0, 300.0, 310.0, 410.0), scale=8.0)
    assert (box.x0, box.y0, box.x1, box.y1) == (210.0, 302.0, 220.0, 306.0)


def test_the_same_label_read_in_two_overlapping_tiles_is_kept_once_with_the_better_score():
    found = [
        ("4x10x7", Box(100, 100, 130, 110), 0.80),
        ("4x10x7", Box(101, 100.5, 131, 110.5), 0.95),   # same label, seen again
        ("4x10x7", Box(300, 100, 330, 110), 0.90),       # a different label with the same text
        ("EF-1", Box(100, 100, 130, 110), 0.90),         # different text at the same place
    ]
    kept = dedupe(found, tolerance=4)
    assert len(kept) == 3
    same_place = [b for t, b in kept if t == "4x10x7" and b.x0 < 200]
    assert len(same_place) == 1 and same_place[0].x0 == 101


def _outlined_sheet(tmp_path, rotation=0):
    return build(Sheet(boots=[(400, 300, "4x10x7", "100 CFM")], flags=[(500, 1000, 2)], with_text=False),
                 str(tmp_path / f"textless{rotation}.pdf"), rotation)


def test_the_engine_sees_tiles_and_results_are_placed_by_tile_origin(tmp_path):
    pdf = _outlined_sheet(tmp_path)
    seen = []

    def engine(image):
        seen.append(image.shape)
        # One label at the top-left of every tile.
        return [([(0, 0), (80, 0), (80, 16), (0, 16)], "4x10x7", 0.9)]

    layout = load_ocr_layout(pdf, engine=engine, settings=OcrSettings(scale=4.0, tile_pt=400, overlap_pt=40))

    assert layout.rotation == 0 and not layout.lines == []
    origins = sorted({(round(l.box.x0), round(l.box.y0)) for l in layout.lines})
    assert (0, 0) in origins
    assert len(origins) > 1                                  # more than one tile contributed
    assert all(shape[2] == 3 for shape in seen)              # RGB tiles
    assert layout.has_text                                   # OCR gave the sheet a text layer


def test_blank_tiles_are_not_sent_to_the_engine(tmp_path):
    calls = []

    def engine(image):
        calls.append(1)
        return []

    pdf = _outlined_sheet(tmp_path)
    load_ocr_layout(pdf, engine=engine, settings=OcrSettings(scale=2.0, tile_pt=300, overlap_pt=20))
    from ductwork_sidecar.ocr_layout import tile_rects as tr
    assert 0 < len(calls) < len(tr(2592, 1728, 300, 20))


def test_low_scores_are_dropped(tmp_path):
    def engine(image):
        return [([(0, 0), (80, 0), (80, 16), (0, 16)], "4x10x7", 0.2)]

    layout = load_ocr_layout(_outlined_sheet(tmp_path), engine=engine, settings=OcrSettings(scale=2.0, tile_pt=500, overlap_pt=20))
    assert layout.lines == []


def test_results_are_cached_and_the_engine_is_not_called_again(tmp_path):
    pdf = _outlined_sheet(tmp_path)
    cache = str(tmp_path / "ocr.json")
    calls = []

    def engine(image):
        calls.append(1)
        return [([(0, 0), (80, 0), (80, 16), (0, 16)], "EF-1", 0.9)]

    settings = OcrSettings(scale=2.0, tile_pt=900, overlap_pt=20)
    first = load_ocr_layout(pdf, engine=engine, settings=settings, cache_path=cache)
    n = len(calls)
    second = load_ocr_layout(pdf, engine=engine, settings=settings, cache_path=cache)

    assert len(calls) == n
    assert [(l.text, l.box) for l in first.lines] == [(l.text, l.box) for l in second.lines]


def test_a_rotated_text_less_sheet_is_refused_rather_than_misplaced(tmp_path):
    pdf = _outlined_sheet(tmp_path, rotation=90)
    with pytest.raises(NotImplementedError):
        load_ocr_layout(pdf, engine=lambda image: [])


def test_triangles_are_found_on_a_small_print_of_the_same_sheet(tmp_path):
    """Flag triangles are sized relative to the sheet, so a letter-size print still yields them."""
    from ductwork_sidecar.pdf_layout import load_page_layout

    big = build(Sheet(flags=[(500, 1000, 2)]), str(tmp_path / "big.pdf"))
    small = tmp_path / "small.pdf"
    src = pymupdf.open(big)
    out = pymupdf.open()
    page = out.new_page(width=792, height=528)
    page.show_pdf_page(page.rect, src, 0)  # the same sheet scaled to about a third
    out.save(str(small))

    assert len(load_page_layout(big).triangles) == 1
    # Drawings inside a form XObject are not reported by the PDF library, so this checks the threshold
    # logic directly instead of the embedded copy.
    from ductwork_sidecar.pdf_layout import _is_triangle

    class R:  # a stand-in drawing: a 24 x 24 pt triangle drawn on a 792 pt wide sheet
        width = height = 24.0

    class P:
        def __init__(self, x, y): self.x, self.y = x, y

    drawing = {"items": [("l", P(0, 0), P(24, 0)), ("l", P(24, 0), P(12, 24)), ("l", P(12, 24), P(0, 0))], "rect": R()}
    assert _is_triangle(drawing, 792)
    assert not _is_triangle(drawing, 2592 * 3)   # too small to be a flag on a very large sheet


# ---- flag digits read from vector glyph paths

def test_a_flag_digit_is_read_from_the_paths_inside_the_triangle_alone(tmp_path):
    import pymupdf as pm
    from ductwork_sidecar.ocr_layout import read_flag_digits

    pdf = build(Sheet(flags=[(500, 1000, 2), (900, 1000, 11)], with_text=False), str(tmp_path / "flags.pdf"))
    seen = []

    def recognizer(image):
        seen.append(image)
        # Clean white background with dark glyph pixels only: the triangle outline must not be in the image.
        return ("7", 0.99)

    with pm.open(pdf) as doc:
        page = doc[0]
        found = read_flag_digits(page.get_drawings(), max(page.rect.width, page.rect.height), recognizer, pm)

    assert len(found) == 2 and all(text == "7" for _box, text in found)
    for image in seen:
        dark = (image.mean(axis=2) < 128)
        assert dark.any()                       # the glyph is there
        assert dark.mean() < 0.9                # and it is a glyph, not a filled triangle


def test_low_confidence_or_non_digit_reads_are_dropped(tmp_path):
    import pymupdf as pm
    from ductwork_sidecar.ocr_layout import read_flag_digits

    pdf = build(Sheet(flags=[(500, 1000, 2)], with_text=False), str(tmp_path / "f.pdf"))
    with pm.open(pdf) as doc:
        page = doc[0]
        drawings, scale = page.get_drawings(), max(page.rect.width, page.rect.height)
        assert read_flag_digits(drawings, scale, lambda im: ("7", 0.2), pm, min_score=0.5) == []
        assert read_flag_digits(drawings, scale, lambda im: ("A", 0.99), pm) == []
        assert read_flag_digits(drawings, scale, lambda im: ("123", 0.99), pm) == []
        assert read_flag_digits(drawings, scale, lambda im: None, pm) == []
        assert len(read_flag_digits(drawings, scale, lambda im: (" 1 1 ", 0.99), pm)) == 1   # spaces removed: "11"


def test_a_symbol_read_digit_replaces_the_ocr_tokens_in_the_same_triangle():
    from ductwork_sidecar.ocr_layout import merge_symbol_digits
    from ductwork_sidecar.pdf_layout import TextWord

    tri = Box(100, 100, 120, 120)
    words = [
        TextWord("1", Box(104, 108, 107, 112)),      # OCR read half of "11" inside the triangle
        TextWord("1", Box(108, 108, 111, 112)),
        TextWord("2", Box(500, 500, 503, 504)),      # a digit elsewhere stays
        TextWord("BEDROOM", Box(105, 108, 140, 112)),
    ]
    merged = merge_symbol_digits(words, [(tri, "11")])

    assert sorted(w.text for w in merged) == ["11", "2", "BEDROOM"]


def test_the_layout_from_ocr_says_where_its_text_came_from(tmp_path):
    layout = load_ocr_layout(_outlined_sheet(tmp_path), engine=lambda image: [], settings=OcrSettings(scale=2.0, tile_pt=900, overlap_pt=20))
    assert layout.text_source == "ocr"
