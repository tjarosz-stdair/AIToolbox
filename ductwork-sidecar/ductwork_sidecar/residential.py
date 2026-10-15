"""Family C: label and flag-note counting for residential unit plans (plan section 6.7).

The method, as the sample plans use it:
* terminals are labeled in place, ``4x10x7`` over ``100 CFM``;
* fans and grilles carry short tags (``EF-1``, ``EF/LIGHT``, ``12x24 R.A.``);
* other items are marked by a numbered triangle (a *flag*) that refers to a numbered list on the
  sheet, for example flag 2 is a pass-through grille and flag 8 a dryer vent box.

What the detector does NOT do is apply any convention the drawing does not state. A pass-through
flag is one drawn symbol, so it is reported as one detection; "two grilles per flag" is an
interpretation rule a user approves. A boot with no depth is reported with the size as drawn and a
finding. A CFM with no size is reported as a CFM-only terminal and a finding. Everything is a proposal.

A flag counts only when its digit sits inside a triangle symbol, because room names such as
"BEDROOM 2" contain digits and must never be counted.
"""

from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass, field

from .notes import FlagNotes, parse_flag_notes
from .pdf_layout import Box, PageLayout, TextLine
from .registry import (
    BOOT,
    CFM_LINE,
    CFM_NUMBER_LINE,
    CFM_WORD_LINE,
    LABEL_RULES,
    NOTE_RULES,
    TERMINAL_CFM_ONLY,
    TITLE_LINE,
    LabelRule,
)

FAMILY = "LabelFlagCount"
# A CFM line belongs to the size label above it when it sits just below and roughly centered on it.
_CFM_MAX_DROP = 22.0
_CFM_MAX_OFFSET = 35.0
_PLAN_MARGIN = 10.0
OCR_TRUST = 0.9


@dataclass
class DetectionResult:
    detections: list[dict] = field(default_factory=list)
    findings: list[dict] = field(default_factory=list)
    diagnostics: dict = field(default_factory=dict)


def detect_building_type(layout: PageLayout) -> str | None:
    """The unit or building named in the sheet title, for example the text after ``MECH PLAN -``."""
    found = None
    for line in layout.lines:
        m = TITLE_LINE.match(re.sub(r"\s+", " ", line.text).strip())
        if m:
            found = m.group(1).strip()
    return found


def plan_region(layout: PageLayout, notes: FlagNotes) -> Box:
    """Where plan content lives: inside the sheet border, left of the notes column."""
    border = layout.border or Box(0, 0, layout.width, layout.height)
    x1 = (notes.left - _PLAN_MARGIN) if notes.left is not None else border.x1 - _PLAN_MARGIN
    return Box(border.x0 + _PLAN_MARGIN, border.y0 + _PLAN_MARGIN, min(x1, border.x1 - _PLAN_MARGIN), border.y1 - _PLAN_MARGIN)


