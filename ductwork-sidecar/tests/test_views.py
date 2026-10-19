"""Finding the views on a sheet, on synthetic drawings. No customer drawing is used or stored here."""

from __future__ import annotations

import json

import pymupdf
import pytest

from ductwork_sidecar.__main__ import main
from ductwork_sidecar.pdf_layout import load_sheet_facts
from ductwork_sidecar.views import default_view_type, find_views, parse_scale, to_report
from tests.sheets import H, W, _Canvas


def sheet(path, views, title_under_view=True, rotation=0, extra_text=()):
    """A sheet with a title strip on the right and the given views. Each view is (x, y, title, scale text, number): a block of
    line-work with its title and scale label either just under it or just over it, and a number in a bubble beside the title."""
    doc = pymupdf.open()
    stored_w, stored_h = (H, W) if rotation in (90, 270) else (W, H)
    page = doc.new_page(width=stored_w, height=stored_h)
    page.set_rotation(rotation)
    c = _Canvas(page, rotation)
    c.rect(30, 30, W - 30, H - 30)
    c.rect(W - 300, 30, W - 30, H - 30)
    c.text(W - 280, H - 160, "M1.6", 58)
    for x, y, title, scale, number in views:
        # y is the top of the title line. The block of line-work is 300 high, on the side the title is not.
        top, bottom = (y - 320, y - 20) if title_under_view else (y + 70, y + 370)
        for i in range(0, 700, 28):
            for j in range(int(top), int(bottom), 28):
                c.rect(x + i, j, x + i + 12, j + 12)
        c.text(x, y, title, 20)
        if number:
            c.text(x - 32, y + 4, number, 18)
        if scale:
            c.text(x, y + 26, scale, 10)
    for text, x, y in extra_text:
        c.text(x, y, text, 10)
    doc.save(path)
    doc.close()
    return str(path)


def views_of(path, default="Plan"):
    return find_views(load_sheet_facts(str(path))[0], default)


# ---------------------------------------------------------------- reading a scale


@pytest.mark.parametrize("text,ratio", [
    ('1/4" = 1\'-0"', 48.0), ('1/8" = 1\'-0"', 96.0), ('3/32" = 1\'-0"', 128.0), ('1" = 20\'-0"', 240.0), ('SCALE: 1/4" = 1\'-0"', 48.0),
    ('1 1/2" = 1\'-0"'.replace("1 1/2", "1.5"), 8.0), ('1/4" = 1\'-6"', 72.0)])
def test_a_printed_scale_becomes_real_length_per_drawn_length(text, ratio):
    assert parse_scale(text) == ratio


@pytest.mark.parametrize("text", ["NTS", "SCALE SHOWN TO ENSURE REPRODUCTION ACCURACY", "MECHANICAL PLAN", "4x10x7", ""])
def test_text_that_is_not_a_scale_is_not_read_as_one(text):
    assert parse_scale(text) is None


# ---------------------------------------------------------------- one view or several


@pytest.mark.parametrize("rotation", [0, 90, 270])
def test_a_sheet_with_one_scale_label_is_one_view_over_the_whole_drawing_area(tmp_path, rotation):
    path = sheet(tmp_path / "a.pdf", [(300, 1500, "MECHANICAL PLAN", 'SCALE: 1/4" = 1\'-0"', "1")], rotation=rotation)

    (view,) = views_of(path)

    assert (view.title, view.number, view.view_type, view.ratio, view.region, view.title_found) == ("MECHANICAL PLAN", "1", "Plan", 48.0, None, True)


def test_a_sheet_with_no_scale_label_has_no_views(tmp_path):
    assert views_of(sheet(tmp_path / "a.pdf", [(300, 1500, "MECHANICAL PLAN", None, None)], extra_text=[("SCALE SHOWN TO ENSURE REPRODUCTION ACCURACY", 300, 100)])) == []


