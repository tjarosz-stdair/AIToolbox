import json
import pathlib
from collections import Counter

import pytest

from ductwork_sidecar import manifest as manifest_module
from ductwork_sidecar import registry
from ductwork_sidecar.manifest import build_manifest
from ductwork_sidecar.notes import parse_flag_notes
from ductwork_sidecar.pdf_layout import load_page_layout
from ductwork_sidecar.residential import detect, detect_building_type

from sheets import Sheet, build

SCHEMA = pathlib.Path(__file__).resolve().parents[2] / "AIToolbox.Ductwork.Contracts" / "schemas" / "task-result-manifest.schema.json"


def standard_sheet(**overrides) -> Sheet:
    sheet = Sheet(
        boots=[
            (400, 300, "4x10x7", "100 CFM"),
            (700, 300, "4x10x7", "75 CFM"),
            (1000, 300, "4x10x4", "40 CFM"),
            (400, 600, "4x12x7", "100 CFM"),
        ],
        tags=[(1200, 900, "EF-1"), (1400, 900, "EF/LIGHT"), (900, 900, "12x24 R.A.")],
        flags=[(500, 1000, 2), (800, 1000, 2), (1100, 1000, 2), (1300, 1100, 8)],
        plain=[(300, 1200, "BEDROOM 2"), (520, 1060, "2"), (1500, 1200, "BATH 2")],
    )
    for key, value in overrides.items():
        setattr(sheet, key, value)
    return sheet


def run(tmp_path, sheet: Sheet, rotation: int = 0, building=None):
    pdf = build(sheet, str(tmp_path / f"sheet{rotation}.pdf"), rotation)
    layout = load_page_layout(pdf)
    return layout, detect(layout, building_type=building, sheet_ref="s1")


def by_type_size(result) -> Counter:
    return Counter((d["type"], d.get("size")) for d in result.detections)


# ---- what is counted

def test_labels_tags_and_flags_are_counted_as_drawn(tmp_path):
    _, result = run(tmp_path, standard_sheet())

    counts = by_type_size(result)
    assert counts[("Boot", "4x10x7")] == 2
    assert counts[("Boot", "4x10x4")] == 1
    assert counts[("Boot", "4x12x7")] == 1
    assert counts[("Fan", "EF-1")] == 1
    assert counts[("Fan", "EF/Light")] == 1
    assert counts[("Grille", "12x24 RA")] == 1
    assert counts[("DryerBoot", "Dryer Boot")] == 1
    assert len(result.detections) == 11  # 4 boots, 3 tags, 3 pass-through flags, 1 dryer flag


def test_a_pass_through_flag_is_one_detection_not_two(tmp_path):
    """Two grilles per flag is a convention a user approves as a rule. The detector reports what is drawn."""
    _, result = run(tmp_path, standard_sheet())
    assert by_type_size(result)[("Grille", "14x9 Pass Through")] == 3


def test_digits_outside_triangles_are_never_flags(tmp_path):
    layout, result = run(tmp_path, standard_sheet())
    # The sheet has the digit 2 in "BEDROOM 2", in "BATH 2" and alone next to a flag, and three real flags with 2.
    assert by_type_size(result)[("Grille", "14x9 Pass Through")] == 3
    assert result.diagnostics["digits_outside_triangles"] >= 3
    assert result.diagnostics["triangles"] == 4


def test_cfm_is_paired_with_the_label_above_it_and_each_cfm_is_used_once(tmp_path):
    _, result = run(tmp_path, standard_sheet())
    boots = [d for d in result.detections if d["type"] == "Boot"]
    assert all("CFM" in d["evidence"] for d in boots)
    assert not [d for d in result.detections if d["type"] == "CfmOnlyTerminal"]
    cfms = [d["evidence"].split(" with ")[1] for d in boots]
    assert sorted(cfms) == sorted(["100 CFM", "75 CFM", "40 CFM", "100 CFM"])


def test_a_label_without_cfm_is_still_a_detection(tmp_path):
    sheet = standard_sheet(boots=[(400, 300, "4x10x7", None)])
    _, result = run(tmp_path, sheet)
    boot = next(d for d in result.detections if d["type"] == "Boot")
    assert "CFM" not in boot["evidence"]
    assert boot["confidence"] < 0.95


def test_notes_text_is_not_mistaken_for_plan_labels(tmp_path):
    """The notes column contains '16x8x30' and other size-like text, and must not be counted."""
    _, result = run(tmp_path, standard_sheet())
    assert not [d for d in result.detections if d.get("size") == "16x8x30"]


# ---- what is raised for the user instead of guessed

def test_a_two_dimension_label_is_reported_as_drawn_with_a_finding(tmp_path):
    sheet = standard_sheet(boots=[(400, 300, "4x12", "100 CFM")])
    _, result = run(tmp_path, sheet)

    boot = next(d for d in result.detections if d["type"] == "Boot")
    assert boot["size"] == "4x12"  # no depth invented
    finding = next(f for f in result.findings if f["kind"] == "GrammarVariant")
    assert finding["detectionId"] == boot["detectionId"]
    assert boot["confidence"] < 0.8