def detect(layout: PageLayout, building_type: str | None = None, sheet_ref: str | None = None) -> DetectionResult:
    result = DetectionResult()
    if not layout.has_text:
        # No text layer: labels cannot be read here. The text-less path (OCR, then approved vision)
        # handles this sheet. Say so instead of returning a confident empty answer.
        result.diagnostics["no_text_layer"] = True
        result.findings.append(
            _finding(
                sheet_ref, 0, "MissingEvidence",
                "This sheet has no text layer, so labels cannot be read directly.",
                ["Run local OCR on the sheet", "Request approved hosted vision for the sheet", "Count by hand"],
            )
        )
        return result

    building = building_type or detect_building_type(layout)
    notes = parse_flag_notes(layout)
    region = plan_region(layout, notes)
    lines = _merge_stacked_cfm([l for l in layout.lines if region.contains_point(l.box.cx, l.box.cy)])

    ids = _IdSource(sheet_ref or "p1")
    # Text read by OCR can be misread, so everything derived from it is trusted a little less.
    trust = 1.0 if layout.text_source == "text-layer" else OCR_TRUST
    sized, cfm_lines, other = _classify_lines(lines)
    pairs = _pair_cfm(sized, cfm_lines)
    paired_cfm = {id(c) for c in pairs.values() if c is not None}

    sizes_on_page = Counter(rule_size for _, _, rule_size in sized if rule_size)

    for line, rule, size in sized:
        cfm = pairs.get(id(line))
        confidence = rule.confidence * trust
        evidence = f"label '{line.text}'"
        if cfm is not None:
            evidence += f" with {cfm.text}"
            confidence = min(0.99, confidence + 0.04 * trust)
        det_id = ids.next()
        result.detections.append(
            _detection(det_id, rule.type, size, rule.name, "Text", confidence, building, sheet_ref, line.box, evidence)
        )
        if rule.two_dimension:
            result.findings.append(
                _finding(
                    sheet_ref, ids.finding(), "GrammarVariant",
                    f"Label '{line.text}' has no depth. The drawing does not say which depth applies.",
                    [f"Treat as {size}x<depth> (set a rule for the depth)", f"Keep as drawn ({size})"],
                    det_id, evidence,
                )
            )

    for cfm in cfm_lines:
        if id(cfm) in paired_cfm:
            continue
        value = CFM_LINE.match(cfm.text).group(1)
        det_id = ids.next()
        result.detections.append(
            _detection(det_id, TERMINAL_CFM_ONLY, None, "cfm-only", "Text", 0.3 * trust, building, sheet_ref, cfm.box,
                       f"'{cfm.text}' has no size label", tag=f"{int(value)} CFM")
        )
        result.findings.append(
            _finding(
                sheet_ref, ids.finding(), "AmbiguousLabel",
                f"A terminal is labeled only '{cfm.text}', with no size.",
                _cfm_candidates(sized, value),
                det_id, f"label '{cfm.text}'",
            )
        )

    flags, flag_diagnostics = _count_flags(layout, region, notes)
    result.diagnostics.update(flag_diagnostics)
    for number, boxes in sorted(flags.items()):
        text = notes.notes.get(number)
        rule = next((r for r in NOTE_RULES if text and r.pattern.search(text)), None)
        if rule is None:
            result.diagnostics.setdefault("unmapped_flags", {})[number] = len(boxes)
            continue
        size = rule.size(rule.pattern.search(text))
        for box in boxes:
            result.detections.append(
                _detection(ids.next(), rule.type, size, rule.name, "FlagNote", rule.confidence * trust, building, sheet_ref, box,
                           f"flag {number}: {text[:80]}")
            )

    result.diagnostics.update(
        building_type=building,
        notes_parsed=len(notes.notes),
        labels=len(sized),
        cfm_only=len(cfm_lines) - len(paired_cfm),
        sizes_on_page=dict(sizes_on_page),
    )
    return result


# ---- internals

class _IdSource:
    def __init__(self, prefix: str):
        self._prefix = prefix
        self._n = 0
        self._f = 0

    def next(self) -> str:
        self._n += 1
        return f"{self._prefix}-d{self._n}"

    def finding(self) -> int:
        self._f += 1
        return self._f


def _merge_stacked_cfm(lines: list[TextLine]) -> list[TextLine]:
    """Joins an airflow written on two lines (``100`` above ``CFM``) into one ``100 CFM`` line.

    Without this, such a terminal is not recognised at all and silently disappears from the count. Only
    a bare number sitting directly above a bare ``CFM`` is joined, and each line is used once.
    """
    numbers = [l for l in lines if CFM_NUMBER_LINE.match(l.text.strip())]
    words = [l for l in lines if CFM_WORD_LINE.match(l.text.strip())]
    consumed: set[int] = set()
    merged: list[TextLine] = []
    for word in words:
        best = None
        for num in numbers:
            if id(num) in consumed:
                continue
            gap = word.box.y0 - num.box.y1
            if -2 <= gap <= 8 and abs(word.box.cx - num.box.cx) <= 15:
                if best is None or gap < best[0]:
                    best = (gap, num)
        if best is not None:
            num = best[1]
            consumed.add(id(num))
            consumed.add(id(word))
            box = Box(min(num.box.x0, word.box.x0), num.box.y0, max(num.box.x1, word.box.x1), word.box.y1)
            merged.append(TextLine(f"{num.text.strip()} CFM", box))
    return [l for l in lines if id(l) not in consumed] + merged


