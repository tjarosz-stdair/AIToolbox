import csv
import pathlib
import sys

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools"))

import engine_parity as parity  # noqa: E402
from engine_parity import Line, compare, lines_from_rows  # noqa: E402


def L(category, item, size, unit, qty):
    return Line(category, item, size, unit, qty)


def test_identical_results_have_no_differences():
    lines = [L("Duct", "Rectangular", "12x8", "LF", 100.5), L("Fittings", "Elbow 90", "", "EA", 40)]
    assert compare(lines, list(reversed(lines))) == []


def test_quantity_changes_and_missing_and_extra_lines_are_all_reported():
    expected = [L("Duct", "Rect", "12x8", "LF", 100), L("Fittings", "Tee", "", "EA", 5), L("Fittings", "Cross", "", "EA", 1)]
    actual = [L("Duct", "Rect", "12x8", "LF", 101), L("Fittings", "Tee", "", "EA", 5), L("Fittings", "Wye", "", "EA", 2)]

    kinds = sorted(d.describe().split(":")[0] for d in compare(expected, actual))

    assert kinds == ["extra in actual", "missing in actual", "quantity differs"]


def test_tiny_rounding_is_tolerated_but_a_real_change_is_not():
    a = [L("Duct", "Rect", "12x8", "LF", 100.0)]
    assert compare(a, [L("Duct", "Rect", "12x8", "LF", 100.0004)]) == []
    assert len(compare(a, [L("Duct", "Rect", "12x8", "LF", 100.01)])) == 1


def test_keys_ignore_case_and_extra_spaces_and_lines_with_the_same_key_are_summed():
    expected = [L("Duct", "Rect  Duct", "12x8", "LF", 30)]
    actual = [L("duct", "rect duct", "12X8", "lf", 10), L("DUCT", "Rect Duct", "12x8", "LF", 20)]
    assert compare(expected, actual) == []


def test_display_only_callout_rows_are_set_aside_by_default():
    expected = [L("Air", "Terminal", "", "EA", 0, ) , Line("Duct", "Rect", "12x8", "LF", 100)]
    expected[0] = Line("Air", "Terminal", "", "EA", 0, "Callout count")
    actual = [L("Air", "Terminal", "", "EA", 36), L("Duct", "Rect", "12x8", "LF", 100)]

    kept, ignored = parity.split_callouts(expected)

    assert [l.item for l in kept] == ["Rect"] and len(ignored) == 1
    assert compare(expected, actual, ignore_keys=ignored) == []
    assert len(compare(expected, actual)) == 1            # compared when not set aside


def test_section_titles_and_blank_rows_are_not_lines():
    rows = [
        ["HVAC Take-off Summary", None, None, None, None, None, None],
        [None] * 7,
        ["Category", "Item", "Size / model", "Unit", "Total qty", "of which LOW confidence", "Basis"],
        ["Equipment", None, None, None, None, None, None],                    # a section title
        ["Equipment", "AHU-1", "MODEL-A", "EA", 1, 0, "schedule"],
        [None, "AHU-2", "MODEL-B", "EA", 2.0, 0, "schedule"],                  # category carried from above
        ["Duct", "Total", "", "LF", "n/a", None, None],                        # no numeric quantity
    ]
    lines = lines_from_rows(rows)
    assert [(l.item, l.qty) for l in lines] == [("AHU-1", 1.0), ("AHU-2", 2.0)]


def test_rows_before_the_header_are_ignored():
    assert lines_from_rows([["Equipment", "AHU-1", "M", "EA", 1], ["Category", "Item", "Size / model", "Unit", "Total qty"]]) == []


def test_the_engine_csv_is_read_with_the_same_shape(tmp_path):
    path = tmp_path / "summary.csv"
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["category", "item", "size", "unit", "total_qty"])
        w.writerow(["Equipment", "AHU-1", "MODEL-A", "EA", "1.0"])
        w.writerow(["Equipment", "", "", "", ""])
        w.writerow(["Duct", "Rect", "12x8", "LF", "100.5"])

    lines = parity.load_actual_csv(str(path))

    assert [(l.category, l.item, l.size, l.unit, l.qty) for l in lines] == [
        ("Equipment", "AHU-1", "MODEL-A", "EA", 1.0), ("Duct", "Rect", "12x8", "LF", 100.5)]


def test_a_missing_summary_sheet_is_an_error(tmp_path):
    openpyxl = pytest.importorskip("openpyxl")
    wb = openpyxl.Workbook()
    wb.active.title = "Other"
    path = tmp_path / "ref.xlsx"
    wb.save(path)

    with pytest.raises(ValueError):
        parity.load_expected_xlsx(str(path))


def test_the_expected_workbook_summary_is_read(tmp_path):
    openpyxl = pytest.importorskip("openpyxl")
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = "Summary"
    ws.append(["Take-off Summary"])
    ws.append(["Category", "Item", "Size / model", "Unit", "Total qty", "LOW", "Basis"])
    ws.append(["Equipment", "AHU-1", "MODEL-A", "EA", 1])
    ws.append([None, "AHU-2", "MODEL-B", "EA", 2])
    path = tmp_path / "ref.xlsx"
    wb.save(path)

    lines = parity.load_expected_xlsx(str(path))

    assert [(l.category, l.item, l.qty) for l in lines] == [("Equipment", "AHU-1", 1.0), ("Equipment", "AHU-2", 2.0)]
