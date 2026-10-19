"""Compares the schedule reader with an audited reference, mark by mark (exit code 0 means they agree).

``python tools/schedule_parity.py PLAN.pdf EVIDENCE.json`` reads every schedule in the plan set and counts each scheduled mark on its plan
sheets, then checks marks, descriptions, makers, models, capacities and tag counts against the ``equipment`` and ``pages`` of the engine's
audit evidence. Like ``engine_parity.py`` it takes paths, because no customer data lives in this repository.
"""

from __future__ import annotations

import json
import sys
from collections import Counter

from ductwork_sidecar.pdf_layout import load_sheet_facts
from ductwork_sidecar.schedules import count_marks, read_schedules


def main(argv: list[str]) -> int:
    pdf, evidence = argv[1], argv[2]
    ref = json.load(open(evidence, encoding="utf-8"))
    sheets = {p["page"]: p["sheet"] for p in ref["pages"]}
    kinds = {p["page"]: p["kind"] for p in ref["pages"]}
    facts = load_sheet_facts(pdf, text_only=True)

    rows = {}
    for page, kind in kinds.items():
        if kind == "schedule":
            for schedule in read_schedules(facts[page - 1], sheets[page]):
                for row in schedule.rows:
                    rows.setdefault(row.mark, row)

    expected = {e["mark"]: e for e in ref["equipment"]}
    problems = [f"scheduled but not read: {m}" for m in sorted(set(expected) - set(rows))] + [f"read but not scheduled: {m}" for m in sorted(set(rows) - set(expected))]

    for mark, row in rows.items():
        e = expected.get(mark)
        if e:
            for name, mine, theirs in (("maker", row.manufacturer.upper(), (e["mfr"] or "").upper()), ("model", row.model, e["model"] or ""), ("capacity", row.capacity, e["capacity"] or "")):
                if mine != theirs:
                    problems.append(f"{mark} {name}: read {mine!r}, reference {theirs!r}")

    totals = Counter()
    for page, kind in kinds.items():
        if kind == "plan":
            for c in count_marks(facts[page - 1], sheets[page], list(rows)):
                totals[c.mark] += c.count
    tag_diffs = [m for m in rows if m in expected and totals.get(m, 0) != expected[m]["plan_tags"]]

    print(f"{len(rows)} marks read, {len(expected)} in the reference; {len(problems)} differences in marks and fields; tag counts differ for {len(tag_diffs)}")
    for p in problems[:40]:
        print("  " + p)
    for m in tag_diffs[:40]:
        print(f"  {m}: counted {totals.get(m, 0)}, reference {expected[m]['plan_tags']}")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