def test_a_cfm_only_terminal_is_reported_with_candidates_and_never_sized(tmp_path):
    sheet = standard_sheet(boots=[(400, 300, "4x10x7", "100 CFM")], plain=[(1200, 300, "100 CFM")])
    _, result = run(tmp_path, sheet)

    orphan = next(d for d in result.detections if d["type"] == "CfmOnlyTerminal")
    assert "size" not in orphan
    assert orphan["tag"] == "100 CFM"
    assert orphan["confidence"] <= 0.35
    finding = next(f for f in result.findings if f["kind"] == "AmbiguousLabel")
    assert finding["candidates"] == ["4x10x7"]  # terminal sizes only: no fans or grilles


def test_a_sheet_without_a_text_layer_says_so_instead_of_returning_an_empty_answer(tmp_path):
    _, result = run(tmp_path, standard_sheet(with_text=False))

    assert result.detections == []
    assert result.diagnostics["no_text_layer"] is True
    assert [f["kind"] for f in result.findings] == ["MissingEvidence"]


def test_flags_without_a_matching_note_are_diagnostics_not_detections(tmp_path):
    sheet = standard_sheet(flags=[(500, 1000, 4), (800, 1000, 5)])
    _, result = run(tmp_path, sheet)
    assert result.diagnostics["unmapped_flags"] == {4: 1, 5: 1}
    assert not [d for d in result.detections if d["type"] in ("Grille", "DryerBoot") and "flag" in d["evidence"]]



def test_airflow_written_on_two_lines_is_not_silently_dropped(tmp_path):
    """Some sheets stack the number above the word CFM. Without joining them the terminal vanishes from the count."""
    sheet = standard_sheet(boots=[(400, 300, "4x10x7", "100 CFM")])
    sheet.stacked_cfm = [(900, 600, 100), (1200, 600, 75)]
    _, result = run(tmp_path, sheet)

    orphans = [d for d in result.detections if d["type"] == "CfmOnlyTerminal"]
    assert sorted(d["tag"] for d in orphans) == ["100 CFM", "75 CFM"]
    assert len([f for f in result.findings if f["kind"] == "AmbiguousLabel"]) == 2


def test_stacked_airflow_under_a_size_label_pairs_with_it(tmp_path):
    sheet = standard_sheet(boots=[])
    sheet.plain = [(400, 300, "4x12x7")]
    sheet.stacked_cfm = [(400, 313, 100)]
    _, result = run(tmp_path, sheet)

    boot = next(d for d in result.detections if d["type"] == "Boot")
    assert "100 CFM" in boot["evidence"]
    assert not [d for d in result.detections if d["type"] == "CfmOnlyTerminal"]


def test_a_number_that_is_not_above_a_cfm_word_is_left_alone(tmp_path):
    sheet = standard_sheet(boots=[])
    sheet.plain = [(400, 300, "100"), (900, 700, "CFM")]  # far apart: two unrelated lines
    _, result = run(tmp_path, sheet)
    assert not [d for d in result.detections if d["type"] == "CfmOnlyTerminal" and d.get("tag") == "100 CFM"]


def test_the_outside_air_louver_note_is_counted_from_its_flag(tmp_path):
    sheet = standard_sheet(flags=[(500, 1000, 11)])
    _, result = run(tmp_path, sheet)
    assert by_type_size(result)[("Louver", "Outside Air Louver")] == 1

# ---- coordinates and rotation

@pytest.mark.parametrize("rotation", [0, 90, 180, 270])
def test_rotated_sheets_give_the_same_counts_and_the_same_upright_positions(tmp_path, rotation):
    _, upright = run(tmp_path, standard_sheet(), rotation=0)
    layout, rotated = run(tmp_path, standard_sheet(), rotation=rotation)

    assert layout.rotation == rotation
    assert by_type_size(rotated) == by_type_size(upright)

    def centers(result):
        return sorted(
            (round((d["box"]["x0"] + d["box"]["x1"]) / 2), round((d["box"]["y0"] + d["box"]["y1"]) / 2), d["type"], d.get("size"))
            for d in result.detections
        )

    for (ax, ay, at, asz), (bx, by, bt, bsz) in zip(centers(upright), centers(rotated)):
        assert (at, asz) == (bt, bsz)
        assert abs(ax - bx) <= 4 and abs(ay - by) <= 4


# ---- notes and titles

def test_notes_are_parsed_across_continuation_lines_and_two_digit_numbers(tmp_path):
    layout, _ = run(tmp_path, standard_sheet())
    notes = parse_flag_notes(layout)
    assert notes.notes[2].startswith("14X9 PASS THROUGH")
    assert notes.notes[8] == "RECESSED DRYER VENT BOX."
    assert "ROOF JACK" in notes.notes[10]  # continuation line joined
    assert len(notes.notes) == 11


def test_building_type_is_read_from_the_title_and_can_be_overridden(tmp_path):
    layout, result = run(tmp_path, standard_sheet(title="MECH. PLANS - UNIT B WEST"))
    assert detect_building_type(layout) == "UNIT B WEST"
    assert all(d["buildingType"] == "UNIT B WEST" for d in result.detections)

    _, overridden = run(tmp_path, standard_sheet(), building="Type 9")
    assert all(d["buildingType"] == "Type 9" for d in overridden.detections)


