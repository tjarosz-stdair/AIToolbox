"""Sheet identification on synthetic drawings. No customer drawing is used or stored here."""

from __future__ import annotations

import json

import pymupdf
import pytest

from ductwork_sidecar.__main__ import main
from ductwork_sidecar.pdf_layout import load_sheet_facts
from ductwork_sidecar.sheet_id import SHEET_NO, _discipline, _kind, identify, to_page_report
from ductwork_sidecar.sheet_ocr import interpret, respace
from tests.sheets import H, W, _Canvas


def sheet(path, number="M1.6", title_lines=("MECHANICAL PLAN - BUILDING A",), rotation=0, project="REDWOOD FRANKLIN", date="MAY 1, 2025", project_size=12, plan_marks=(("4x10x7", 400, 400),)):
    """A sheet with a title strip down the right-hand edge: project, date, a title, and the number set large at the bottom."""
    doc = pymupdf.open()
    stored_w, stored_h = (H, W) if rotation in (90, 270) else (W, H)
    page = doc.new_page(width=stored_w, height=stored_h)
    page.set_rotation(rotation)
    c = _Canvas(page, rotation)
    c.rect(30, 30, W - 30, H - 30)
    c.rect(W - 300, 30, W - 30, H - 30)
    c.text(W - 280, 80, "REVISIONS", 14)
    c.text(W - 280, 700, project, project_size)
    c.text(W - 280, 740, "UPPER SHELBYVILLE RD", 15)
    c.text(W - 280, 780, "330.666.5770", 15)
    c.text(W - 280, 820, date, 15)
    y = 1000
    for line in title_lines:
        c.text(W - 280, y, line, 13)
        y += 30
    c.text(W - 280, H - 160, number, 58)
    for text, x, yy in plan_marks:
        c.text(x, yy, text, 9)
    doc.save(path)
    doc.close()
    return str(path)


def facts_of(path):
    return load_sheet_facts(str(path))[0]


@pytest.mark.parametrize("rotation", [0, 90, 180, 270])
def test_a_sheet_is_read_the_same_however_it_is_stored(tmp_path, rotation):
    f = facts_of(sheet(tmp_path / "a.pdf", rotation=rotation))

    i = identify(f)

    assert (i.sheet_number, i.title, i.discipline, i.kind, i.confidence) == ("M1.6", "MECHANICAL PLAN - BUILDING A", "Mechanical", "Plan", "title-block")
    assert (f.rotation, round(f.width), round(f.height)) == (rotation, round(W), round(H))      # the size is the displayed one


def test_a_title_set_over_several_lines_is_one_title(tmp_path):
    f = facts_of(sheet(tmp_path / "a.pdf", number="M1-S1", title_lines=("SUBGRADE LEVEL S1", "FLOOR PLAN -", "MECHANICAL")))

    assert identify(f).title == "SUBGRADE LEVEL S1 FLOOR PLAN - MECHANICAL"


def test_the_project_name_and_address_are_never_taken_for_the_title(tmp_path):
    f = facts_of(sheet(tmp_path / "a.pdf", title_lines=("MECHANICAL SCHEDULES",), project="EVER - LEXINGTON", project_size=26))

    i = identify(f)

    assert (i.title, i.kind) == ("MECHANICAL SCHEDULES", "Schedule")


def test_other_disciplines_are_named_so_they_can_be_left_out(tmp_path):
    f = facts_of(sheet(tmp_path / "a.pdf", number="E1.1", title_lines=("POWER PLAN",)))

    i = identify(f)

    assert (i.sheet_number, i.discipline, i.kind) == ("E1.1", "Electrical", "Plan")


def test_a_page_with_no_text_says_so_and_still_has_a_hash(tmp_path):
    doc = pymupdf.open()
    page = doc.new_page(width=800, height=600)
    page.draw_rect(pymupdf.Rect(100, 100, 300, 300))
    doc.save(tmp_path / "t.pdf")
    doc.close()

    f = facts_of(tmp_path / "t.pdf")
    i = identify(f)

    assert (f.has_text, i.sheet_number, i.confidence) == (False, None, "none")
    assert len(f.content_hash) == 64 and i.notes


def test_equipment_tags_and_insulation_values_are_not_sheet_numbers():
    assert all(SHEET_NO.match(t) for t in ["M1.6", "M1-S1", "M0-00", "MD101", "E0.2", "M-101", "M1.12L"])
    from ductwork_sidecar.sheet_id import _NOT_A_SHEET

    assert all(_NOT_A_SHEET.match(t) for t in ["R-21", "EF-1", "FD4", "CO2"])


# ---------------------------------------------------------------- the content hash: how a repeated sheet is recognised


def test_the_same_drawing_reissued_under_another_title_block_has_the_same_hash(tmp_path):
    a = facts_of(sheet(tmp_path / "a.pdf", project="REDWOOD FRANKLIN", date="MAY 1, 2025", number="M1.2"))
    b = facts_of(sheet(tmp_path / "b.pdf", project="REDWOOD WHITELAND", date="MAY 19, 2023", number="M1.7", title_lines=("MECHANICAL PLAN - ELSEWHERE",)))

    assert a.content_hash == b.content_hash