def test_a_scale_label_inside_the_title_block_is_not_a_view(tmp_path):
    path = sheet(tmp_path / "a.pdf", [(W - 280, 600, "SOME TITLE BLOCK ENTRY", '1/8" = 1\'-0"', None)])

    assert views_of(path) == []


def test_two_views_side_by_side_are_divided_at_the_empty_stretch_between_them(tmp_path):
    path = sheet(tmp_path / "a.pdf", [(200, 1500, "LEVEL 1 PLAN", '1/8" = 1\'-0"', None), (1200, 1500, "LEVEL 2 PLAN", '1/4" = 1\'-0"', None)])

    left, right = views_of(path)

    assert (left.title, left.ratio, right.title, right.ratio) == ("LEVEL 1 PLAN", 96.0, "LEVEL 2 PLAN", 48.0)
    assert left.region[0] == 0 and right.region[2] == pytest.approx(W * 0.88, abs=1)
    assert left.region[2] == pytest.approx(right.region[0], abs=1)
    assert 200 + 700 <= left.region[2] <= 1200                                    # the cut falls in the empty stretch between the blocks
    assert left.region[3] == pytest.approx(1500, abs=4)                           # the view ends where its title begins


@pytest.mark.parametrize("title_under_view", [True, False])
def test_four_views_in_a_grid_each_get_their_own_area_whichever_side_the_title_is_on(tmp_path, title_under_view):
    ys = (700, 1500) if title_under_view else (200, 1000)
    path = sheet(tmp_path / "a.pdf", [(200, ys[0], "LEVEL A", '1/8" = 1\'-0"', None), (1200, ys[0], "LEVEL B", '1/8" = 1\'-0"', None),
                                      (200, ys[1], "LEVEL C", '1/8" = 1\'-0"', None), (1200, ys[1], "LEVEL D", '1/8" = 1\'-0"', None)], title_under_view=title_under_view)

    views = {v.title: v for v in views_of(path)}

    assert set(views) == {"LEVEL A", "LEVEL B", "LEVEL C", "LEVEL D"}
    marks = {"LEVEL A": (400, ys[0] - 200 if title_under_view else ys[0] + 200), "LEVEL B": (1400, ys[0] - 200 if title_under_view else ys[0] + 200),
             "LEVEL C": (400, ys[1] - 200 if title_under_view else ys[1] + 200), "LEVEL D": (1400, ys[1] - 200 if title_under_view else ys[1] + 200)}
    for name, (x, y) in marks.items():
        for other, view in views.items():
            x0, y0, x1, y1 = view.region
            inside = x0 <= x <= x1 and y0 <= y <= y1
            assert inside == (name == other), f"{name}'s drawing is {'in' if inside else 'not in'} view {other}"


def test_a_scale_printed_as_its_own_word_and_value_is_one_label_starting_at_the_word(tmp_path):
    path = sheet(tmp_path / "a.pdf", [(300, 1500, "MECHANICAL PLAN", None, None)], extra_text=[("SCALE:", 300, 1526), ('1/8" = 1\'-0"', 340, 1526)])

    (view,) = views_of(path)

    assert (view.ratio, view.title) == (96.0, "MECHANICAL PLAN")


# ---------------------------------------------------------------- details


def test_details_with_no_scale_are_views_with_no_ratio_and_are_numbered(tmp_path):
    path = sheet(tmp_path / "a.pdf", [(200, 700, "ROOF DUCT SUPPORT", "NTS", "1"), (1200, 700, "VOLUME DAMPER", "NO SCALE", "2")])

    one, two = views_of(path, default="Detail")

    assert [(v.number, v.title, v.view_type, v.ratio, v.scale_text) for v in (one, two)] == [("1", "ROOF DUCT SUPPORT", "Detail", None, "NTS"), ("2", "VOLUME DAMPER", "Detail", None, "NO SCALE")]


