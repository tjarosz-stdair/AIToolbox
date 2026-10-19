"""Reading equipment schedules from a sheet's text, on synthetic drawings. No customer drawing is used or stored here."""

from __future__ import annotations

import json

import pymupdf
import pytest

from ductwork_sidecar.__main__ import main
from ductwork_sidecar.pdf_layout import load_sheet_facts
from ductwork_sidecar.schedules import MARK_RE, count_marks, mark_regex, params_hash, producer, read_schedules
from tests.sheets import H, W, _Canvas


def draw(path, build, rotation=0):
    doc = pymupdf.open()
    stored_w, stored_h = (H, W) if rotation in (90, 270) else (W, H)
    page = doc.new_page(width=stored_w, height=stored_h)
    page.set_rotation(rotation)
    c = _Canvas(page, rotation)
    c.rect(30, 30, W - 30, H - 30)
    build(c)
    doc.save(path)
    doc.close()
    return str(path)


def schedule(c, x, y, title, headers, rows, pitch=26, title_size=24, header_y=None):
    """A schedule: a big title, a header row of (text, x offset), and data rows of cell texts at the same offsets."""
    c.text(x + 200, y, title, title_size)
    hy = header_y if header_y is not None else y + 60
    for text, dx in headers:
        c.text(x + dx, hy, text, 11)
    for i, row in enumerate(rows):
        for (text, dx), cell in zip(headers, row):
            if cell:
                c.text(x + dx, hy + 40 + i * pitch, cell, 11)


EF = [("MARK", 0), ("SERVES", 90), ("MANUFACTURER", 230), ("MODEL", 360), ("AIRFLOW (CFM)", 470), ("REMARKS", 600)]
EF_ROWS = [["EF-1", "BATHROOM", "PANASONIC", "FV-0510VS1", "50", "1"], ["EF-2", "KITCHEN", "PANASONIC", "FV-1115VK2", "100", "2"], ["EF-3", "LAUNDRY", "GREENHECK", "SP-A90", "80", ""]]


def facts_of(path, page=0):
    return load_sheet_facts(str(path), [page], text_only=True)[0]


@pytest.mark.parametrize("rotation", [0, 90, 270])
def test_a_schedule_is_read_the_same_however_the_sheet_is_stored(tmp_path, rotation):
    path = draw(tmp_path / "a.pdf", lambda c: schedule(c, 200, 300, "EXHAUST FAN SCHEDULE", EF, EF_ROWS), rotation)

    (sch,) = read_schedules(facts_of(path), "M5.1")

    assert (sch.title, sch.sheet, len(sch.rows)) == ("EXHAUST FAN SCHEDULE", "M5.1", 3)
    assert [r.mark for r in sch.rows] == ["EF-1", "EF-2", "EF-3"]
    first = sch.rows[0]
    assert (first.manufacturer, first.model, first.location, first.capacity, first.quantity) == ("PANASONIC", "FV-0510VS1", "BATHROOM", "50 CFM", None)


def test_a_row_box_is_where_the_row_is_on_the_sheet(tmp_path):
    path = draw(tmp_path / "a.pdf", lambda c: schedule(c, 200, 300, "EXHAUST FAN SCHEDULE", EF, EF_ROWS))

    (sch,) = read_schedules(facts_of(path), "M5.1")

    x0, y0, x1, y1 = sch.rows[1].box
    assert 190 <= x0 <= 215 and x1 > 700 and y1 - y0 < 20
    assert sch.rows[1].box[1] > sch.rows[0].box[1]


def test_a_quantity_column_gives_the_quantity_and_without_one_there_is_none(tmp_path):
    with_qty = [("MARK", 0), ("SERVES", 90), ("QTY", 230), ("MODEL", 330)]
    path = draw(tmp_path / "a.pdf", lambda c: schedule(c, 200, 300, "LOUVER SCHEDULE", with_qty, [["L-1", "MECH ROOM", "4", "EHH-601"], ["L-2", "ROOF", "", "EHH-602"]]))

    (sch,) = read_schedules(facts_of(path), "M5")

    assert [r.quantity for r in sch.rows] == [4.0, None]


def test_a_header_over_several_columns_is_part_of_each_columns_name(tmp_path):
    def build(c):
        c.text(200 + 200, 300, "UNIT HEATER SCHEDULE", 24)
        c.text(200 + 230, 342, "HEATING", 11)
        c.text(200 + 360, 342, "HEATING", 11)
        for text, dx in (("MARK", 0), ("SERVES", 90), ("CAPACITY (KW)", 230), ("CAPACITY (MBH)", 360), ("MODEL", 500)):
            c.text(200 + dx, 360, text, 11)
        for i, row in enumerate([["EUH-1", "STAIR A", "4.8", "16.5", "AFA848D"], ["EUH-2", "STAIR B", "4.8", "16.5", "AFA848D"]]):
            for (dx, cell) in zip((0, 90, 230, 360, 500), row):
                c.text(200 + dx, 400 + i * 26, cell, 11)

    (sch,) = read_schedules(facts_of(draw(tmp_path / "a.pdf", build)), "M5")

    assert "HEATING / CAPACITY (KW)" in sch.headers and "HEATING / CAPACITY (MBH)" in sch.headers
    assert [r.capacity for r in sch.rows] == ["4.8 kW", "4.8 kW"]


