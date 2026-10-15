"""Command line entry: ``python -m ductwork_sidecar residential <pdf> [--building NAME] [--page N] --out manifest.json``.

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
    args = parser.parse_args(argv)

    layout = load_page_layout(args.pdf, args.page)
    result = detect(layout, building_type=args.building, sheet_ref=args.sheet_ref)
    manifest = build_manifest(result, args.task_id)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print(json.dumps({"detections": len(result.detections), "findings": len(result.findings), **result.diagnostics}), file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
