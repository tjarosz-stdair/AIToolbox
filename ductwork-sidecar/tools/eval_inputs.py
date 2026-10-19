"""Makes the inputs for an evaluation run: a gold file (what is right) and result manifests (what a detector says), from files on disk.

The evaluation CLI then stores the gold set and records the run (``evalcli gold-import`` and ``evalcli record``), which is what lets an
administrator enable a task type for a detector. Everything here takes paths, because no customer data lives in this repository.

    python tools/eval_inputs.py sheets      PLAN.pdf EVIDENCE.json OUT_DIR
    python tools/eval_inputs.py schedules   PLAN.pdf EVIDENCE.json OUT_DIR
    python tools/eval_inputs.py residential PLANS_DIR WORKSHEET.csv MAP.json OUT_DIR

Each writes ``OUT_DIR/gold.json`` and ``OUT_DIR/pred/*.json``. Where the "gold" is the audited output of the earlier engine, not a
person's count, the notes say so and the run is recorded as a regression, not as a measure of truth.
"""

from __future__ import annotations

import csv
import json
import os
import re
import subprocess
import sys
import uuid

CONTRACT_VERSION = "0.1.0"


def norm(text: str | None) -> str:
    return " ".join((text or "").upper().split())


def write(path: str, value) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(value, f, indent=2)


def manifest(task_type: str, producer: dict, detections: list[dict]) -> dict:
    return {"contractVersion": CONTRACT_VERSION, "taskType": task_type, "taskPublicId": str(uuid.uuid4()), "producer": producer, "detections": detections, "findings": []}


# ---------------------------------------------------------------- sheet identification


_KIND = {"legend": "Legend", "plan": "Plan", "riser": "Riser", "detail": "Detail", "schedule": "Schedule", "notes": "Notes", "other": "Other"}
_CONFIDENCE = {"title-block": 0.95, "bottom-band": 0.8, "page": 0.6, "ocr": 0.6, "none": 0.2}


def sheets(pdf: str, evidence: str, out: str) -> None:
    from ductwork_sidecar.pdf_layout import load_sheet_facts
    from ductwork_sidecar.sheet_id import TASK_TYPE, identify, producer

    truth = json.load(open(evidence, encoding="utf-8"))["pages"]
    gold = []
    for t in truth:
        gold.append({"type": "Sheet", "size": f"{t['sheet']}|{_KIND.get(t['kind'], 'Other')}|Mechanical", "quantity": 1})
        gold.append({"type": "Sheet title", "size": f"{t['sheet']}|{norm(t['title'])}", "quantity": 1})

    detections = []
    for facts in load_sheet_facts(pdf):
        i = identify(facts)
        number = i.sheet_number or "?"
        conf = _CONFIDENCE.get(i.confidence, 0.5)
        for kind, size in (("Sheet", f"{number}|{i.kind}|{i.discipline or '?'}"), ("Sheet title", f"{number}|{norm(i.title)}")):
            detections.append({
                "detectionId": f"p{facts.page_no}-{kind.split()[-1].lower()}", "type": kind, "size": size, "quantity": 1, "method": "Text", "family": "ScheduleNote",
                "confidence": conf, "evidence": f"page {facts.page_no}"})
    write(os.path.join(out, "gold.json"), gold)
    write(os.path.join(out, "pred", "sheets.json"), manifest(TASK_TYPE, producer(), detections))
    print(f"{len(truth)} sheets in the reference; {len(gold)} gold items, {len(detections)} predictions.")


# ---------------------------------------------------------------- schedules