def test_text_that_wraps_onto_a_second_line_stays_with_its_row(tmp_path):
    def build(c):
        schedule(c, 200, 300, "EXHAUST FAN SCHEDULE", EF, [["EF-1", "BATHROOM", "PANASONIC", "FV-0510VS1", "50", "1"], ["EF-2", "KITCHEN", "PANASONIC", "FV-1115VK2", "100", "2"]])
        c.text(200 + 90, 360 + 40 + 13, "AND CORRIDOR", 11)         # wraps under EF-1's SERVES, with the mark column empty

    (sch,) = read_schedules(facts_of(draw(tmp_path / "a.pdf", build)), "M5")

    assert [r.mark for r in sch.rows] == ["EF-1", "EF-2"]
    assert sch.rows[0].location == "BATHROOM AND CORRIDOR"


def test_two_schedules_one_above_the_other_are_read_separately(tmp_path):
    def build(c):
        schedule(c, 200, 200, "EXHAUST FAN SCHEDULE", EF, EF_ROWS)
        schedule(c, 200, 600, "UNIT HEATER SCHEDULE", [("MARK", 0), ("SERVES", 90), ("MODEL", 230), ("WATTS", 340)], [["UH-1", "SHOP", "MUH-05", "10000 W"]])

    first, second = read_schedules(facts_of(draw(tmp_path / "a.pdf", build)), "M5")

    assert ([r.mark for r in first.rows], [r.mark for r in second.rows]) == (["EF-1", "EF-2", "EF-3"], ["UH-1"])
    assert second.rows[0].capacity == "10000 W"


def test_two_schedules_side_by_side_are_read_separately(tmp_path):
    def build(c):
        schedule(c, 100, 300, "EXHAUST FAN SCHEDULE", [("MARK", 0), ("SERVES", 90), ("MODEL", 230)], [["EF-1", "BATH", "X1"], ["EF-2", "KITCHEN", "X2"]])
        schedule(c, 1100, 300, "UNIT HEATER SCHEDULE", [("MARK", 0), ("SERVES", 90), ("MODEL", 230)], [["UH-1", "SHOP", "H1"]])

    left, right = read_schedules(facts_of(draw(tmp_path / "a.pdf", build)), "M5")

    assert (left.title, [r.mark for r in left.rows], right.title, [r.mark for r in right.rows]) == ("EXHAUST FAN SCHEDULE", ["EF-1", "EF-2"], "UNIT HEATER SCHEDULE", ["UH-1"])


def test_a_table_under_a_title_with_no_mark_column_is_not_taken_from_the_next_table_down(tmp_path):
    def build(c):
        c.text(400, 200, "ROOM FINISH SCHEDULE", 24)
        c.text(200, 260, "ROOM", 11)
        c.text(400, 260, "FLOOR", 11)
        schedule(c, 200, 500, "EXHAUST FAN SCHEDULE", EF, EF_ROWS)

    schedules = read_schedules(facts_of(draw(tmp_path / "a.pdf", build)), "M5")

    assert [s.title for s in schedules] == ["EXHAUST FAN SCHEDULE"]


def test_a_list_of_notes_is_not_a_schedule(tmp_path):
    def build(c):
        c.text(300, 300, "GENERAL NOTES", 24)
        for i in range(6):
            c.text(300, 340 + i * 20, f"{i + 1}. PROVIDE EF-1 WITH A DISCONNECT.", 11)

    assert read_schedules(facts_of(draw(tmp_path / "a.pdf", build)), "M0") == []


def test_a_schedule_whose_type_column_is_named_type_mark_is_read(tmp_path):
    grd = [("TYPE MARK", 0), ("MANUFACTURER", 90), ("MODEL", 230), ("NECK SIZE", 330), ("MAXIMUM FLOW", 440)]
    path = draw(tmp_path / "a.pdf", lambda c: schedule(c, 200, 300, "GRILLE, REGISTER AND DIFFUSER SCHEDULE", grd, [["RG-1", "PRICE", "610", '20"x10"', "500 CFM"], ["SD-1", "PRICE", "SPD", '24"x24"', "400 CFM"]]))

    (sch,) = read_schedules(facts_of(path), "M5")

    assert [(r.mark, r.size, r.capacity) for r in sch.rows] == [("RG-1", '20"x10"', "500 CFM"), ("SD-1", '24"x24"', "400 CFM")]


# ---------------------------------------------------------------- marks and counting them


@pytest.mark.parametrize("mark", ["EF-1", "SSAC-S2-1", "JF-XX-#", "AHU-1", "FCU1", "RG-2", "MSCCU-1A"])
def test_marks_are_recognised(mark):
    assert MARK_RE.match(mark)