@pytest.mark.parametrize("title,kind", [("BUILDING SECTION", "Section"), ("NORTH ELEVATION", "Elevation"), ("FAN DETAIL", "Detail"), ("RISER DIAGRAM", "Riser"), ("FLOOR PLAN", "Plan")])
def test_a_views_kind_comes_from_its_own_title(tmp_path, title, kind):
    (view,) = views_of(sheet(tmp_path / "a.pdf", [(300, 1500, title, '1/8" = 1\'-0"', None)]), default="Other")

    assert view.view_type == kind


def test_a_view_with_no_title_above_its_label_is_named_by_its_position(tmp_path):
    path = sheet(tmp_path / "a.pdf", [(300, 1500, "", '1/8" = 1\'-0"', None)])

    (view,) = views_of(path)

    assert (view.title, view.title_found) == ("View 1", False)


def test_a_sheets_kind_decides_what_an_unnamed_view_is():
    assert [default_view_type(k) for k in ("Plan", "Detail", "Schedule", "Riser", "Legend", "Other")] == ["Plan", "Detail", "Schedule", "Riser", "Other", "Other"]


# ---------------------------------------------------------------- the command line


def test_the_sheets_command_reports_each_pages_views_in_the_shape_the_server_takes(tmp_path):
    pdf = sheet(tmp_path / "a.pdf", [(200, 1500, "LEVEL 1 PLAN", '1/8" = 1\'-0"', "1"), (1200, 1500, "LEVEL 2 PLAN", '1/8" = 1\'-0"', "2")])
    out = tmp_path / "pages.json"

    assert main(["sheets", pdf, "--out", str(out)]) == 0

    views = json.loads(out.read_text())["pages"][0]["views"]
    assert [(v["number"], v["title"], v["type"], v["scaleRatio"]) for v in views] == [("1", "LEVEL 1 PLAN", "Plan", 96.0), ("2", "LEVEL 2 PLAN", "Plan", 96.0)]
    assert all(len(v["polygon"]) == 4 and len(v["polygon"][0]) == 2 for v in views)


def test_a_whole_page_view_has_no_polygon():
    from ductwork_sidecar.views import DetectedView

    assert to_report([DetectedView("1", "PLAN", "Plan", "1/4\" = 1'-0\"", 48.0, None, True)])[0]["polygon"] is None


def test_nts_in_a_table_of_abbreviations_is_not_a_scale_label(tmp_path):
    path = sheet(tmp_path / "a.pdf", [], extra_text=[("NTS", 300, 500), ("NOT TO SCALE", 340, 500), ("NIC", 300, 520), ("NOT IN CONTRACT", 340, 520)])

    assert views_of(path) == []


def test_a_stand_alone_nts_is_a_scale_label(tmp_path):
    path = sheet(tmp_path / "a.pdf", [(300, 1500, "FAN DETAIL", "NTS", "1")])

    assert [(v.title, v.scale_text) for v in views_of(path, "Detail")] == [("FAN DETAIL", "NTS")]


def test_the_sheets_command_records_no_views_for_a_page_that_is_not_mechanical(tmp_path):
    pdf = tmp_path / "e.pdf"
    doc = pymupdf.open(sheet(tmp_path / "base.pdf", [(300, 1500, "POWER PLAN", '1/4" = 1\'-0"', "1")]))
    doc.close()
    # Re-draw the same sheet with an electrical number.
    d = pymupdf.open()
    page = d.new_page(width=W, height=H)
    c = _Canvas(page, 0)
    c.text(W - 280, H - 160, "E1.1", 58)
    c.text(300, 1500, "POWER PLAN", 20)
    c.text(300, 1526, '1/4" = 1\'-0"', 10)
    d.save(pdf)
    d.close()
    out = tmp_path / "pages.json"

    main(["sheets", str(pdf), "--out", str(out)])

    page = json.loads(out.read_text())["pages"][0]
    assert (page["discipline"], page["views"]) == ("Electrical", [])
