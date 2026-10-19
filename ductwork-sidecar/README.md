# ductwork-sidecar

Python detectors for Ductwork Takeoff. They run as a subprocess of the C# worker, read a PDF, and
emit a `TaskResultManifest` that conforms to the JSON Schema generated from
`AIToolbox.Ductwork.Contracts` (`../AIToolbox.Ductwork.Contracts/schemas`). C# is the source of truth
for the contract; this package never defines it.

Everything a detector emits is a **proposal** for a person to verify. Detectors never apply
unconfirmed conventions: a boot label with no depth, a CFM-only terminal, or "two grilles per
pass-through flag" is reported as drawn, with a finding, and a rule (resolved by the user) decides.

## Layout

| Module | Role |
|---|---|
| `pdf_layout.py` | The only place that touches the PDF library. Returns text lines, words, triangles and the sheet border in **canonical space** (after `/Rotate`, origin top-left). |
| `registry.py` | Data-driven label grammar and flag-note patterns. New designers' conventions are added here, not in code paths. |
| `ocr_layout.py` | Text-less sheets (outlined text): local OCR on tiles, plus flag digits read from the vector glyph paths inside each triangle. Optional `ocr` extra. Same `PageLayout`, so the same detector runs on it. |
| `notes.py` | Parses a flag-notes column into `{number: text}`. |
| `residential.py` | Family C detector: label and flag-note counting for residential unit plans. |
| `manifest.py` | Builds the contract-shaped manifest. |
| `sheet_id.py` | Says which sheet a page is: number, title, discipline, kind. Works in canonical space, so rotated sheets read like upright ones. |
| `views.py` | Finds the views on a sheet from its scale labels: title, number, scale ratio and area. |
| `sheet_ocr.py` | The same for a text-less page, by reading only its title-block strip with local OCR. Optional `ocr` extra. |

## Commands

`python -m ductwork_sidecar residential <pdf> --page N --out manifest.json` counts a plan sheet. `python -m ductwork_sidecar sheets <pdf> [--ocr] --out pages.json` identifies every page of a file, hashes its drawing area, and finds the views on each mechanical page, in the shape the server's sheet registration takes.

## Tools

`tools/engine_parity.py` runs the wrapped commercial engine on a reference plan set and compares its
summary with an audited reference workbook, line by line (exit code 0 means identical). It is the
regression test for "wrapping the engine did not change its answers". The plan, settings and reference
are passed as paths, because no customer data lives in this repository.

## Licensing

`pymupdf` is an optional dependency because its license (AGPL or Artifex commercial) is an open
company decision. Only `pdf_layout.py` and `ocr_layout.py` import it.

## Tests

```
pip install -e ".[pymupdf,dev]"
pytest
```

The tests build synthetic CAD-style pages (including rotated pages) with known contents and check
the output against the committed contract schema. No customer drawings are used or stored here.