# ---- the contract

def test_the_manifest_conforms_to_the_committed_contract_schema(tmp_path):
    jsonschema = pytest.importorskip("jsonschema")
    _, result = run(tmp_path, standard_sheet(boots=[(400, 300, "4x12", "100 CFM"), (800, 300, "4x10x7", "75 CFM")], plain=[(1200, 300, "100 CFM")]))

    manifest = build_manifest(result, "task-1")
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    jsonschema.validate(manifest, schema)

    assert manifest["producer"]["kind"] == "Algorithm"
    assert manifest["contractVersion"] == manifest_module.CONTRACT_VERSION
    assert len({d["detectionId"] for d in manifest["detections"]}) == len(manifest["detections"])


def test_every_detection_stays_inside_its_own_unit_interval_and_page(tmp_path):
    layout, result = run(tmp_path, standard_sheet())
    for d in result.detections:
        assert 0.0 <= d["confidence"] <= 1.0
        assert 0 <= d["box"]["x0"] < d["box"]["x1"] <= layout.width
        assert 0 <= d["box"]["y0"] < d["box"]["y1"] <= layout.height


def test_output_is_deterministic(tmp_path):
    _, a = run(tmp_path, standard_sheet())
    _, b = run(tmp_path, standard_sheet())
    assert a.detections == b.detections
    assert a.findings == b.findings


def test_params_hash_changes_when_the_grammar_changes(monkeypatch):
    before = manifest_module.params_hash()
    assert before == manifest_module.params_hash()
    monkeypatch.setattr(registry, "LABEL_RULES", registry.LABEL_RULES[:-1])
    assert manifest_module.params_hash() != before


# ---- OCR-shaped input: dropped spaces, split digits, lower trust

def test_ocr_text_with_dropped_spaces_still_matches_notes_and_heading():
    from ductwork_sidecar.notes import parse_flag_notes
    from ductwork_sidecar.pdf_layout import Box, PageLayout, TextLine

    def line(text, x0, y0, x1, y1):
        return TextLine(text, Box(x0, y0, x1, y1))

    lines = [
        line("FLAGNOTES-MECHANIC", 600, 185, 685, 190),
        line("2.", 580, 200, 583, 204),
        line("14X9PASSTHROUGHLOUVEREDVENT,STAGGERED,ABOVEBEDROOM", 587, 201, 685, 204),
        line("8.", 580, 252, 583, 256),
        line("RECESSEDDRYERVENTBOX.", 587, 252, 628, 256),
    ]
    layout = PageLayout(792, 612, 0, lines, [], [], None, text_source="ocr")
    notes = parse_flag_notes(layout)

    assert 2 in notes.notes and 8 in notes.notes
    assert registry.NOTE_RULES[0].pattern.search(notes.notes[2])
    assert registry.NOTE_RULES[-1].pattern.search(notes.notes[8])


def test_a_two_digit_flag_split_into_two_tokens_is_one_flag_not_two(tmp_path):
    from ductwork_sidecar.pdf_layout import Box, PageLayout, TextWord
    from ductwork_sidecar.notes import FlagNotes
    from ductwork_sidecar.residential import _count_flags

    tri = Box(100, 100, 130, 130)
    words = [TextWord("1", Box(108, 112, 112, 120)), TextWord("1", Box(114, 112, 118, 120))]
    layout = PageLayout(792, 612, 0, [], words, [tri], None)
    boxes, diagnostics = _count_flags(layout, Box(0, 0, 792, 612), FlagNotes({}, None, None))

    assert list(boxes) == [11] and len(boxes[11]) == 1
    assert diagnostics["ambiguous_flag_triangles"] == 0


def test_three_digits_in_one_triangle_are_not_a_flag():
    from ductwork_sidecar.pdf_layout import Box, PageLayout, TextWord
    from ductwork_sidecar.notes import FlagNotes
    from ductwork_sidecar.residential import _count_flags

    tri = Box(100, 100, 130, 130)
    words = [TextWord(d, Box(104 + 6 * i, 112, 108 + 6 * i, 120)) for i, d in enumerate("123")]
    boxes, diagnostics = _count_flags(PageLayout(792, 612, 0, [], words, [tri], None), Box(0, 0, 792, 612), FlagNotes({}, None, None))

    assert boxes == {} and diagnostics["ambiguous_flag_triangles"] == 1


def test_detections_from_ocr_are_trusted_less_than_the_same_detections_from_a_text_layer(tmp_path):
    layout, from_text = run(tmp_path, standard_sheet())
    ocr_layout = PageLayoutCopy(layout)
    from_ocr = detect(ocr_layout, sheet_ref="s1")

    assert [d["type"] for d in from_ocr.detections] == [d["type"] for d in from_text.detections]
    for a, b in zip(from_text.detections, from_ocr.detections):
        assert b["confidence"] < a["confidence"]


def PageLayoutCopy(layout):
    import dataclasses
    return dataclasses.replace(layout, text_source="ocr")