def test_a_change_to_the_drawing_itself_changes_the_hash(tmp_path):
    a = facts_of(sheet(tmp_path / "a.pdf"))
    moved = facts_of(sheet(tmp_path / "b.pdf", plan_marks=(("4x10x7", 460, 400),)))
    relabelled = facts_of(sheet(tmp_path / "c.pdf", plan_marks=(("4x12x7", 400, 400),)))

    assert len({a.content_hash, moved.content_hash, relabelled.content_hash}) == 3


def test_the_hash_does_not_depend_on_how_the_sheet_is_stored(tmp_path):
    upright = facts_of(sheet(tmp_path / "a.pdf", rotation=0))
    sideways = facts_of(sheet(tmp_path / "b.pdf", rotation=90))

    assert upright.content_hash == sideways.content_hash


# ---------------------------------------------------------------- the command line


def test_the_command_writes_the_pages_in_the_shape_the_server_takes(tmp_path):
    pdf = sheet(tmp_path / "a.pdf", rotation=270)
    out = tmp_path / "pages.json"

    assert main(["sheets", pdf, "--out", str(out)]) == 0

    page = json.loads(out.read_text())["pages"][0]
    assert set(page) == {"pageNo", "widthPts", "heightPts", "rotation", "sheetNumber", "title", "discipline", "kind", "contentHash", "hasTextLayer"}
    assert (page["pageNo"], page["rotation"], page["sheetNumber"], page["kind"], page["hasTextLayer"]) == (1, 270, "M1.6", "Plan", True)


# ---------------------------------------------------------------- helpers


@pytest.mark.parametrize("number,expected", [("M1.6", "Mechanical"), ("MD101", "Mechanical"), ("E0.1", "Electrical"), ("P2.1", "Plumbing"), ("A53", "Architectural"), ("T1.0", "General"), ("FP-1", "Fire Protection")])
def test_discipline_comes_from_the_prefix(number, expected):
    assert _discipline(number, None) == expected


def test_discipline_falls_back_to_the_title():
    assert _discipline("X9", "MECHANICAL SCHEDULES") == "Mechanical"
    assert _discipline(None, "SOMETHING ELSE") is None


@pytest.mark.parametrize("title,kind", [
    ("MECHANICAL SCHEDULES", "Schedule"), ("MECHANICAL SYMBOLS AND ABBREVIATIONS", "Legend"), ("STAIR PRESSURIZATION RISER DIAGRAM", "Riser"),
    ("MECHANICAL DETAILS", "Detail"), ("GENERAL NOTES - MECHANICAL", "Notes"), ("FIRST LEVEL FLOOR PLAN - MECHANICAL", "Plan"),
    ("MECHANICAL DEMOLITION PLANS", "Other"), ("COMCHECK REPORT", "Other"), (None, "Other")])
def test_kind_comes_from_the_title(title, kind):
    assert _kind(title) == kind


# ---------------------------------------------------------------- text-less sheets read by OCR (engine faked)


def test_ocr_lines_become_a_number_and_a_title_with_the_spaces_put_back():
    upright = [("M1.3", 98.0, 1.0), ("3 OF10", 33.0, 0.9), ("PROJECT#:33922", 261.0, 0.97)]
    turned = [("MECHANICALPLAN-MEADOWOOD", 35.0, 1.0), ("3660EmbassyParkway", 32.0, 1.0)]

    i = interpret(upright, turned)

    assert (i.sheet_number, i.title, i.kind, i.discipline, i.confidence) == ("M1.3", "MECHANICAL PLAN - MEADOWOOD", "Plan", "Mechanical", "ocr")
    assert i.notes


def test_a_sentence_from_the_notes_column_is_not_a_title():
    turned = [("REFER TO SHEET M1.0 FOR MECHANICAL SPECS AND NOTES.", 600.0, 1.0)]

    assert interpret([("M1.8", 90.0, 1.0)], turned).title is None


def test_respace_only_splits_tokens_made_entirely_of_known_words():
    assert respace("MECHANICALPLAN-MEADOWOOD") == "MECHANICAL PLAN - MEADOWOOD"
    assert respace("MECHANICALNOTESSPECSANDDETAILS") == "MECHANICAL NOTES SPECS AND DETAILS"
    assert respace("HAYDENWOODSUNROOM") == "HAYDENWOODSUNROOM"                      # unknown words are left alone, never half-split
    assert respace("MECHANICAL PLAN") == "MECHANICAL PLAN"


def test_the_facts_round_trip_to_a_page_report(tmp_path):
    f = facts_of(sheet(tmp_path / "a.pdf"))

    report = to_page_report(f, identify(f))

    assert report["contentHash"] == f.content_hash and report["widthPts"] == round(W, 2)
