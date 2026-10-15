"""Parses a flag-notes column ("FLAG NOTES - MECHANICAL: 1. ... 2. ...") into {number: text}."""

from __future__ import annotations

import re
from dataclasses import dataclass

from .pdf_layout import Box, PageLayout, TextLine
from .registry import NOTES_HEADING

_NUMBER_ALONE = re.compile(r"^(\d{1,2})\.$")
_NUMBER_WITH_TEXT = re.compile(r"^(\d{1,2})\.\s+(\S.*)$")
# How far below the heading the column can run, as a fraction of the sheet height.
_COLUMN_SPAN = 0.45


@dataclass(frozen=True)
class FlagNotes:
    notes: dict[int, str]
    # Left edge of the notes column (the numbers), or None when the sheet has no notes list.
    left: float | None
    heading: Box | None


def parse_flag_notes(layout: PageLayout) -> FlagNotes:
    heading = next((l for l in layout.lines if NOTES_HEADING.search(l.text)), None)
    if heading is None:
        return FlagNotes({}, None, None)

    top = heading.box.y1
    bottom = top + layout.height * _COLUMN_SPAN
    # The numbers sit to the left of the heading's start; the text runs to its right.
    candidates = [
        l for l in layout.lines
        if top - 1 <= l.box.y0 <= bottom and heading.box.x0 - 110 <= l.box.x0 <= heading.box.x1
    ]

    numbers: list[tuple[int, TextLine, str | None]] = []
    for line in candidates:
        m = _NUMBER_ALONE.match(line.text)
        if m:
            numbers.append((int(m.group(1)), line, None))
            continue
        m = _NUMBER_WITH_TEXT.match(line.text)
        if m and line.box.x0 <= heading.box.x0 - 30:
            numbers.append((int(m.group(1)), line, m.group(2)))

    numbers.sort(key=lambda n: n[1].box.y0)
    notes: dict[int, str] = {}
    for i, (number, number_line, inline_text) in enumerate(numbers):
        y_start = number_line.box.y0 - 3
        y_end = numbers[i + 1][1].box.y0 - 3 if i + 1 < len(numbers) else bottom
        parts = [inline_text] if inline_text else []
        for line in candidates:
            if line is number_line:
                continue
            if y_start <= line.box.y0 < y_end and line.box.x0 >= number_line.box.x1 - 2:
                if not _NUMBER_ALONE.match(line.text):
                    parts.append(line.text)
        text = " ".join(parts).strip()
        if text:
            notes.setdefault(number, text.upper())

    left = min((n[1].box.x0 for n in numbers), default=heading.box.x0)
    return FlagNotes(notes, left, heading.box)
