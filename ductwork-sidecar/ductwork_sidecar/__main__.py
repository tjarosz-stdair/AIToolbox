"""Command line entry: ``python -m ductwork_sidecar residential <pdf> [--building NAME] [--page N] --out manifest.json``
and ``python -m ductwork_sidecar sheets <pdf> [--ocr] --out pages.json``.

The C# worker runs this as a subprocess. It reads the PDF it is given and writes one manifest. It
never touches the network, a database or any path other than the two it is handed.
"""

from __future__ import annotations

import argparse
import json
import sys

from .manifest import build_manifest
from .pdf_layout import load_page_layout
from .residential import detect
from .sheet_id import identify, producer, to_page_report
from .views import default_view_type, find_views, to_report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="ductwork_sidecar")
    sub = parser.add_subparsers(dest="command", required=True)
    res = sub.add_parser("residential", help="count labeled terminals, fans, grilles and flag-note items")
    res.add_argument("pdf")
    res.add_argument("--page", type=int, default=0)
    res.add_argument("--building", default=None, help="building or unit type; read from the title block when omitted")
    res.add_argument("--sheet-ref", default=None)
    res.add_argument("--task-id", default="local")
    res.add_argument("--out", required=True)
    sheets = sub.add_parser("sheets", help="identify every page of a file: sheet number, title, discipline, kind and content hash")
    sheets.add_argument("pdf")
    sheets.add_argument("--ocr", action="store_true", help="read the title block of text-less pages by OCR (slower; needs the ocr extra)")
    sheets.add_argument("--out", required=True)
    sched = sub.add_parser("schedules", help="read the equipment schedules on the pages named in a spec file")
    sched.add_argument("pdf")
    sched.add_argument("--spec", required=True, help='JSON: {"pages": [{"page": 24, "sheet": "M5-00"}]}')
    sched.add_argument("--out", required=True)
    marks = sub.add_parser("marks", help="count how many times each scheduled mark is written on the plan pages named in a spec file")
    marks.add_argument("pdf")
    marks.add_argument("--spec", required=True, help='JSON: {"marks": ["EF-1"], "pages": [{"page": 5, "sheet": "M1-S2"}]}')
    marks.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    if args.command == "sheets":
        return _sheets(args)
    if args.command in ("schedules", "marks"):
        return _schedules(args)

    layout = load_page_layout(args.pdf, args.page)
    result = detect(layout, building_type=args.building, sheet_ref=args.sheet_ref)
    manifest = build_manifest(result, args.task_id)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print(json.dumps({"detections": len(result.detections), "findings": len(result.findings), **result.diagnostics}), file=sys.stderr)
    return 0


def _sheets(args) -> int:
    from .pdf_layout import load_sheet_facts

    engine = None
    pages = []
    for facts in load_sheet_facts(args.pdf):
        identity = identify(facts)
        if not facts.has_text and args.ocr:
            try:
                from .ocr_layout import rapidocr_engine
                from .sheet_ocr import identify_text_less

                engine = engine or rapidocr_engine()
            except ImportError:
                # Without the OCR extra a text-less page stays unidentified, and the page says why, instead of the whole file failing.
                print("OCR is not installed; text-less pages are left unidentified.", file=sys.stderr)
            else:
                identity = identify_text_less(args.pdf, facts.page_no - 1, engine)
        # The views are read from the page's own text, so a page read by OCR has none yet.
        # Only mechanical pages are counted, so the views on an electrical or architectural page are not worth recording.
        counted = identity.discipline in (None, "Mechanical")
        views = to_report(find_views(facts, default_view_type(identity.kind))) if facts.has_text and counted else []
        pages.append(to_page_report(facts, identity, views))

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump({"producer": producer(), "pages": pages}, f, indent=2)
    print(json.dumps({"pages": len(pages), "textLess": sum(1 for p in pages if not p["hasTextLayer"]), "views": sum(len(p["views"]) for p in pages)}), file=sys.stderr)
    return 0

def _schedules(args) -> int:
    from . import schedules as sched
    from .pdf_layout import load_sheet_facts

    with open(args.spec, encoding="utf-8") as f:
        spec = json.load(f)
    pages = spec.get("pages", [])
    # The reader needs only the text, so the line-work (most of the time on a large sheet) is not read.
    facts = load_sheet_facts(args.pdf, [p["page"] - 1 for p in pages], text_only=True)

    if args.command == "schedules":
        found = [sched.schedule_to_report(s) for fact, p in zip(facts, pages) for s in sched.read_schedules(fact, p["sheet"])]
        result = {"producer": sched.producer(), "schedules": found}
        note = {"schedules": len(found), "rows": sum(len(s["rows"]) for s in found)}
    else:
        marks = [m for m in spec.get("marks", []) if isinstance(m, str) and m]
        counts = [
            {"mark": c.mark, "sheet": c.sheet, "page": c.page_no, "count": c.count, "box": list(c.box)}
            for fact, p in zip(facts, pages) for c in sched.count_marks(fact, p["sheet"], marks)
        ]
        result = {"producer": sched.producer(), "counts": counts}
        note = {"marksFound": len({c["mark"] for c in counts})}

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
    print(json.dumps(note), file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