def schedules(pdf: str, evidence: str, out: str) -> None:
    from ductwork_sidecar.pdf_layout import load_sheet_facts
    from ductwork_sidecar.schedules import TASK_TYPE, producer, read_schedules

    ref = json.load(open(evidence, encoding="utf-8"))
    sheets_by_page = {p["page"]: p["sheet"] for p in ref["pages"]}
    schedule_pages = [p["page"] for p in ref["pages"] if p["kind"] == "schedule"]

    def detail(mark, maker, model, capacity):
        return f"{mark}|{norm(maker)}|{norm(model)}|{norm(capacity)}"

    gold = []
    for e in ref["equipment"]:
        gold.append({"type": norm(e["schedule"]), "size": e["mark"], "quantity": 1})
        gold.append({"type": "Schedule row", "size": detail(e["mark"], e["mfr"], e["model"], e["capacity"]), "quantity": 1})

    facts = load_sheet_facts(pdf, [p - 1 for p in schedule_pages], text_only=True)
    detections, seen = [], set()
    for f, page in zip(facts, schedule_pages):
        for sched in read_schedules(f, sheets_by_page[page]):
            kind = re.sub(r"\s*\bSCHEDULES?\b\s*", " ", sched.title, flags=re.I).strip() or "Equipment"
            for row in sched.rows:
                if row.mark in seen:
                    continue
                seen.add(row.mark)
                common = {"method": "Schedule", "family": "ScheduleNote", "confidence": 0.9, "sheetViewRef": sched.sheet}
                detections.append({**common, "detectionId": f"m-{row.mark}", "type": norm(kind), "size": row.mark, "quantity": 1})
                detections.append({**common, "detectionId": f"r-{row.mark}", "type": "Schedule row", "size": detail(row.mark, row.manufacturer, row.model, row.capacity), "quantity": 1})
    write(os.path.join(out, "gold.json"), gold)
    write(os.path.join(out, "pred", "schedules.json"), manifest(TASK_TYPE, producer(), detections))
    print(f"{len(ref['equipment'])} scheduled marks in the reference; {len(gold)} gold items, {len(detections)} predictions.")


# ---------------------------------------------------------------- the residential label counter


def residential(plans_dir: str, worksheet: str, map_path: str, out: str) -> None:
    """The map is JSON: {"plans": {"FILE.pdf": {"building": "Willowood", "worksheetPlan": "Whiteland Whiteland Rd - Willowood"}}}."""
    mapping = json.load(open(map_path, encoding="utf-8"))["plans"]
    rows = list(csv.reader(open(worksheet, encoding="utf-8-sig")))
    by_plan: dict[str, list[tuple[str, str, float]]] = {}
    for r in rows[1:]:
        if r and r[0] == "Plans" and r[3].strip():
            by_plan.setdefault(r[1].strip(), []).append((r[2].strip(), r[3].strip(), float(r[5] or 0)))

    def kind_of(category: str, description: str) -> str | None:
        c = category.lower()
        if description.lower() == "dryer boot":
            return "DryerBoot"
        if "boot" in c:
            return "Boot"
        if c == "ef":
            return "Fan"
        if c == "grd":
            return "Grille"
        return None

    gold, skipped = [], []
    for name, entry in mapping.items():
        for category, description, qty in by_plan.get(entry["worksheetPlan"].strip(), []):
            kind = kind_of(category, description)
            if kind is None:
                skipped.append((entry["building"], category, description))
                continue
            gold.append({"buildingType": entry["building"], "type": kind, "size": description, "quantity": qty})

    pred_dir = os.path.join(out, "pred")
    os.makedirs(pred_dir, exist_ok=True)
    count = 0
    for i, (name, entry) in enumerate(mapping.items()):
        result = os.path.join(pred_dir, f"{i:02d}-{entry['building'].replace(' ', '_')}.json")
        subprocess.run([sys.executable, "-B", "-m", "ductwork_sidecar", "residential", os.path.join(plans_dir, name), "--building", entry["building"], "--out", result,
                        "--sheet-ref", entry["building"], "--task-id", str(uuid.uuid4())], check=True, stderr=subprocess.DEVNULL)
        count += len(json.load(open(result, encoding="utf-8"))["detections"])
    write(os.path.join(out, "gold.json"), gold)
    print(f"{len(mapping)} plans; {len(gold)} gold items, {count} predictions; worksheet rows with no category mapping: {skipped}")


def main(argv: list[str]) -> int:
    commands = {"sheets": (sheets, 3), "schedules": (schedules, 3), "residential": (residential, 4)}
    if len(argv) < 2 or argv[1] not in commands or len(argv) - 2 != commands[argv[1]][1]:
        print(__doc__)
        return 1
    commands[argv[1]][0](*argv[2:])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
