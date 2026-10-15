"""Label grammar and flag-note patterns for residential unit plans (detector family C).

This is data, not control flow. Another designer's conventions are added as rows here (and later as
user-approved interpretation rules), not as new code paths. Every row says what it produces and how
sure a clean match of it should be; those confidences are priors that the eval harness calibrates
against gold data, never claims on their own.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Callable

# Types used on the wire. The eval harness scores on (building type, type, size), so the names here
# must be the ones gold labels use.
BOOT = "Boot"
FAN = "Fan"
GRILLE = "Grille"
DRYER_BOOT = "DryerBoot"
LOUVER = "Louver"
TERMINAL_CFM_ONLY = "CfmOnlyTerminal"


@dataclass(frozen=True)
class LabelRule:
    name: str
    pattern: re.Pattern[str]
    type: str
    size: Callable[[re.Match[str]], str | None]
    confidence: float
    two_dimension: bool = False


def _x(a: str, b: str, c: str | None = None) -> str:
    return f"{int(a)}x{int(b)}" + (f"x{int(c)}" if c is not None else "")


LABEL_RULES: tuple[LabelRule, ...] = (
    # Boot or terminal label: WxHxD. The depth is part of the size.
    LabelRule(
        "boot-3d",
        re.compile(r"^(\d{1,2})\s*[xX]\s*(\d{1,2})\s*[xX]\s*(\d{1,2})$"),
        BOOT,
        lambda m: _x(m.group(1), m.group(2), m.group(3)),
        0.93,
    ),
    # Two-dimension label (WxH, no depth). Drawn this way on some sheets while the estimator's
    # worksheet carries a depth. Reported as drawn, with a finding. Whether to assume a depth is the
    # user's decision (an interpretation rule), never the detector's.
    LabelRule(
        "boot-2d",
        re.compile(r"^(\d{1,2})\s*[xX]\s*(\d{1,2})$"),
        BOOT,
        lambda m: _x(m.group(1), m.group(2)),
        0.7,
        two_dimension=True,
    ),
    LabelRule("fan-numbered", re.compile(r"^EF\s*-\s*(\d+)$", re.I), FAN, lambda m: f"EF-{int(m.group(1))}", 0.95),
    LabelRule("fan-light", re.compile(r"^EF\s*/\s*LIGHT$", re.I), FAN, lambda m: "EF/Light", 0.95),
    LabelRule("fan-plain", re.compile(r"^EF$", re.I), FAN, lambda m: "EF", 0.85),
    LabelRule(
        "return-grille",
        re.compile(r"^(\d{1,2})\s*[xX]\s*(\d{1,2})\s*R\.?\s*A\.?$", re.I),
        GRILLE,
        lambda m: f"{int(m.group(1))}x{int(m.group(2))} RA",
        0.93,
    ),
)

CFM_LINE = re.compile(r"^(\d{2,4})\s*CFM$", re.I)
# Some sheets stack the airflow on two lines: the number above the word CFM.
CFM_NUMBER_LINE = re.compile(r"^\d{2,4}$")
CFM_WORD_LINE = re.compile(r"^CFM$", re.I)


@dataclass(frozen=True)
class NoteRule:
    """A flag note whose symbol on the plan marks a countable item."""

    name: str
    pattern: re.Pattern[str]
    type: str
    size: Callable[[re.Match[str]], str]
    confidence: float


NOTE_RULES: tuple[NoteRule, ...] = (
    NoteRule(
        "pass-through",
        re.compile(r"(\d{1,2})\s*[xX]\s*(\d{1,2})\s+PASS[\s-]*THROUGH", re.I),
        GRILLE,
        lambda m: f"{int(m.group(1))}x{int(m.group(2))} Pass Through",
        0.9,
    ),
    NoteRule(
        "outside-air-louver",
        re.compile(r"OUTSIDE\s+AIR\s+DUCT.*LOUVER", re.I),
        LOUVER,
        lambda m: "Outside Air Louver",
        0.85,
    ),
    NoteRule(
        "dryer-vent-box",
        re.compile(r"DRYER\s+VENT\s+BOX", re.I),
        DRYER_BOOT,
        lambda m: "Dryer Boot",
        0.9,
    ),
)

TITLE_LINE = re.compile(r"^MECH[A-Z.&\s]*PLANS?\s*-\s*(.+)$", re.I)
NOTES_HEADING = re.compile(r"^FLAG\s+NOTES", re.I)