def _classify_lines(lines: list[TextLine]):
    sized: list[tuple[TextLine, LabelRule, str]] = []
    cfm: list[TextLine] = []
    other: list[TextLine] = []
    for line in lines:
        text = re.sub(r"\s+", " ", line.text).strip()
        if CFM_LINE.match(text):
            cfm.append(line)
            continue
        for rule in LABEL_RULES:
            m = rule.pattern.match(text)
            if m:
                size = rule.size(m)
                if size is not None:
                    sized.append((line, rule, size))
                break
        else:
            other.append(line)
    return sized, cfm, other


def _pair_cfm(sized, cfm_lines: list[TextLine]) -> dict[int, TextLine | None]:
    """Pairs each sized terminal label with the CFM line just below it. Each CFM is used once."""
    pairs: dict[int, TextLine | None] = {}
    used: set[int] = set()
    # Closest pairs first, so a crowded plan does not let a far label steal a nearer one's CFM.
    candidates = []
    for line, _rule, _size in sized:
        for cfm in cfm_lines:
            drop = cfm.box.y0 - line.box.y1
            offset = abs(cfm.box.cx - line.box.cx)
            if -4 <= drop <= _CFM_MAX_DROP and offset <= _CFM_MAX_OFFSET:
                candidates.append((drop + offset, id(line), line, cfm))
    for _d, line_id, line, cfm in sorted(candidates, key=lambda c: c[0]):
        if line_id in pairs or id(cfm) in used:
            continue
        pairs[line_id] = cfm
        used.add(id(cfm))
    for line, _r, _s in sized:
        pairs.setdefault(id(line), None)
    return pairs


def _count_flags(layout: PageLayout, region: Box, notes: FlagNotes):
    """Flag numbers found inside triangle symbols within the plan region."""
    triangles = [t for t in layout.triangles if region.contains_point(t.cx, t.cy)]
    boxes: dict[int, list[Box]] = {}
    ambiguous = 0
    for tri in triangles:
        # One flag per triangle. A two-digit flag can arrive as two tokens, so tokens inside one
        # triangle are read left to right and joined, and anything longer than two digits is not a flag.
        digits = sorted(
            (w for w in layout.words
             if re.fullmatch(r"\d{1,2}", w.text) and tri.contains_point(w.box.cx, w.box.cy, margin=3)),
            key=lambda w: w.box.x0,
        )
        if not digits:
            continue
        text = "".join(w.text for w in digits)
        if len(text) > 2:
            ambiguous += 1
            continue
        boxes.setdefault(int(text), []).append(tri)
    digits_outside = sum(
        1 for w in layout.words
        if re.fullmatch(r"\d{1,2}", w.text) and region.contains_point(w.box.cx, w.box.cy)
        and not any(t.contains_point(w.box.cx, w.box.cy, margin=3) for t in triangles)
    )
    return boxes, {"triangles": len(triangles), "digits_outside_triangles": digits_outside, "ambiguous_flag_triangles": ambiguous}


def _cfm_candidates(sized, cfm_value: str) -> list[str]:
    """Terminal sizes that appear elsewhere on the sheet, most frequent first. Fans and grilles are not terminals."""
    counts = Counter(size for _line, rule, size in sized if rule.type == BOOT)
    return [size for size, _n in counts.most_common()]


def _detection(det_id, type_, size, method_name, method, confidence, building, sheet_ref, box: Box, evidence, tag=None) -> dict:
    d = {
        "detectionId": det_id,
        "type": type_,
        "quantity": 1,
        "unit": "EA",
        "method": method,
        "family": FAMILY,
        "confidence": round(confidence, 3),
        "box": box.to_contract(),
        "evidence": f"{method_name}: {evidence}",
    }
    if size is not None:
        d["size"] = size
    if tag is not None:
        d["tag"] = tag
    if building:
        d["buildingType"] = building
    if sheet_ref:
        d["sheetViewRef"] = sheet_ref
    return d


def _finding(sheet_ref, n, kind, question, candidates, detection_id=None, evidence=None) -> dict:
    f = {
        "findingId": f"{sheet_ref or 'p1'}-f{n}",
        "kind": kind,
        "question": question,
        "candidates": candidates,
    }
    if detection_id:
        f["detectionId"] = detection_id
    if evidence:
        f["evidence"] = evidence
    return f