@pytest.mark.parametrize("text", ["BATHROOM", "50", "1,035", "4x10x7", ""])
def test_ordinary_cell_text_is_not_a_mark(text):
    assert not MARK_RE.match(text.replace(" ", ""))


def test_a_scheduled_mark_with_placeholders_matches_the_tags_drawn():
    rx = mark_regex("JF-XX-#")

    assert all(rx.match(t) for t in ["JF-A2-17", "JF-L1-3", "jf-b-9"]) and not rx.match("JF-1")


def test_marks_are_counted_as_tags_on_a_plan_but_not_in_a_sentence(tmp_path):
    def build(c):
        for i, (x, y) in enumerate([(300, 300), (700, 420), (1100, 500)]):
            c.text(x, y, "EF-1", 9)
        c.text(300, 700, "EF-2 100 CFM", 9)
        c.text(300, 800, "SEE EF-1 AND EF-2 AND THE NOTES ON THIS SHEET FOR MORE", 9)

    counts = {c.mark: c for c in count_marks(facts_of(draw(tmp_path / "a.pdf", build)), "M1.2", ["EF-1", "EF-2", "EF-9"])}

    assert (counts["EF-1"].count, counts["EF-2"].count, "EF-9" in counts) == (3, 1, False)
    assert counts["EF-1"].sheet == "M1.2" and counts["EF-1"].box[0] == pytest.approx(300, abs=3)


def test_the_producer_names_the_reader_and_changes_when_what_it_recognises_changes():
    p = producer()

    assert (p["kind"], p["algorithmName"], p["algorithmVersion"]) == ("Algorithm", "schedule-reader", "0.1.0")
    assert p["paramsHash"] == params_hash() and len(p["paramsHash"]) == 64


# ---------------------------------------------------------------- the commands


def test_the_schedules_and_marks_commands_round_trip_a_spec(tmp_path):
    pdf = draw(tmp_path / "a.pdf", lambda c: (schedule(c, 200, 300, "EXHAUST FAN SCHEDULE", EF, EF_ROWS), c.text(1500, 900, "EF-1", 9), c.text(1700, 900, "EF-1", 9)))
    spec = tmp_path / "spec.json"
    out = tmp_path / "out.json"

    spec.write_text(json.dumps({"pages": [{"page": 1, "sheet": "M5.1"}]}))
    assert main(["schedules", pdf, "--spec", str(spec), "--out", str(out)]) == 0
    rows = json.loads(out.read_text())["schedules"][0]["rows"]
    assert [r["mark"] for r in rows] == ["EF-1", "EF-2", "EF-3"] and rows[0]["manufacturer"] == "PANASONIC"

    spec.write_text(json.dumps({"marks": ["EF-1", "EF-3"], "pages": [{"page": 1, "sheet": "M1.2"}]}))
    assert main(["marks", pdf, "--spec", str(spec), "--out", str(out)]) == 0
    result = json.loads(out.read_text())
    assert result["producer"]["algorithmName"] == "schedule-reader"
    assert {c["mark"]: c["count"] for c in result["counts"]} == {"EF-1": 3, "EF-3": 1}      # EF-1 is in the schedule too, and a schedule is counted when it is named


def test_a_tag_stacked_in_a_bubble_is_counted_as_one_tag(tmp_path):
    def build(c):
        # EUH over S1-A, centred, as drawn in a bubble; and a one-line EUH-S1-B beside it.
        c.text(500, 500, "EUH", 8.7)
        c.text(500, 511, "S1-A", 8.7)
        c.text(900, 500, "EUH-S1-B", 8.7)
        # Not a tag: the prefix with something below it that is not under it.
        c.text(1300, 500, "EUH", 8.7)
        c.text(1500, 511, "S1-A", 8.7)

    counts = {c.mark: c for c in count_marks(facts_of(draw(tmp_path / "a.pdf", build)), "M1-S1", ["EUH-S1-A", "EUH-S1-B", "EUH-S2-A"])}

    assert (counts["EUH-S1-A"].count, counts["EUH-S1-B"].count, "EUH-S2-A" in counts) == (1, 1, False)
    box = counts["EUH-S1-A"].box
    assert box[0] == pytest.approx(500, abs=4) and box[3] - box[1] > 15            # the box takes in both lines


def test_a_stacked_tag_is_not_built_from_a_suffix_that_belongs_to_another_prefix(tmp_path):
    def build(c):
        c.text(500, 500, "EUH", 8.7)
        c.text(500, 511, "S1-A", 8.7)
        c.text(500, 540, "EF", 8.7)               # a different prefix; its own suffix is further down

    counts = {c.mark: c.count for c in count_marks(facts_of(draw(tmp_path / "a.pdf", build)), "M1", ["EUH-S1-A", "EF-S1-A"])}

    assert counts == {"EUH-S1-A": 1}
