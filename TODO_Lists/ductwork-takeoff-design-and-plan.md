# Ductwork Takeoff: design and implementation plan

**Status:** design agreed, nothing built. Sample drawings have arrived and were surveyed (section 4). Phase 0 implementation starts only on explicit go-ahead.
**Basis:** a read of `AIToolbox.Web` (net10.0) and its tests, the publish profiles, `P21Toolbox/FileStorage`, and the `RingCentralProjects` repo for worker precedent. Statements come from code unless marked **[unverified]**. Section 4 adds a read-only survey of the sample drawings and baseline runs of the existing takeoff engine.

---

## 1. Purpose and scope

Ductwork Takeoff lets an estimator upload an HVAC mechanical drawing set (PDF, possibly multi-floor, with plan, section and riser views) and obtain a verified, floor-by-floor and job-total list of ductwork components: duct, takeoffs, transitions, elbows, tees, plenums, dampers, registers/grilles/diffusers and equipment. "Takeoff" is the industry term for identifying all the ductwork on a plan and producing the report of required material, so it names the tool. The reference product is withrebar.ai, which separates takeoff from bid management and market intelligence.

The AI produces **proposals**. A human verifies them. This is an estimating workbench with an AI proposal engine inside it, not a fire-and-forget extractor, because a missed fitting is a bid error. Verification features are therefore core.

**Naming.** Tool: **Ductwork Takeoff** (ToolKey `DuctworkTakeoff`). `ductwork` is the domain prefix (SQL schema, projects, Job entity) so that future sibling tools such as bid management can share Jobs, storage and audit.

**Vocabulary.**
- **Ductwork Job:** one **development**, the whole job a contractor bids, which may contain several buildings. Each Job is its own entity with a lifecycle, edit lock, membership, tags and audit log.
- **Building type:** a group of identical buildings or units inside a Job (for example one unit type, a leasing office, extended garages), counted once and multiplied. Its **multiplier** is the number of identical buildings or units, confirmed by the user.
- **Processing Task:** a workflow step run by the worker for a Job (ingest, rasterize, extract, classify, evaluate, report, archive). Tasks belong to a Job but are not entities.
- **Instance / tenant:** each P21Toolbox tenant that uses AI tools has its own AIToolbox instance (own IIS site and SQL database). Koch Air and Standard Air share one instance because Koch Air is the parent company; each points its own P21Toolbox instance at it. Other tenants never share an instance.

**Workflow:** Setup, Process, Review/Refine, Report.
- *Setup:* sheet and view setup (floor, view type, discipline, include/exclude regions), scale calibration per view (two points plus a distance, cross-checked against the title block), building types with confirmed multipliers, symbol-by-example, and governing notes proposed as rules.
- *Review/Refine:* viewer with colored per-type highlights, a tree and overlay sharing one selection keyed by entity id, edit tools, a confidence-sorted review queue, per-sheet sign-off, a red overlay of unclaimed duct geometry, and schedule-versus-detected reconciliation.
- *Report:* the **count sheet**: a building-type breakdown first, then a multiplier column and an extended count (the job total), with a holds list, rules applied and assumptions/exclusions/RFI flags, as Excel and PDF. The tool produces no quotes or pricing: the user keys the count sheet into P21.

---

## 2. Architecture principles and why

1. **Hybrid pipeline, not "send the drawing to an LLM".** Vision LLMs read well (sheet classification, schedules, legends, labeling a cropped symbol) but are unreliable at exact coordinates and exhaustive counting. So: prefer **vector extraction** from CAD-exported PDFs (paths, text, layers) over pixel detection; use geometric reconstruction and symbol matching (vector hash first, template/embedding fallback) for localization; use the LLM only on **crops and tables**.
2. **Rules are deterministic.** Governing notes such as "damper at every duct takeoff" are proposed by an LLM in a constrained schema, reviewed and approved by a user, and applied by a deterministic engine. The LLM never touches counts. This is enforced by a type boundary: only an approved `Rule` row can feed the evaluator.
3. **Heavy work runs out of process.** The web app owns UI, workflow and data. A separate worker does rasterization, vector extraction, CV and inference. The web app has no CV or PDF-processing dependencies and does no long-running work inside IIS.
4. **Provenance is structural.** Every entity and every processing task records who or what produced it, enforced by non-nullable columns, CHECK constraints and typed contracts, not by convention.
5. **Data model first, eval before detection.** Precision/recall per fitting type against a hand-labeled gold set runs as a task, tagged with its producer. No directive change or model swap is activated without a recorded eval run.
6. **Every user correction is logged** (accept/reject/reclassify with crop and context) to grow the gold set.
7. **Invariants live in types, schema and tests**, not comments. Geometry code is tested with synthetic CAD-style PDF fixtures with known ground truth, watching y-axis flips, rotation, scale and tolerance.
8. **The C#/Python boundary is narrow:** JSON contract only, no shared logic. A Python sidecar is acceptable only beneath a C# host.
9. **Detector families, not one detector.** Commercial duct plans, residential unit plans and text-less pages need different methods. Each is a detector behind the same contract, with its own producer and measured accuracy (section 6.7).

---

## 3. What the existing AIToolbox code gives us, and what it forces

| Area | Finding | Consequence |
|---|---|---|
| **Runtime** | net10.0 (not net48). | Any current .NET library is usable. Remaining library risk is licensing, not target framework. |
| **Jobs** | `dbo.ToolJobs` is flat (no parent, progress, attempts, lease, provider). Execution is an in-memory `Channel<int>` drained by a `BackgroundService` inside the IIS process. Restart recovery re-enqueues all "active" jobs, which is not lease-safe across an overlapped IIS recycle. `JobsController` is hard-coded to ProductParser. `AiBatchJobs` is the only parent/child structure but models provider batch APIs, not general work. | Nothing supports parent/child, progress, retry, or out-of-process execution. We build a separate processing-task table with leases. We reuse only the user-facing Jobs menu and the SignalR pattern. |
| **Directives** | `ToolDirectives.Version` is a hand-edited int and edits overwrite in place. Immutable history exists only as content-hashed `ToolDirectiveSnapshots` created at submission. `ToolJobs.ToolDirectiveSnapshotId` is nullable. The snapshot hash function is private to `ProductParsingService`. `TargetSchemaJson` is an example object pasted into the prompt, not a JSON Schema. Responses are only `JsonDocument.Parse`d. Several active directives per tool are allowed with undefined ordering. | Provenance is by convention today. Ductwork pins immutable snapshots, validates responses against a real schema (and uses provider structured output where available), and shares one hash function. |
| **Model definitions** | `AiProviders` rows (type, endpoint, model, key, headers, max tokens, timeout) are **mutable in place**: editing `ModelName` rewrites the apparent model of historical runs. No capability flags. **No image input**: `SendDocumentPromptAsync` sends PDFs only (`input_file` / Anthropic `document`), so a PNG crop would be sent as a `document` block. `AiChatClient` maps only text and function-call content. One active provider per `ToolKey`. | We add an immutable `ProviderSnapshot`, capability flags, an image input path, and a per-task binding table. |
| **"Tools"** | A *tool* is a feature-registry row (`ToolDefinitions`, allow-list, one provider, scope `tool:{ToolKey}`), not an LLM function tool. Registering one takes four hand edits (seed SQL, an authorization policy in `Program.cs`, the Home card switch, the per-tool log sink). LLM function tools exist only in the PICSearch agent loop, which consumes P21 Toolbox's MCP server through `ModelContextProtocol.Core`; AIToolbox cannot host an MCP server today. | A worker has no tool mechanism to reuse and does not need one. Hosting v2 takeoff MCP tools is deferred. |
| **Storage and upload** | `FileStorageService` is PDF-only and buffers the whole upload in a `MemoryStream`. `Storage:MaxUploadBytes` is 15 MiB. `[RequestSizeLimit]` is 50 MiB on ProductParser and 100 MiB on batches. The app runs under the `/aicompare` IIS path base. IIS `maxAllowedContentLength` is now 100,000,000 bytes and the YARP route was set to match. Static assets (`MapStaticAssets`) are unauthenticated. | 50-100 MB plan sets need chunked upload straight to disk with hash verification, and tiles must be served through an authenticated controller. All URLs must be path-base aware (`Url.Content("~/...")`). |
| **Auth and tenancy** | M365 OIDC cookie; a policy scheme also accepts hashed, scoped, revocable API keys (`ApiConsumers`) and Entra JWTs. `TenantId` is stored on jobs but queries filter by `UserEmail`/`IsPublic`, not tenant. Only an `IsPublic` bool exists for sharing. Precedent for a Windows-service client using an API key: `RingCXAgentHubAPI`; `RingCXStreamListener` is a .NET Windows service. SignalR is wired (`CallMonitorHub`). | The instance is the tenant boundary. Worker authentication reuses `ApiConsumers`. Ductwork needs real per-job membership. |
| **UI conventions** | One flat navbar, tools as cards on Home. No Areas, no bundler, no npm; `wwwroot/lib` has only bootstrap and jQuery; JS is hand-written vanilla. `FrameGuardMiddleware` sends `X-Frame-Options: DENY` everywhere except `/embed`, so **a same-origin iframe panel is blocked**. | The viewer is a normal Razor page with vendored Leaflet, no iframe. |
| **Persistence** | Hand-written ADO.NET repositories. Schema is one ~1,200-line idempotent T-SQL script run at startup. No EF, no migration framework. xUnit test project references the web project. | A ~25-table module does not belong in that script (section 5.2). |

---

## 4. What the sample jobs show, and what we start with

The sample folder holds four sources. This section records what was surveyed and measured so the design rests on evidence. All measurements were read-only. Nothing in the user's folder was modified.

### 4.1 The sample set
| Source | Type | Contents | Ground truth available |
|---|---|---|---|
| **Sample A** | Commercial multifamily, 27 mechanical sheets, 42 x 30 in | Source PDF, plus the full output of the existing engine and its audit (workbooks, markup PDF, completeness audit, validation logs, project config) | Engine output only. Its own audit says these are geometry estimates, not verified counts (20 priority holds, 261 review candidates). Useful as a **regression baseline**, not as truth |
| **Sample B** | Commercial renovation, 7 sheets, 42 x 30 in | Plans, the estimator's marked-up PDF, a counts workbook and CSV (Plan Group, Plan, Stage, Part#, Quantity, Multiplier), a P21-generated quote PDF | Human counts. **Reserved as the holdout** and never used for tuning |
| **Sample C** (one job, renamed mid-job) | Residential development: several buildings, about seven unit types, a leasing office, a model home, extended garages | A bound set of 12 letter-size pages, 30 single-page 36 x 24 in plan files pre-split by the estimator per building (with A4 legend extracts), and two quantity worksheets (original and updated) | Human counts per unit type with a multiplier column (for example 26 Type 4, 30 garages, 83 garages). The **development set** |
| **Handoff package** | Documentation and code | The Python engine (`hvac_takeoff`), a saved "skill" with purchasing rules, technical gates and an intake template, a reuse manual, and an audit | Rules and constraints written by the estimator's earlier workflow |

### 4.2 Format findings
- All four are **CAD-exported vector PDFs**. None is a scan. There are no DXF or DWG files, so **PDF-only for v1 is confirmed**.
- **Live text:** Sample A, Sample B and the Sample C plan files have extractable text. The Sample C bound set has **zero extractable text**: its labels were converted to outlines, so the shapes exist but nothing can be read as text. Counting there needs OCR or a vision model on rendered crops.
- **Scale of pages:** page sizes include 42 x 30 in, 36 x 24 in and letter. Heavy pages reach roughly 150,000 to 400,000 path objects (Sample A). Sample C plan pages are light (about 2,500 paths).
- **Sets are multi-page mixtures** of legends, general notes, schedules, details, and floor or unit plans. Several plans can share one sheet at different scales (Sample B page 3 has three views).
- **Rotated title blocks** exist (Sample C), and electrical sheets can sit next to mechanical ones in the same file.
- **Duplicates:** the same unit-plan sheet appears in more than one building file (the Type 1 sheet is in two), and the same title and general-notes sheet repeats in every building file. Sample A's handoff also records that a later upload was byte-identical to the first. Content hashing at file and sheet level is required.
- **Some files carry baked-in markup.** Eight of the 20 Sample C plan files contain colored paths in the page content (80 to 100 per file): green diamond markers (34 to 45 per file, each paired with a cyan shape), purple polylines tracing flex routes (5 to 9), and a few pink or yellow lines. The other Sample C files are clean copies of the same sheets. The Sample B markup is also baked into the page content, with handwriting as its only structured annotation. Ingest must detect markup and keep it separate from drawing content. Markers might also be a source of item-level labels, but what each marker means is not established (a marked Type 2 plan has 39 markers against 17 pieces per unit in the worksheet).
- Not examined: the Sample A title block. The 638 image objects reported on every Sample B page are two small logo images.

### 4.3 The existing engine and the baseline results
The estimator's earlier workflow produced a working Python package, `hvac_takeoff` (version 1.1-audit-2026-10-12, PyMuPDF, numpy, scipy). It has about twenty modules covering sheet and view identification, schedules, keynotes, duct runs, the duct network, fitting estimates, procurement rules, insulation and simplified construction assumptions, and Excel/markup output, plus 120 tests and a local Flask interface.

Baseline runs, in an isolated virtual environment, with the engine's cache off:

| Run | Result |
|---|---|
| **Sample A** (the engine's home turf) | **Reproduces the audited totals exactly:** rectangular plus round duct 25,724.74 LF, flex 12,372.38 LF, fittings 1,428 EA, air devices 1,110 EA. About 48 seconds for 27 pages, producing 1,119 take-off lines and 17 review items. The engine is deterministic and installs cleanly on current PyMuPDF (1.28) |
| **Sample C, unchanged** | **Zero take-off lines.** It never recognized a plan sheet, because its title-block parser accepts only upright text and these sheets rotate the title |
| **Sample C, with an 8-line patch** to title parsing (scratch copy only) | 18 of 20 plan files identified correctly. Two still fail (a fan tag mistaken for a sheet number, and a project name taken as a title). After that the engine produced only 2 take-off lines on the Type 4 sheet (a pass-through louver and two 4 in connection callouts) against seven categories in the worksheet |
| **Residential label and flag counter** (first a throwaway prototype, now the committed `ductwork-sidecar` detector) | The prototype matched 68 of 77 categories (88%) on 11 unit-plan files. **Correction recorded earlier:** a first version counted flag digits as text and matched pass-throughs by coincidence (room names BEDROOM 2 and BATH 2), so only digits inside a triangle symbol count. The built detector is measured in section 4.7 on counts per building type: 91.9% precision and 81.9% recall as drawn, 98.6% and 99.3% once the estimator's conventions are applied as user rules |
| **Local OCR on the text-less bound set** (RapidOCR, tiled, plus flag digits read from vector glyph paths) | Measured on all eight unit-plan pages (about 40 s per page, no hosted model): 84.9% precision and 77.5% recall as drawn, 92.9% and 94.9% with the same user decisions applied (section 4.7). The earlier one-page test read 22 of 23 labels at a 10x render and 6x was much worse. An earlier summary of that test used the wrong sheet as the answer key and is superseded |
| **Vision reading of the same page** (three high-resolution crops of about 1,900 x 1,000 px, read by Claude) | All 8 boot labels with size and CFM, both EF/LIGHT, EF-1 and 12x24 R.A. read correctly. It found exactly 2 flag-2 triangles and 1 flag-8 triangle and did not mistake room numbers for flags. One page, one reader. Provider cost and latency are unmeasured, and it needed high-resolution crops, not the whole page |

Conclusions that shape the design:
1. **The engine is strong on commercial duct geometry and schedules** and is worth reusing as a detector, with exact parity on Sample A as a regression test.
2. **Its sheet identification is brittle** (title-block assumptions) and electrical sheets are treated as plans. We write our own sheet classification and do not reuse it.
3. **Residential unit plans are counting problems, not geometry problems.** The worksheet counts "flex, boot, grill, take off" combinations per unit type. A text-and-flag-note method nearly reproduces it, while the engine measures the wrong thing. This is a separate detector family, not a patch to the geometry pipeline.
4. **Some terminals are labeled with CFM only** (for example `100 CFM`, no size). The handoff names the same weakness in Sample A (794 CFM-only terminal occurrences). Resolving a size needs a rule or a user decision and must stay visible as TBD.
5. **Parts of the engine are hard-coded to Sample A** (fixed ceiling radiation damper logic, notes that name Sample A sheets, fixed gauge tables). These must become data-driven rules and must not be ported as written.
6. **Its state handling must not be reused:** job state is held in memory, the cache is a trusted local pickle, the upload is deleted after processing, and its JSON export is not a restart checkpoint. Uploaded files must never reach a pickle loader.
7. **Disagreement with a worksheet is a review item, not a verdict.** Several unit types in the worksheet carry identical counts, which could be copied counts. Both numbers must be shown to the user.
8. **Text-less pages can be read, but verification stays with the user.** High-resolution OCR reads most labels and a vision model on crops read the one test page fully. Both depend on resolution and tiling, and neither is proven beyond one page. A live-text re-plot, when one exists (it does for this job), is preferred, so ingest detects text-less PDFs and tells the user.
9. **Some items are symbols with no text.** The second EF/LIGHT fan on Type 3 is a hatched square with no label, so label counting alone undercounts and symbol detection is needed.
10. **Several interpretation rules are the estimator's conventions, not drawn facts:** two grilles per pass-through flag, a two-dimension boot label implying a depth of 7, and the boot size of a CFM-only terminal. The tool must raise these as findings for the user to resolve by rule or override, never apply them silently (section 6.11).

### 4.4 Domain requirements the samples add
- **Job = the whole development, with building types and multipliers.** Quantities are counted per building type (or unit type), then multiplied by the number of identical buildings or units. The user needs the **building-type breakdown and counts first**, then a **multiplier column** (number of buildings or units) and an **extended count** column (the job total). Multipliers come from the architectural building list or from the user, not from the mechanical sheets, so they are user-entered with their source recorded. The engine's own notes warn that partial pilots do not establish multipliers. **Zero is a valid confirmed multiplier** (three of the eleven unit types in the updated worksheet have none built). The bound set's index sheet lists the buildings with addresses and square footage but not the unit mix, so the user supplies or confirms it.
- **Scale belongs to a view, not a sheet.** One sheet can hold several views at different scales.
- **Evidence status, procurement disposition and scope are separate facts.** An item can be verified yet factory-furnished, or measured yet excluded. Resolving one must never silently approve another.
- **Holds** (topic, required evidence, owner, state) are first-class, and unknown quantities stay **TBD**, never zero.
- **Edits make derived rows stale.** Editing a duct run changes fittings, supports, insulation and dampers, so dependents are flagged stale instead of being silently kept or recomputed.
- **The deliverable is a count sheet, not a quote.** The Sample B quote PDF is a P21-generated quote and is not our output. The user keys a concise, logical count sheet into P21. A P21-importable file may come later. Waste allowances, pack-length rounding, order quantities and pricing are quote and purchasing logic and are out of v1.
- **Units are never mixed:** EA, LF and SF stay separate and are never summed.
- **Customer part codes** exist (Sample B uses codes like `20.8.48` for trunk duct and suffixes like `ECAP`). They are captured as an optional field now, so a later P21 import file or catalog mapping has something to build on.
- **The estimator's audit supplies a hold taxonomy** (H01 to H20: register boots, fire and radiation damper boots, diagonal fittings, equipment-adjacent fittings, supports, lined connections, rated transfer assemblies, risers, access doors, controls, fan accessories, thermostats and pumps, heater frames, curbs and pads, caps, sealing and firestop, grease and generator exhaust, insulation and gauge, unit multipliers and variants, and unlabeled small symbols) and a quantity-state vocabulary (counted, inferred, factory-furnished, excluded, TBD). Both seed the hold register and disposition field.
- The estimator's standing rules (supplies scope, equipment excluded but its schedules read for accessories, HET and damper handling, boot review fields, separate insulation SF, "do not invent models or counts") are seed content for directives and rules in Phase 4.

### 4.5 Evaluation data and holdout policy
| Set | Role |
|---|---|
| Sample C plans with the worksheet | **Development set** for the residential detector and building-type logic |
| Sample A with the engine output | **Regression baseline** for the wrapped engine (exact parity). Not ground truth |
| Sample B plans with the counts and marked-up PDF | **Holdout.** Not run, tuned or inspected for tuning. Used only at gates, and a gate use spends the holdout, so a new one is needed afterwards |
| Sample C bound set | Development set for the text-less method (OCR or vision), when that work starts |

**Gold labels** are the human-verified correct answer for a drawing: for each fitting, its type, size, building type and location on the sheet. The detectors are scored against them (precision, recall, and how well confidence predicts correctness). The Sample C worksheet gives totals per building type but not locations, so it cannot show what a detector missed or invented, and gold labels fill that gap. Until the viewer exists they are made with a lightweight script and reviewed by the estimator, and from Phase 1 in the viewer. They are stored apart from the drawings and never committed to the repository, since they describe customer drawings.

There are no pass/fail accuracy thresholds. The estimator does not have a number, and inventing one would be false precision. Phase 0 measures and reports; the **review level** (section 6.13) is the configurable control that decides how much the user is shown, and its cutoffs are derived from the measured calibration.

### 4.6 What we start with
- A deterministic, tested commercial engine to wrap as `Algorithm hvac_takeoff@1.1-audit`, with Sample A parity as a regression test.
- A prototype counting method for residential plans and a worksheet to measure it against.
- The estimator's rules, technical gates and intake template as seed material.
- Measured performance: 42 to 48 s and about 930 MB peak memory for the engine on a 27-page, 16 MB commercial set. The heaviest page renders at 6048 x 4320 in 1.4 s, so tile generation is cheap. These feed worker sizing.
- Text-less methods tested on one page: high-resolution OCR (22 of 23 labels) and a vision reading of crops (complete), both proposals for user verification.
- A reproducible environment recipe: an isolated virtual environment with PyMuPDF 1.28, numpy, scipy, xlsxwriter, pyyaml and openpyxl.
- Known gaps: sheet identification, the residential and text-less methods, symbol-only items, markup separation, building types and multipliers, CFM-only terminals and other ambiguities, the finding, rule and override workflow (section 6.11), data-driven rules, and the PyMuPDF licensing decision.

### 4.7 Phase 0 measurements (development set, counts)

The residential detector (`ductwork-sidecar`, family C) was built from the prototype, run on the 8 distinct unit types of Sample C (138 worksheet items) and scored with the evaluation CLI against the worksheet. These are **development-set numbers**. The user-decision rules were written while looking at this worksheet, so they show what the decision workflow can resolve, not how the detector generalizes to an unseen designer. The holdout was not touched.

| Sheets read from | User decisions applied | Precision | Recall |
|---|---|---|---|
| Live text layer | none, detections as drawn | 91.9% | 81.9% |
| Live text layer | two candidate conventions (two grilles per pass-through flag; a no-depth boot label means depth 7) | 95.0% | 95.7% |
| Live text layer | plus the two estimator decisions for unlabeled terminals in the leasing office | **98.6%** | **99.3%** |
| Text-less bound set, local OCR and vector digit reading, no hosted model | none | 84.9% | 77.5% |
| Text-less bound set, same | the same four decisions | **92.9%** | **94.9%** |

With every decision applied, the live-text run leaves 3 items: the second EF/LIGHT fan on one unit type (a hatched symbol with no label, so a family D problem) and 2 extra pass-through grilles on one unit type (3 flags on the plan against 4 grilles in the worksheet, the discrepancy already noted). The text-less run adds OCR noise: misread sizes, and size labels that OCR missed, which surface as CFM-only items instead of vanishing.

**What the measurement changed (all fixed, with tests):**
- **Silent misses.** Airflow written on two lines (`100` above `CFM`) was not recognized at all, so those terminals disappeared from the count instead of being raised. They are now joined and reported as CFM-only findings. A flag-note item (the outside-air louver) had no rule.
- **Triangle symbols are not always upright.** Flags are right-pointing triangles with the digit inside the base, and sizes must be relative to the sheet (the same plan is a third of the size in the bound set). A flag is counted once per triangle, so a two-digit flag read as two tokens is one flag.
- **Coordinates.** The PDF library reports text and drawings in unrotated space and the page rectangle is already rotated, so rotation is applied in exactly one place and tested at 0, 90, 180 and 270 against a second PDF reader. Drawings inside embedded forms are not reported by the library, which matters for how fixtures are built.
- **Reading digits on a text-less sheet.** OCR of the page image misses most digits inside triangles, and cropping the raster lets the triangle's outline dominate. Re-drawing only the vector paths inside each triangle on a blank page gives a clean digit that the recognizer reads at about 1.0 confidence. This is vector-first extraction applied to outlined text.
- **OCR drops spaces in dense small text** (`14X9PASSTHROUGHLOUVERED...`), so patterns tolerate missing spaces.

**Calibration lessons.** Confidence does separate risky detections from safe ones: on the text-less run, detections at 0.8 to 0.9 were 99.2% correct (106 of them) and the uncertain kinds sat in the low bins. Two caveats shape the review-level design (section 6.13). First, a detection that a **user rule has resolved** (a CFM-only terminal given a size) is correct because the user decided, regardless of its detector confidence, so rule-resolved items must be treated as resolved and not judged by their original confidence. Second, 117 to 126 detections cannot support fine cutoffs (the report says so), and 99% precision in one bin says nothing about a different designer's sheets.

**Engine parity.** `tools/engine_parity.py` runs the engine on Sample A with the audited settings and compares its summary with the audited workbook line by line. 385 lines are identical and 72 display-only callout rows (shown as 0 in the workbook, raw counts in the CSV) are set aside and counted. The harness fails (51 differences) when a quantity-affecting setting is changed, and it does not detect a change that only affects the buy list.

**Still open from Phase 0:** item-level gold labels (the worksheet gives totals, not positions), which need the estimator or the viewer; and the verifications in section 15 that need infrastructure or a vendor.

## 5. Placement and projects

```
AIToolbox.Web                    existing: auth, admin, tool registry, providers, directives, usage, SignalR
  Areas/Ductwork/                Razor UI, controllers, viewer JS (vendored Leaflet), ingest + LLM-gateway + worker APIs
AIToolbox.Ductwork.Contracts     net10 library, zero dependencies: detection-result contract, task payloads,
                                 Producer/Provenance types, JSON Schema generated from the C# types (C# is the source of truth)
AIToolbox.Ductwork.Core          net10 library: deterministic rules engine, aggregation, reconciliation, report model
                                 (pure logic, no web or CV dependencies, heavily unit-tested)
AIToolbox.Ductwork.Worker        net10 Worker Service / Windows service: supervisor that claims tasks and runs them
ductwork-sidecar/ (Python)       vector, label and OCR detectors behind the JSON contract; PyMuPDF isolated to two modules (licensing open)
AIToolbox.Ductwork.Tests         xUnit plus synthetic CAD-PDF fixtures with ground-truth JSON
AIToolbox.Ductwork.EvalCli        console tool: scores detector manifests against gold, applies user rules, prints the evaluation and calibration report
```

An **Area inside AIToolbox.Web** (not a separate web app) because authentication, tool access, API keys, providers, directives, usage logging, the Jobs menu and Admin all live there; the Area keeps its views, controllers and wwwroot self-contained. `AIToolbox.Web` references only Contracts and Core.

---

## 6. Key design decisions and their reasoning

### 6.1 Provenance: the Producer
The brief asked for "directive@version and model definition" on every entity. That cannot describe vector extraction or CV, which use neither. So every entity and task references a **Producer**, exactly one of: **LLM** (directive snapshot plus provider snapshot), **Algorithm** (name, version, params hash), or **Manual** (user). A CHECK constraint forbids an LLM producer without both snapshots. `ProviderSnapshot` (type, endpoint host, model, api version, max tokens, content hash, never the key) exists because provider rows are mutable.

### 6.2 Per-task models and the eval gate: `TaskBinding`
Tasks (`sheet-classify`, `schedule-extract`, `legend-propose`, `crop-classify`, `notes-to-rules`) each bind a directive snapshot and a provider snapshot, and **require an `EvalRunId`**. A new table is used instead of one ToolKey per task, which would clutter the tool directory and API scope list. Because the existing directive and provider admin screens edit live rows, they would bypass the eval rule, so Ductwork bindings pin immutable snapshots and change only through a gated path.

### 6.3 Providers, vision and data governance
Providers need a "vision processing enabled" tag (`SupportsVision`), an `AllowedForDuctwork` flag and a `GovernanceRoute`, because vision-capable and document-ingesting LLMs must run through **Microsoft Foundry** for data governance. `TaskBinding` refuses a provider lacking the tag, and the snapshot records the Foundry endpoint host. Only crops and tables are sent, and only through the approval gate in section 6.12. Foundry serves OpenAI models on an OpenAI-compatible path and Claude models on a native Anthropic Messages path, so each fits an existing provider type as a separate provider. Whether authentication is by API key (supported) or by Entra only (not supported by the current provider client) is **[unverified]**.

### 6.4 LLM gateway
The worker never holds provider keys. LLM calls go through a gateway endpoint on the instance, because keys are decrypted with Data Protection keys that live on the web host, and because snapshots and usage logging (`ToolUsageLog`, `ModelPricing`) are then stamped in one place. Each instance's own providers and Foundry configuration stay in that instance.

### 6.5 Worker coupling: HTTP API only
The worker never touches the DB or storage directly. It claims, heartbeats, reports progress, fetches sources, uploads results and calls the LLM gateway over a small REST surface, authenticated with a revocable `ApiConsumer` key. The web app is the **single write path**: it validates results against the contract and stamps provenance, so no code path can create an entity without provenance. A custom lean task table (parent/child, progress, attempt count, lease owner and expiry) was chosen over Hangfire, because the Python sidecar, typed contract and lease semantics map poorly to Hangfire's in-process method model.

### 6.6 Schema management
A dedicated **`ductwork` schema** with an ordered, versioned migration runner (embedded `.sql` files plus a schema-version table), separate from `DatabaseInitializer`. Only two things are shared with the rest of AIToolbox: the `ToolDefinition` row and a thin `ToolJobs` kick-off row (for the navbar Jobs menu and usage) that points at the Ductwork task. All progress, children and leases live in `ductwork`.

### 6.7 Extraction methods and the engine
The samples show that no single detector fits every drawing style, so extraction is a set of **detector families** behind one JSON contract. Each emits entities with an `Algorithm` producer (name and version), and the user or a classifier picks the method per view.

| Family | Used for | Basis |
|---|---|---|
| **A. Schedule and note reading** | Equipment, accessories from schedule remarks, notes-driven requirements | Wrapped engine modules (schedules, keynotes, notes) |
| **B. Geometry network** | Commercial duct runs, flex, fittings, HETs | Wrapped engine modules (ducts, network, fittings). Needs vector geometry |
| **C. Label and flag-note counting** | Residential unit plans: boots by `WxHxD [CFM]` label, fan tags (`EF-1`, `EF/LIGHT`), flag-note triangles mapped through the notes list (for example flag 2 is a pass-through, flag 8 a dryer vent box). Built as `ductwork-sidecar/ductwork_sidecar/residential.py`, data-driven by a label and note registry. A flag counts only when its digit sits inside a triangle symbol. Reports what is drawn and raises findings; conventions are user rules. Measured in section 4.7 |
| **D. Symbol matching** | Symbol-by-example, legend symbols (vector hash first, template fallback) | New |
| **E. OCR, vector glyphs and vision on text-less pages** | Text-less pages (the Sample C bound set). **Order: local first.** Tiled OCR plus flag digits read from the vector glyph paths inside each triangle reached 92.9% precision and 94.9% recall with user decisions applied (section 4.7), so hosted vision is needed only for the residue that local methods cannot resolve, and always through the approval gate in section 6.12. A live-text re-plot, when one exists (it does for this job), is preferred. Everything is a proposal for the user to verify |

**Sheet identification is our own step, not the engine's.** The engine's title-block rules failed on rotated blocks and treat electrical sheets as plans. Ours reads text and title-block crops, handles rotated blocks, filters to the mechanical discipline, finds several views per sheet, and **deduplicates sheets by content hash** across files.

**The engine is wrapped, not rewritten.** The Python sidecar runs the engine's geometry and schedule modules behind the JSON contract, with exact Sample A parity as a regression test. Required changes: no in-memory job state, no pickle cache on uploaded data, never delete the source, plan-specific logic moved into data-driven rules (Phase 4), and its output mapped to contract entities with provenance, evidence and view references. A C# worker owns orchestration, and Python runs only as a subprocess.

**CFM-only terminals** (no size on the label) are produced as entities with `Disposition = TBD` and a hold, never guessed. A rule or user decision resolves them (section 6.11). On the samples CFM alone cannot determine size: 100 CFM goes with both `4x10x7` (17 times) and `4x12` (6 times), while `4x10x4` is always 40 CFM (22 of 22).

**Library and licensing.** The engine depends on PyMuPDF, which is dual-licensed under AGPL or a commercial license through Artifex (confirmed). A central worker on a company server is a network-use case. This is now a concrete decision rather than a comparison: obtain a legal view, or restrict PyMuPDF to a clearly separated component, or evaluate PdfPig (Apache-2.0) for the non-engine parts. PDF only for v1. DXF could follow if needed **[parsers unverified]**, and DWG needs vendor libraries.

### 6.8 Storage
Mirror P21Toolbox's storage configuration, which an AIToolbox admin can configure. `P21Toolbox/FileStorage` already provides `IFileStorageProvider` with Direct (SMB), Relay and **SharePoint (Graph)** backends, chosen per root key, with `PathSafety` containment checks, and it targets net48 and net10.0 so AIToolbox **references it rather than forking it**. SharePoint matters because users already keep job files in SharePoint/OneDrive.
- The configured storage is the **system of record for source drawings, reports and archive zips**. That interface is shaped for deliver/pickup sync, not for the many small ranged reads a tile viewer needs, so **derived artifacts** (tiles, rasters, crops, vector dumps) live in a **local, rebuildable cache**; tile serving never hits Graph per request.
- SharePoint uses app-only Graph, which can read every site it is granted, so permissions should be `Sites.Selected` scoped to the Ductwork site per tenant. This needs an IT decision.
- Offer "import from SharePoint library" (pick a file, not browse the tenant) alongside direct chunked upload.
- Admin UI: a Ductwork Storage tab mirroring P21Toolbox's (provider type, path or site/drive/path, validate access, write-only secrets with a "configured" badge).
- Upload: chunked, hash-verified, streamed to disk with no in-memory buffering. Chunk size and maximum file size are admin settings. IIS and YARP allow 100,000,000 bytes, so the limit only needs to exceed one chunk. One real end-to-end test through YARP is still required.
- Ductwork gets its own storage options and does not use `FileStorageService`.

### 6.9 Tenant isolation and the central worker
Isolation means isolation **between instances**. Inside an instance there is no isolation need between Koch Air and Standard Air users. No tenant-key columns are needed in the schema because the instance database is the boundary.

A **central worker pool** serves all instances. It is acceptable because a customer's data only needs isolating from other tenants on the server, and because the worker cannot touch disk or DB except through the instance APIs with tenant-specific tokens.
- **Pull model:** the worker holds a registry of instances (name, base URL, API key with scope `tool:DuctworkTakeoff` plus a worker scope) and polls each instance's claim endpoint. No inbound connection to the worker; instances never call it. Revoking an instance's key cuts that tenant off at once.
- **One task, one instance, one process:** each claimed task runs as its own low-privilege process, started with only that instance's URL, token and task id. Other instances' credentials are never in its memory or environment. The credential store is readable only by the supervisor service.
- **Scratch only:** the only disk a task may use is `scratch/{instanceId}/{taskId}`, ACL'd to the task account, wiped on completion, failure, and at worker start. Sources stream from the artifact API (hash-verified, ranged, resumable, so a dropped connection does not restart a task), and results are pushed back through the API.
- **Fairness:** per-instance concurrency caps, round-robin or weighted claiming, global CPU/RAM limits, per-task time and memory limits (kill on breach), small interactive tasks ahead of bulk work.
- **Version skew:** an instance's web app and DB always match, but tenants may be on different product versions, and the worker is the only place they meet. So **the worker is upgraded first and stays backward-compatible with the previous contract major**. A claim carries the worker version and supported contract versions, an instance only hands out tasks it can serialize for them, and each instance's admin page shows the worker's last-seen time and version, with a clear message if that instance's version is unsupported.
- **Failure behavior:** if the worker is down, processing waits, but viewing and editing continue because tiles and results already live in the instance. Claims are lease-based, so adding a second worker node needs no code change.
- **Observability:** per-instance health on each instance's admin page, a central ops view, logs tagged with instance id, and no drawing content in logs.
- **Spillage tests (must pass before any second tenant):** two fake instances served by one worker; a task for A cannot read B's artifact id (404 under A's token); scratch is not shared; the task environment contains no other instance's credential; scratch is empty after every task, including after a forced kill.

**Worker host:** a dedicated VM on the Windows Server 2019 Datacenter Hyper-V cluster, so PDF rendering and Python do not compete with IIS.
- .NET 10 runtime and Python 3.12 in a pinned virtual environment with an offline wheel cache. .NET 10's official supported-OS list includes Windows Server 2019.
- A supervisor Windows service starts each task under a virtual or local account, limited with **Windows Job Objects** for memory, CPU and wall-clock time; the task account writes only to its scratch folder.
- Per-instance keys are protected (DPAPI machine scope or a certificate-protected file). Network is outbound HTTPS to instance URLs only, no inbound ports; Foundry calls are made by the instances, so the VM needs no direct LLM access.
- On cluster failover the VM restarts elsewhere; the worker is stateless, scratch is wiped at start, and expired leases requeue interrupted tasks. Only configuration and encrypted credentials need backing up.
- A **drain mode** (finish current tasks, claim none) supports patching and upgrades. Capacity is added by adding VMs. No GPU is planned; Hyper-V on 2019 supports passthrough only with specific hardware.
- Sizing and concurrency caps come from Phase 0 measurements (seconds per sheet, peak RAM, disk per job).

### 6.10 Canonical coordinate space
Sheet space is PDF points with origin **top-left** after applying page rotation and CropBox, y down, with the transform to PDF space recorded per page. Leaflet `CRS.Simple` conversion (y-up, negative lat) happens only at the viewer boundary, which is where y-flip bugs get pinned by tests.

---

### 6.11 Resolving ambiguity: findings, rules and overrides
**The user is always the arbiter.** Wherever the drawings are ambiguous or a detector disagrees with a reference, the tool raises a **finding** for the user and never resolves it silently. The samples show why: a boot labeled `4x12` where the estimator counts `4x12x7`; a boot labeled only `100 CFM` (100 CFM goes with both `4x10x7` and `4x12` on these sheets, while `4x10x4` is always 40 CFM); two grilles per pass-through flag; a fan symbol with no text label; a detector count that disagrees with a worksheet. None of these is a drawn fact, so each is a decision.

**A finding** carries the question, the candidate answers, and the evidence (a crop of the drawing, the label text, neighboring labels, earlier decisions, the reference number it disagrees with). It is a hold with a question attached, and it stays visible on the count sheet until resolved.

**The user resolves a finding in one of three ways:**
1. **Apply an existing rule**, for example "a pass-through flag means two 14x9 grilles". The tool previews every item on this job the rule would change before the user confirms.
2. **Create or add a rule** that covers the whole discrepancy. The user chooses its scope: this job, one building type, one sheet, or the instance-wide rule library (available to future jobs, as with the symbol library). The tool previews the affected items and the user approves.
3. **Override one item**, fitting by fitting, with a reason. A bulk selection can take the same override.

**Rules.** Two kinds share one engine: **interpretation rules** resolve ambiguity in detections (label grammar, CFM-to-size, multiplicity per symbol, default depth), and **requirement rules** derive items from notes (Phase 4). A rule has a scope, a deterministic match, and a deterministic action, and is versioned. Rules are retired, never deleted, because past results reference them. An AI may draft a rule from the user's plain-language explanation, but only an approved rule can change a count, and evaluation is deterministic, not an LLM decision.

**Precedence when several apply:** item override, then job rule, then building-type rule, then sheet rule, then instance rule, then the detector's default. A conflict between rules of the same level is a new finding, not a silent winner.

**Provenance and audit.** Every entity records how it was resolved (`Detector`, `Rule` with rule id and version, `Override`, or `UserAdded`). Rule creation, approval, application and retirement, and every override, are audit events. A rule application records what it changed, so it can be previewed, undone, and reproduced.

**Re-runs.** When a detector runs again, approved rules re-apply deterministically and overrides survive. An override whose underlying detection has changed is marked `Conflicted` and raised as a finding for the user, never dropped and never applied to a different nearby item.

**Timing.** Interpretation rules and this workflow are needed in Phase 3, because the detectors depend on them, earlier than the notes-to-rules work in Phase 4.

### 6.12 Hosted vision as an explicitly approved fallback
Sending drawing content to a hosted model is a data-governance event, so it is never the default path and never silent.

**Order of methods.** (1) The live text layer, or a live-text re-plot of the same sheet if one exists. (2) Local methods that send nothing off the server: OCR, symbol matching, geometry. (3) If those cannot resolve the sheet, the tool tells the user why and **asks permission** to use a hosted vision model. The user can also decline and resolve the sheet by hand, or request vision on a sheet themselves.

**The approval.** A user with edit permission on the job approves each use explicitly. The dialog states, in plain language:
- why local methods could not resolve the sheet;
- exactly which sheets and how many page images or crops will be sent;
- which provider, model and governance route (for example Microsoft Foundry) will receive them, and the retention terms the administrator has configured for that provider;
- the data-governance notice (administrator-editable, versioned wording), including that drawing content leaves the server and may contain customer information.

An approval covers **only the sheets listed, for that run**. It is not job-wide, not permanent, and it expires. Running vision on other sheets, or again later, needs a new approval. Results come back as proposals for the user to verify, like any other detector output.

**Administrator configuration (AIToolbox admin, Ductwork settings):**
- a **master switch**, **off by default**, so corporate guidance can disable the fallback at any time;
- which providers are allowed (each must be vision-enabled, allowed for Ductwork, and on a governance route);
- who may approve (default: users with edit permission; can be restricted to Job Managers or administrators);
- the notice wording and its version, a maximum number of pages per approval, and whether a written reason is required.

Turning the switch off takes effect at once: unused approvals are invalidated, queued vision tasks are cancelled, a running task stops at its next batch boundary, and the interface explains that the option is disabled by the administrator.

**Enforced where it cannot be bypassed.** All hosted-model calls already pass through the instance's LLM gateway (section 6.4). The gateway **refuses any request that carries image content** unless the task holds a valid, unexpired `VisionApproval` for exactly those sheets, the master switch is on, and the provider is on the allowed list. The worker has no provider keys and no other route, so it cannot bypass the gate. A CHECK constraint requires a `VisionApprovalId` on every task type that sends drawing content to a hosted model.

**Audit.** Each approval, each use, each refusal, each expiry, and every policy change is a job-level or settings audit event: who approved, when, which sheets, how many crops, the provider snapshot, the notice version shown, and the outcome. Usage and cost are logged with the existing usage records.

**Scope.** The gate applies to **every** hosted-model use that sends drawing content, so the other planned LLM tasks (sheet classification, schedule extraction, crop classification) pass through the same gateway check. **Decision:** per-run approval is the default for all of them. An administrator may additionally grant a **standing approval** for named task types only (for example sheet-classify, schedule-extract, crop-classify), limited to listed providers and a maximum pages per job. A standing approval is still subject to the master switch, the versioned notice and full audit, and the interface shows when one is in effect. Text-only prompts that contain no drawing content are not covered.

### 6.13 Review level: how much the user is shown
The user chooses how much the tool interrupts them, per job, at job setup and changeable later: **Sensitive** (the user is involved a lot), **Normal**, or **Insensitive** (the user accepts more of the tool's autonomy). An administrator sets the instance default and which levels are allowed, and can cap the maximum autonomy.

**What a level changes.** Each detection carries a calibrated confidence. The level sets a cutoff per detector family. Below the cutoff the detection is raised as an individual **finding**. Above it, the detection goes into the batch review queue (`SurfacedAs = Batch`), grouped by sheet and category, where the user can still inspect, edit or reject it. The level in effect is recorded on the job and on each entity (`ReviewLevelAtCreation`), so the audit log can answer what the user was shown.

**What a level can never change.** Everything stays a proposal until a human signs the sheet off, and these always reach the user at every level:
- sign-off itself, and the count sheet's status;
- holds and unknown quantities (never silent zeros);
- ambiguities with no candidate answer, rule conflicts, reference disagreements, and overrides marked `Conflicted`;
- the hosted-vision approval (section 6.12);
- anything the user's rules classify as "always ask".

Insensitive means fewer interruptions, not unverified counts.

**Calibration comes first.** A cutoff is only meaningful if confidence is calibrated, meaning "90% confident" is right about 90% of the time. The eval harness produces a calibration report per detector family (`CalibrationRun`) from gold labels. `ReviewLevelPolicy` (instance setting, versioned, audited) maps each level to a target precision, and the cutoffs are derived from the latest calibration. Where no calibration exists for a detector family, **every level behaves as Sensitive** for it. The initial target precisions are placeholders to be replaced from measured data before any pilot, and are tuned afterwards.

**Why a setting and not a threshold in code.** The estimator has no accuracy number to offer, firms differ in how much they trust a first pass, and the right balance will move as the detectors improve. The same setting makes the trade-off measurable: the activity log (section 10.1) lets us compare time-on-job and correction rates by level.

## 7. Data model (Phase 1 target)

```
-- provenance and AI configuration
Producer          (immutable) Kind {Llm|Algorithm|Manual}, DirectiveSnapshotId?, ProviderSnapshotId?, AlgorithmName?,
                  AlgorithmVersion?, ParamsHash?, ManualUserEmail?   CHECK per Kind; UNIQUE natural key
ProviderSnapshot  (immutable) type, endpoint host, model, api version, max tokens, ContentHash UNIQUE (no key)
TaskBinding       TaskKey, DirectiveSnapshotId NOT NULL, ProviderSnapshotId NOT NULL, EvalRunId NOT NULL, ActiveFrom
-- jobs (a Job is a whole development)
Job               JobId, PublicId, Name, CustomerName, ProjectName (free text), SiteAddress, OwnerEmail,
                  State {Draft|InReview|Completing|Complete|Closed|Archiving|Archived|Restoring}, CompletionVersion,
                  StateVersion, ReviewLevel {Sensitive|Normal|Insensitive} (section 6.13), BaselineManualHours? (section 10.1),
                  Completed/Closed Utc+By+Reason, summary JSON     ** master record, never deleted **
JobMember         JobId, UserEmail, Role {Owner|Editor|Viewer}        (Owner and Editor = "edit permission")
Tag, JobTag       Tag(Name, NameNormalized UNIQUE); JobTag(JobId, TagId, AddedBy, AddedUtc)
JobWorkflowStep   JobId, Step {Setup|Process|Review|Report}, Status {NotStarted|Active|Done}
ToolPermission    UserEmail, Permission {JobManager}          (Ductwork-only, not a global AIToolbox role)
JobEditLock, PreemptRequest, JobArchive, JobAuditEvent        (sections 9, 10, 8)
ActivitySession, ActivityHeartbeat                            (section 10.1, time-on-job and ROI)
ReviewLevelPolicy (instance, versioned), CalibrationRun       (section 6.13)
-- building types and multipliers
BuildingType      JobId, Name (for example "Type 4", "Office"), Kind {UnitType|Building|CommonArea|Other},
                  Multiplier (the number of identical buildings or units), MultiplierSource {Manual|ArchitecturalList|Suggested},
                  MultiplierEvidence (text or reference), Confirmed bit, Notes
                  Multiplier is user-confirmed input. A detector or the engine may only SUGGEST it
-- drawing content
DrawingSetRevision JobId, RevisionNo, SupersedesRevisionId?          (addenda; diffing is later work)
SourceFile        RevisionId, Sha256, StoragePath, Bytes, ContentType, DuplicateOfSourceFileId?
Sheet             RevisionId, SourceFileId, PageNo, SheetNumber, Title, Discipline, SheetKind {Plan|Schedule|Legend|Notes|Detail|Riser|Other},
                  ContentHash, DuplicateOfSheetId?, HasTextLayer bit, CoordinateSpaceJson, SupersededBySheetId?
SheetView         SheetId, Title, RegionPolygonJson, ScaleRatio, ScaleCalibrationJson, Floor, ViewType, ExtractionMethod {Geometry|LabelCount|
                  Schedule|Vision|Manual}, BuildingTypeId?        (scale and calibration live here, one sheet can hold several views)
SheetRegion       SheetId, Kind {Include|Exclude}, Reason, PolygonJson
Entity            EntityId, SheetViewId, BuildingTypeId?, ProducerId NOT NULL, ProcessingTaskId NOT NULL, Type, Category, Tag, Size,
                  Manufacturer?, Model?, CustomerPartCode?, Quantity, Unit {EA|LF|SF}, Floor, View,
                  Source {Vector|Detector|Vlm|Manual|Derived}, Method {Geometry|Text|FlagNote|Symbol|Schedule|Vision|Manual},
                  Confidence, SurfacedAs {Finding|Batch}, ReviewLevelAtCreation, ReviewStatus {Proposed|Verified|Rejected|Manual},
                  Disposition {Included|Excluded|FactoryFurnished|TBD}, IsStale bit, StaleReason,
                  ResolutionSource {Detector|Rule|Override|UserAdded}, ResolvedByRuleId?, OverrideId?,
                  BBox (4 float cols, indexed), PolygonJson, ParentEntityId?, DerivedFromRuleId?, Supersedes/SupersededByEntityId?
                  ReviewStatus and Disposition are independent. Resolving one never approves the other
EntityRevision    (append-only) every state change: basis for undo, history and the correction log
EntityDependency  EntityId, DependsOnEntityId, Kind     (editing a run marks dependent fittings, supports, insulation and dampers stale)
Hold              JobId, Topic, Reference, RequiredEvidence, Owner, State {Open|Resolved|Waived}, ResolutionNote, ResolvedBy/Utc
Finding           JobId, Kind {AmbiguousLabel|GrammarVariant|ReferenceDisagreement|SymbolOnly|MissingEvidence|Other}, EntityId?, Question,
                  CandidatesJson, EvidenceRefs (crop, labels, reference value), State {Open|Resolved|Waived},
                  ResolvedByKind {Rule|Override|Waiver}, ResolvedRuleId?, ResolvedOverrideId?, ResolvedBy/Utc
Rule              RuleId, Kind {Interpretation|Requirement}, Scope {Job|BuildingType|Sheet|Tenant}, JobId?, BuildingTypeId?, SchemaVersion,
                  RuleVersion, MatchJson, ActionJson (deterministic), Status {Proposed|Approved|Retired}, ProposedBy (user or producer),
                  ApprovedBy/Utc, Reason       (retired, never deleted)
RuleApplication   RuleId, RuleVersion, EntityId, AppliedUtc, PreviousValueJson     (what a rule changed: preview, undo, reproducibility)
EntityOverride    EntityId, Field, FromJson, ToJson, Reason, By/Utc, State {Active|Conflicted|Withdrawn}
SymbolDefinition, SymbolVersion, SymbolContext, SymbolMatch          (section 11)
-- hosted-vision governance (section 6.12)
VisionPolicy      (instance settings, versioned) Enabled (default 0), AllowedProviderIds, ApproverRule {EditPermission|JobManager|Admin},
                  NoticeText + NoticeVersion, MaxPagesPerApproval, RequireReason, StandingApprovalTaskTypes (default none), StandingMaxPagesPerJob, ChangedBy/Utc
VisionApproval    ApprovalId, JobId, ApprovedBy, ApprovedUtc, ExpiresUtc, SheetIdsJson, PageCount, CropCount, ProviderSnapshotId,
                  NoticeVersion, Reason?, Status {Active|Used|Expired|Revoked}
-- work and evaluation
ProcessingTask    ProcessingTaskId, ParentTaskId?, JobId, TaskType, Status, Progress{done,total,message}, Attempt, MaxAttempts,
                  LeaseOwner, LeaseExpiresUtc, ProducerId NOT NULL, InputManifestRef, ResultManifestRef, ContractVersion,
                  VisionApprovalId? (CHECK: NOT NULL for any task type that sends drawing content to a hosted model)
EvalRun/EvalResult, GoldSet/GoldLabel   per-fitting-type and per-building-type TP/FP/FN, precision, recall; labels fed by accepted
                  corrections; GoldSet carries Role {Development|Holdout|Regression} and a "holdout spent" marker
```

Invariants enforced in schema and types:
- Job-scoped tables carry a denormalized `JobId NOT NULL` and a `PublicId`, which makes the edit-lock trigger, purge and restore simple.
- `Entity.ProducerId` and `ProcessingTaskId` are NOT NULL, and the only insert path takes a non-default `Provenance` value issued by the task context.
- **The count sheet is computed, never stored or typed.** Extended quantity equals per-type quantity times the building type's multiplier, and job totals sum per category, tag, size and unit. Units are never summed together.
- A job cannot be Completed with an unconfirmed multiplier (`Confirmed = 0`) on a building type that has included entities.
- A detector's value changes only through an approved rule or an item override, and the entity records which (section 6.11). The count sheet can show it.
- Large payloads (vector dumps, tiles, crops) live on disk. SQL holds paths, hashes and manifests. The revision and supersession columns exist now even though addendum diffing comes later.

## 8. Job persistence, lifecycle and archiving

### 8.1 Where a job lives
| Stage | Authoritative store | Why |
|---|---|---|
| Working (Draft/InReview) | SQL Server, `ductwork` schema | Edited concurrently, needs transactions, per-edit append, undo, queries by sheet/type/confidence and an atomic lock. A JSON file cannot do partial updates, concurrent access or undo safely. |
| Complete / Closed | Same SQL rows, read-only; issued reports as files in tenant storage | Still instantly viewable and reopenable. |
| Archived (zip only) | SQL kept plus a zip in archive storage | A safety copy; the job stays Complete/Closed and viewable. |
| Archived and purged | The zip is the only full copy; SQL keeps the job master record and metadata | Not viewable until restored. |

The archive is a self-describing file because it is kept long-term and must outlive schema changes. Working data is relational because it is edited.

### 8.2 Workflow steps, Complete and Close
Workflow steps tracked per job: **Setup** (sheets and views set up, scale calibrated, building types and multipliers defined), **Process** (detection finished), **Review** (every in-scope sheet signed off), **Report** (count sheet generated).
- **Complete:** "the job is done, all work is complete": all four steps Done and every building type with included entities has a confirmed multiplier. Editing disabled.
- **Close:** "we're not working on this anymore": a deliberate end for a job that did **not** run through all steps (lost bid, partial scope). Editing disabled, reason required. It is a distinct state, so the record honestly shows the workflow was not finished.
- Neither is allowed while any processing task on the job is active (queued, running or retrying); the user waits or cancels the tasks. Unreconciled schedule counts and open RFIs are listed for information at Complete.
- Both need the edit lock (acquired as part of the action) and any user with edit permission may perform them. Complete first produces the issued reports in `Completing`; a failed report run stays `Completing` with retry, never silently `Complete`.
- **Reopen:** any user with edit permission on the job. Reason recorded, audited, the reopener takes the edit lock, the completion version increments, and any earlier archive is flagged `Superseded` but never deleted.
- **Enforced in three places, not only the UI:** every mutating endpoint returns 409 when the job is not editable; data-layer mutation methods take an `EditToken` only the lock service can issue for an editable job; a SQL trigger on the content tables rejects writes when the owning job is not editable, with one narrow audited path for reopen and restore.

**Lifecycle:** `Draft`/`InReview` -> `Completing` -> `Complete`; `Draft`/`InReview` -> `Closed`; `Complete`/`Closed` -> `Archiving` -> `Archived` (or back to Complete/Closed for zip only); `Archived` -> `Restoring` -> the state it had; `Complete`/`Closed` -> reopen -> `Draft`.

### 8.3 Archive, purge and restore
Complete and Archive are different actions. **Complete** means no more editing; **Archive** makes a restorable copy and optionally removes the SQL detail.
- **Manual only.** There is no automatic archiving. Only Complete or Closed jobs can be archived. Runs as a processing task.
- **Zip only:** writes the package and a `JobArchive` row, keeps the SQL data, and returns to Complete/Closed.
- **Archive and purge** is safety-gated. Nothing is deleted unless all gates pass:
  1. Write the zip to archive storage.
  2. Read it back and verify its SHA-256 and every file hash in the manifest.
  3. Verify per-table row counts in the manifest equal the live counts, from one consistent snapshot with the job frozen.
  4. Dry-run restore: parse every file against the archive schema for its version and validate referential integrity in memory.
  5. Persist `PurgePending`, then delete detail rows in resumable batches (jobs may have 100k+ rows). A crash resumes at the same step, and after step 2 the zip is verified, so a half-purged job is still restorable.
  6. Delete the local cache, set `Archived`, audit.
  The UI states that purge is irreversible except via the zip and requires typing the job name.
- **The Job master record is never purged or deleted.** Only detail rows go. This gives a permanent record for tracking and lets other records keep a link to the job. It is enforced by no DELETE permission for the application login, a trigger, and `NO ACTION` foreign keys from anything that must outlive the job. After purge SQL keeps: the master record, a summary (counts, floor and job totals, so lists and dashboards still work), the `JobArchive` row (location, SHA-256, size, archive and contract versions, per-table row counts, timestamps), tags, and the lifecycle/lock/access/archive audit categories.
- Shared immutable records (`Producer`, directive and provider snapshots) are not purged; the zip carries copies so restore can recreate any that are missing. Tenant-library symbols are never touched by a purge.
- **Package:** `ductwork-{jobId}-v{completionVersion}-{yyyyMMdd}.zip` containing `manifest.json` (archive schema version, contract version, per-table row counts, SHA-256 of every file, audit hash-chain head), `data/*.json` (one file per table, JSON Lines for large ones: a **row-level export sufficient to re-populate the job**, not just a report), `provenance.json` (every producer with directive text and provider snapshot values copied in), `summary.json`, `reports/`, `symbols/` (versions used), `tags.json`, and `sources/` with **all source PDFs, always bundled**, because a purged job whose source was later deleted from SharePoint could not rebuild its tiles. Archives can therefore be large. Reading one needs only JSON tooling.
- **Restore:** by the job owner, a Job Manager or an admin. Locate the zip through `JobArchive`, verify its hash, run the versioned importer for its archive version, validate, insert in dependency order, re-point shared records by content hash (or recreate them), and rebuild the cache from the sources. Job-scoped tables use a stable `PublicId` GUID in the archive and all cross-references, so restore does not depend on identity values. The job returns as Complete/Closed (read-only) and can then be reopened. Restore refuses if live rows for the job already exist. **Same tenant only**: a zip is the tenant's data and cross-instance restore would break isolation.
- **Missing archive:** if the zip is not at its recorded location, restore stops with "Archive file not found. Contact your administrator.", with no partial restore. The failure is audited and raised as an admin alert with the last known path and hash.
- **Storage management:** zips are never deleted automatically. An admin can move a zip to secondary storage from an Archives page (copy, verify the hash at the destination, update the location with a history, optionally remove the original, audited). If a file is moved outside the app, a Relink action points the record at the new path and verifies the hash.
- **Structural tests:** a round-trip test archives a fixture job, purges it, restores it and compares a canonical hash of all job data before and after. Golden zips from every released archive version stay in the suite so old archives remain restorable. An archive-and-purge does not ship without a passing round-trip test. The archive format is versioned separately from the DB schema, frozen early and changed only additively.

### 8.4 Permissions (Ductwork only)
| Who | Can |
|---|---|
| Viewer (per job) | View the job, reports and audit log |
| Editor (per job) | Hold the edit lock, edit, complete, close, reopen, tag |
| Owner (per job) | Everything an Editor can, manage members, archive/purge/restore that job |
| **Job Manager** (Ductwork tool permission) | Archive, purge and restore **any** job, member or not. Grants no view or edit access to job contents. Granted by an AIToolbox admin in the Ductwork admin tab. Not a global AIToolbox permission |
| AIToolbox admin | Grant Job Manager, manage storage, move/relink archives, force lock takeover, configure the hosted-vision policy |

Archive, purge and restore are restricted because purge is destructive; reopen and complete are open to editors because they are reversible and part of normal work.

### 8.5 Tags
Free-form labels on a Job used to filter the job list. One tag (for example "Orion Building") can sit on many jobs, so no separate project-grouping system is needed. Tags are unique within the instance (case-insensitive, trimmed) and cannot cross tenants by construction. Any editor can add or remove them. They are job-header metadata, not job content, so they can be edited on Complete, Closed and Archived jobs without reopening or taking the lock; every change is audited. They live on the master record, survive purge, and are written to and restored from the zip. The job list filters by one or more tags (all-of by default), state, owner and date.

---

## 9. Concurrent editing: single editor, takeover, fencing

Only one session edits a Job at a time; everyone else is view-only.
- **Lock identity is the browser session, not the user**, so a second tab by the same person is view-only.
- `JobEditLock(JobId PK, HolderUserEmail, HolderSessionId, FencingToken bigint, AcquiredUtc, LastHeartbeatUtc, ExpiresUtc)` and `PreemptRequest(Id, JobId, requester, RequestedUtc, ExpiresUtc, Status {Pending|Granted|Cancelled|Denied|Expired})`.
- **Open:** one atomic statement acquires the lock (none held, expired, or same session) or returns the holder. Only users with edit permission can hold it.
- **Heartbeat** every 15 s (SignalR with HTTP fallback); the lock expires after 60 s without one; graceful exit releases at once (explicit close plus `sendBeacon`). Both timings are admin-configurable. Crashes and closed laptops are covered by expiry.
- **Read-only view:** banner "Being edited by *Name* (active 12 s ago)", a **Take over editing** button, and a live `StateVersion` push so the view never goes long-stale.
- **Takeover:**
  1. *Stale lock* (heartbeat expired): immediate, no prompt, audited. This is the common ungraceful-exit case.
  2. *Live lock:* the requester creates a `PreemptRequest`; the holder immediately sees a modal "*Name* wants to take over editing" with **Keep editing (cancel request)** and **Hand over now** and a countdown. Cancel ends the request as Denied; Hand over transfers at once; **no response within a configurable timeout (default 60 s) grants it**, because an open but unattended tab would otherwise block the job indefinitely, its heartbeat never lapsing. The requester can also cancel a pending request.
  3. An owner or admin can force takeover (audited).
- **Preempted editor:** a `LockLost` push makes the page read-only with a blocking notice ("taken over by *Name* at *time*, this view may be out of date") offering **Reload latest** or **Stay read-only**. **Fencing makes this safe even if the push is missed:** every write carries the fencing token, which changes on takeover, so a late write from the old browser is rejected with 409 and the same notice. A preempted user can never overwrite the new editor's work.
- Edits are saved **per operation** (each writes an `EntityRevision` immediately), so there is no large unsaved buffer; at most the single in-flight operation is rejected.
- Reprocessing (re-run detection or ingest) is a mutation: blocked while a user holds the lock or the job is Complete/Closed, and it follows the same fencing rules. Undo is per user and valid only while that session holds the lock.

---

## 10. Job audit log

Every Job has a rich, append-only log of who did what, when, from where, to which object, why, and what changed. It is the user-facing history; `EntityRevision` is the machine-facing state history used for undo.

`JobAuditEvent`: EventId (monotonic per job), JobId, OccurredUtc (server clock), ActorType {User|System|Worker|Llm}, actor email/name/session, ClientIp, UserAgent, Category, Action, SubjectType, SubjectPublicId, human-readable Summary, Before/After JSON (changed fields only), Reason, CorrelationId, ProcessingTaskId?, ProducerId?, RevisionId?, FencingToken?, StateVersion, PrevHash, Hash.

**Recorded:** lifecycle (created, edited, completed, closed, reopened with reason, archived, purged, restored); lock events (acquired, released, expired, takeover requested/cancelled/denied/granted/forced, `LockLost`, stale-token write rejected); access and membership (member changes, job opened, report or archive downloaded, data exported; session starts, not every pan or zoom); sources and processing (upload with name, size and hash; each task started/finished/failed/retried with its producer, token usage and cost); setup (floor/view/discipline, regions, scale calibration inputs and result); entity edits (each with before/after and `RevisionId`; a **lasso or bulk operation is one audit event** with a count and `CorrelationId` plus one revision per entity, so the log stays readable and undo stays exact); rules and symbols; reports and assumption/RFI changes; tag changes; hosted-vision approvals, uses, refusals and policy changes (who, which sheets, provider, notice version).

**Structural guarantees:**
- **No mutation without an audit event and no audit event without a mutation.** Every data-layer mutating method takes an `AuditContext` (actor, session, reason, correlation) and writes both in one transaction. An architecture test fails the build if a public mutating repository method lacks one.
- **Append-only:** the application login has no UPDATE or DELETE on the table, and a trigger rejects both, with the audited purge path as the single exception.
- **Tamper evidence:** `Hash = SHA-256(PrevHash + canonical event JSON)` per job; a verify action recomputes the chain and reports the first break; the chain head goes into the archive manifest.
- Server time and server-derived identity only; the client never supplies actor, timestamp or session.

**UI:** an Audit tab with a filterable timeline (user, category, date, sheet, entity type, text), a History drawer on any entity leaf, CSV/JSON export, and a verify-integrity button; viewers can read it.

**Retention:** the complete log is always in the zip with its chain. After archive-and-purge, SQL keeps the low-volume categories (lifecycle, lock, access/membership, archive) so "who touched this job and when" is answerable without restoring; entity-edit and processing detail live only in the zip. Restore re-inserts them and re-verifies the chain. A job's gold-set crops (from correction events) are kept as separate, instance-isolated copies with their own retention, so a purge neither silently deletes nor silently keeps customer drawing content that was deliberately captured for training.

### 10.1 Activity and time-on-job metrics (ROI)

The audit log says what changed. This section says how long the work took, so the tool's value can be shown with measurements instead of estimates.

**Time is computed on the server from heartbeats**, never reported by the client. The viewer sends a heartbeat (it reuses the lock heartbeat, 15 s) carrying only a state: **Active** if there was input (pointer, key, wheel, touch) in the last N seconds (default 60, configurable) and the tab is visible, otherwise **Idle**. The server adds the gap since the previous heartbeat to active or idle time, **capped at two heartbeat intervals**, so a closed laptop or a dead tab accrues nothing. A session ends on explicit close, on lock expiry, or on no heartbeat for the expiry period.

**`ActivitySession`:** SessionId (the same identity the lock and audit log use), JobId, user, WorkflowStep (Setup, Review, Report; Process is machine time), Surface (viewer, count sheet, findings, rules, symbols), StartedUtc, EndedUtc, ActiveSeconds, IdleSeconds, ReviewLevel at the time, ClientKind. `ActivityHeartbeat` keeps the raw state samples briefly, with a short retention, since the session totals are the durable record. Counters of what the user did (edits, verifications, rejections, findings resolved, overrides, rules created) are not stored twice: they are derived by joining the audit log on session id.

**Machine time and cost** come from `ProcessingTask`: queue wait, run time, retries, tokens and cost per producer. **Application logging** is structured, in the existing Serilog sink with a Ductwork category: every task start, finish, failure and retry; every gateway call with provider, tokens, duration and approval id; every lock event; correlation id on all of it so one job's story can be followed across web, gateway and worker.

**ROI summary per job** (a computed view, also in the Excel report's metrics tab and the admin page):
- calendar time: created to first count sheet, to complete;
- human active hours by step, and idle hours, from `ActivitySession`;
- machine hours and AI cost;
- how much the tool produced versus what the user did with it: proposals, accepted unchanged, edited, rejected, added by hand (a recall proxy), findings raised and resolved, rules created, rework (reopens);
- comparison with a **baseline**: `Job.BaselineManualHours` (the estimator's own estimate of the manual time, entered at completion, or an instance default per sheet) and, where one real job is timed by hand, that measurement;
- estimated saving = baseline minus human active hours, times a loaded hourly rate that is an **administrator setting** and not stored per person, minus AI cost.

**Cohort comparisons** for tuning: by review level, by detector family, by building type, by firm or designer. These are how we learn whether Insensitive actually saves time without raising correction rates.

**Privacy.** Time-on-job can become employee monitoring. The default reports are **job-level aggregates**. Per-user breakdown is visible only to Job Managers and administrators, is stated in the tool's notice, and an administrator setting can turn per-user detail off, keeping totals only. This is a company decision (section 15).

**Retention.** Session totals are kept with the job and included in the archive zip and the retained low-volume categories, so ROI survives archive-and-purge. Raw heartbeats are purged on a short schedule.

---

## 11. Symbol library

v1 is a **tenant-isolated, instance-wide library shared by users and jobs**, with an extensible foundation. Cross-tenant sharing is deferred until needed.
- **Scopes:** `Job` (default, only that Job) and `Tenant` (everyone in this instance). Scope is a plain column with no CHECK limiting it to two values, so a later scope is an additive migration.
- **Promotion inside the instance:** a user tags a job symbol or a whole job library "Add to tenant library". It is a **copy**, not a move or reference, so deleting the original or purging its job never affects jobs that came to depend on the library symbol. The copy records `SourceJobId` (a real FK to the permanent Job master record, possible because it is one database) and `CopiedFromVersionId`.
- **Foundation seams built from day one:** (1) `SymbolDefinition` plus immutable, content-hashed `SymbolVersion`; edits create a version and jobs/matches pin one, so library changes never silently alter an existing job; (2) content (crop, vector hash, template, meaning) kept apart from context (sheet, box, who) in `SymbolContext`, so a later anonymized export is just content without context; (3) stable `PublicId` and `ContentHash`; (4) all access through an `ISymbolLibrary` interface with one local implementation; (5) the archive zip carries the versions a job used.
- **Retire, never delete:** tenant-library symbols are retired by their owner or an admin; a retired version stays readable for pinned jobs.
- **If cross-tenant sharing is revisited:** instance databases are separate, so it needs a neutral central home, most plausibly a small central "Ductwork Hub" service with its own DB (like the existing RingCX hub), not a tenant instance and not the stateless worker. Anonymization would run in the source instance before anything is sent, promotion would be an anonymized deep copy, another tenant would review a symbol and explicitly import it as its own copy, with no admin approval because users know better than administrators which symbols are right, plus withdraw and flag actions. A system symbol's link to its source job would be a soft reference (instance id plus Job `PublicId`) because a foreign key cannot span databases. The v1 seams make this additive.

---

## 12. Count sheet and reports

**The deliverable is a concise, logical count sheet** that the user keys into P21, which produces the quote. The tool does not generate quotes, pricing, waste allowances, pack rounding or order quantities in v1. (The Sample B quote PDF in the samples is a P21-generated quote and is not our output.) An optional P21-importable file may be added later as another renderer.

**Count sheet layout:** the **building-type breakdown comes first**, one block per building type grouped by category or stage, followed by the job totals.

| Column | Meaning |
|---|---|
| Building type | For example Type 4, Office, Extended Garages |
| Category | For example Flex/Boot/Grill/Take-off, EF, GRD, Duct |
| Tag | The drawing tag |
| Description | As on the drawing |
| Size | For example 4x10x7 |
| Manufacturer, Model | Only where documented; never invented |
| Customer part code | Optional (Sample B-style codes) |
| **Quantity per building** | The count for one building or unit of this type |
| Unit | EA, LF or SF. Never mixed or summed together |
| **Multiplier** | The number of identical buildings or units, user-confirmed with its source |
| **Extended quantity** | Quantity times multiplier, computed, never typed |
| Status | Review status and disposition (included, excluded, factory-furnished, TBD) |
| Notes | Evidence references, holds |

Job totals sum extended quantities per category, tag, size and unit. Unknown quantities stay **TBD**, never zero. A holds list accompanies the sheet. Supporting outputs are a detail list with evidence, a marked-up PDF for review, the rules applied, assumptions/exclusions/RFI flags, and the audit and exceptions list.

**Implementation:** the count sheet comes from a neutral **report model** in `Ductwork.Core`, rendered through an `IReportRenderer` interface (Excel, HTML, PDF, and later P21 import) as processing tasks on the worker, never in IIS. All renderers read the same effective item state, so the screen, Excel and PDF cannot disagree.
- **Excel:** ClosedXML (MIT, per secondary sources; already used in other repos).
- **PDF, proof of concept:** HTML (also the on-screen preview) printed to PDF by a headless browser using CSS `@page`. Free. Server 2019 is believed not to ship Chromium-based Edge, so the worker VM installs a **pinned Edge or Chromium build [unverified]**. Pin and patch it, cap concurrency and time per render, and render only from local files.
- **If production rejects headless printing:** PDFsharp/MigraDoc (its official site states it is MIT-licensed and free, including commercially, so the EUR 750 quote needs explaining before any purchase) or QuestPDF (about $2k per year, ruled out because the preference is free or one-time licensing; its community license is free only below US$1M annual revenue). Neither renders HTML, so switching means re-implementing layout. Keeping content in the report model confines that to layout code.
- Commercial viewers (Apryse, Nutrient) are a noted alternative to the Leaflet viewer only.

## 13. Viewer

A normal Razor page (no iframe, see section 3) with **Leaflet `CRS.Simple`** over a tile pyramid, vendored by hand; OpenSeadragon is the fallback if large rasters demand it. Canvas rendering for 10k+ polygons. Per-type colored highlights with show/hide filters; the side tree (building type, then fitting type, then fitting leaves) and the overlay share **one selection store keyed by entity id**, so hovering either highlights the other and shows where the item sits in the report. Derived (rule-generated) items appear as leaves and highlight at the parent's location with a link to the rule. Tiles are served through an authenticated controller with ETag and immutable caching.

---

## 14. Phased plan

Sizing is relative (S/M/L).

**Phase 0: platform prerequisites, baseline and eval harness (M-L).** Part of this is already done (section 4).
- *Done:* sample survey; engine baselines; the residential detector built and measured (section 4.7); the text-less method measured on all eight bound-set unit plans and decided (local OCR and vector glyph digits first, approved hosted vision only as a fallback); the engine-parity harness (Sample A, 385 lines identical); markup survey; licensing and platform verifications. **Built and committed:** the `ductwork` schema migration runner with Producer, provider-snapshot and evaluation tables (CHECK constraints, holdout-spent trigger) tested on LocalDB; Contracts v0 with generated, strict JSON Schema and a drift test; provider capability flags, image input, structured-output validation and provider snapshots (stub-tested, with the real schema initializer exercised on LocalDB); a synthetic CAD-style PDF fixture generator cross-checked against a second PDF reader; the evaluation harness, scoring, calibration and review-level policy; the deterministic interpretation-rule engine; the server-side time-on-job accumulator; an evaluation CLI.
- *Remaining without the user:* nothing. The infrastructure checks (a real 100 MB upload through IIS and YARP, Edge or Chromium on the worker VM, Foundry authentication, `Sites.Selected`) need the environment, credentials or a vendor reply.
- *Remaining with the user:* item-level gold labels (made in the viewer from Phase 1, or from a review pack with the estimator), because the worksheet gives totals and not positions; the legal view on PyMuPDF.
- **Exit:** measured match rates and a **calibration report** (how well confidence predicts correctness) on the development set for each detector family, reported without pass/fail thresholds because the review level (section 6.13) is the configurable control and its cutoffs come from this data; engine parity holds; the text-less method decision is recorded; the holdout remains sealed. Measurements also size the worker host and concurrency caps.
- **Risks:** the worksheet itself may contain errors (identical counts across unit types), so disagreements need adjudication in the viewer; other designers use other label conventions, so the residential detector may need new rules per designer; confidentiality of drawings sent to hosted LLMs.

**Phase 1: foundations (L).** Schema and migration runner; the data model including building types and multipliers, sheet views, disposition, holds and stale-dependency tracking; provenance types and enforcement tests; processing-task table with lease/claim/heartbeat/retry/parent-child/progress; Job entity with lifecycle, membership, tags, workflow steps, master-record protection, `JobArchive`, the edit lock and `JobAuditEvent` with the `AuditContext` architecture test; `ToolPermission` and the Job Manager grant UI; worker skeleton with the instance registry, per-instance keys, version handshake, per-task process isolation, scratch folders, concurrency caps, drain mode and the spillage test suite; chunked upload and the artifact-store abstraction; tool registration (`DuctworkTakeoff` definition, policy, Home card, log sink, scope); the hosted-vision policy settings page and the gateway's approval enforcement (section 6.12); activity heartbeats, `ActivitySession` and the ROI summary (section 10.1); the review-level setting and policy page (section 6.13); Jobs menu generalization; viewer with a building-type, fitting-type and leaf tree, filters and hover/selection sync on **fake detections**; a count-sheet skeleton (building-type blocks, multiplier and extended columns) computed from fake data; SignalR progress.
- **Exit:** a 100 MB PDF uploads through IIS and YARP; a fake task runs end to end through the worker and survives a killed worker; fake entities sync tree and overlay at 10k+ entities; extended quantities recompute correctly when a multiplier changes; no code path inserts an entity without provenance; lock, takeover and completion rules verified with two browsers.
- **Risks:** proxy limits (test on the real path); viewer performance; hand-vendored Leaflet with no bundler.
- **Progress (data layer, committed and tested on LocalDB only; nothing deployed):**
  - *1a:* jobs, lifecycle state machine, permissions, members, tags, workflow steps, and the hash-chained append-only audit log, with every change and its event in one transaction under a job-row lock.
  - *1b:* the single-editor lock with fencing tokens, takeover, timeout grant and forced takeover; the processing-task queue with atomic claim, leases, reclaim, retry with backoff and cancel.
  - *1c:* building types with the rule that a detector only suggests a multiplier and a person confirms it; entities whose only creation path is a worker's result, which is validated, stamped with the task's own producer and task, and stored atomically with finishing the task (a refused result stores nothing); the review-level decision applied at ingestion; findings stored open; the count sheet computed (extended quantity, units never mixed, unknowns to be determined and never zero, undetermined items listed and kept off the totals, totals that name the unconfirmed types holding them up); and a completion check that now sees unconfirmed multipliers.
  - *1d:* the HTTP surface. A browser API (`ductwork/api/...`, cookie auth, antiforgery, tab and fencing-token headers; jobs, lock, building types, count sheet, entities, tasks, audit, time on job) and a central-worker API (`api/v1/ductwork/worker`, API key with the `ductwork:worker` and tool scopes: handshake, claim, heartbeat, result, fail) plus an admin API for task bindings. A non-member always gets 404, never 403, so ids reveal nothing (lock acquire, takeover and force were fixed to follow this). The `DuctworkTakeoff` tool is registered disabled, and migrations run only when `Ductwork:RunMigrationsOnStartup` is set. Covered by 48 controller-level tests over a real database, including the full bind, queue, claim, result and count-sheet round trip.
  - *1e:* drawing intake. An artifact-store abstraction (staging and final areas, server-built keys, path containment) with a local-folder implementation (`Ductwork:ArtifactRoot`); resumable chunked upload that streams to disk and never buffers a file, with per-piece and whole-file SHA-256 checks, a server-held offset that is the authority (bytes left by a crash are cut off), a PDF check, edit-lock fencing at start and finish, and an expiry sweep; drawing-set revisions and source files, with an identical file on the same job recorded as a duplicate and stored once; list and download endpoints (download is sandboxed and never served as a page). The first run of the tests found two real bugs (a null binary parameter, and a duplicate lookup that turned "none" into id 0). Still to do for this area: a real 100 MB upload through IIS and YARP, and the SMB, relay and SharePoint providers behind the same abstraction.
  - *1f:* sheets, views and regions (migration 0008). Pages of an uploaded file are recorded by a person, or by the worker under the task it holds (`POST /api/v1/ductwork/worker/tasks/{id}/sheets`). Each sheet keeps its size in PDF points, an optional content hash, and the task that found it; a page whose content the job already holds is flagged as a duplicate and left out of scope until a person brings it back. A sheet can hold several views (floor, building type, optional region polygon); a scale is never assumed, only a person can set one (stated, or calibrated with the measurement it came from), and the database refuses a scale with no setter or method; an excluded region needs a reason. Polygons are validated (three or more finite points inside the page). All changes need the edit lock and are audited; the entity-to-view link (`SheetViewRef` is still free text) is the next step toward the viewer.
  - *1g:* review operations (migration 0009). A person verifies, rejects or reopens an item (singly or a batch), decides its disposition (leaving one out or marking it factory-furnished needs a reason, and review status and disposition stay independent), corrects its details (changing what it is or how many marks it an override of the detector; placing it on a view or building type does not), adds an item the detector missed (attributed to a manual producer for that person and a manual task, so provenance still holds), and undoes. Every change is an append-only revision keeping before and after, enforced by a trigger; undo is itself a revision and can be undone, and "undo my last change" never touches another person's work. Each edit names the revision it was made against and a stale one is a 409, so two people cannot overwrite each other. Items now carry the view they sit on, and a view with items placed on it cannot be deleted. Only a person can do any of this; a worker cannot even hold an edit token.
  - *1h:* findings and rules (migration 0010; the rule table is `ductwork.InterpretationRule` because `Rule` is a reserved word in T-SQL). A rule is data with a scope (job, building type, sheet, or instance-wide), a match and an action run by the existing deterministic engine; its content never changes (a trigger enforces it, and rules cannot be deleted), a change is a new version, approving a version retires the one in force, and retiring needs a reason. A person writes, previews (draft or saved), approves, applies and retires; only an approved rule changes a count. Applying is recorded item by item and can be undone; undo skips any item changed since. Precedence is enforced: a person's override stands over any rule, a stronger scope is never overwritten by a weaker one, and two rules of the same strength that disagree raise a finding instead of a silent winner. Hand-added and rejected items are left alone. Changing the instance rule library needs a Job Manager or an administrator. A finding is closed by an approved rule that has actually decided its item, by an override (the item was changed or rejected), or by a waiver with a written reason, and can be reopened. The count sheet lists open findings and is not final while any is open.
  - *1i:* the worker process skeleton (`AIToolbox.Ductwork.Worker`, a Windows service or console app that references only the Contracts). It handshakes (stopping without claiming if the contract is incompatible), claims up to a configured number of tasks, runs each in its own scratch folder with a heartbeat that keeps the lease and learns of cancellation, and reports a result, or a retryable or non-retryable failure; a stopping worker hands its tasks back, and a `drain` file lets it finish and exit for an upgrade. Heavy work runs in a child process with an empty environment (the API key never reaches it), the scratch folder as working and temp folder, a timeout and process-tree kill. Drawings are downloaded through two new worker endpoints (task input, file content; only for the worker holding the task, only for that task's job) and checked against their SHA-256. Handlers: the residential label detector (runs the Python sidecar per in-scope plan sheet and merges results, refusing early if the sidecar's producer differs from the binding) and a fake handler for exercising the path. 37 worker tests, and a contract test in the web tests reads the real server's answers with the worker's models. Still to build: per-task memory and scratch-size caps, the other detector handlers, and the instance registry and per-instance key provisioning screen.
  - *1j:* the LLM gateway and hosted-vision governance (migration 0011, plan section 6.12). The administrator's switch is off by default and is a versioned, append-only policy (allowed models, who may approve, the data-governance notice with a version, page and image limits, approval lifetime, optional standing approval for named task types with a per-job page limit). A person with the right to approves listed sheets for one model for one run, after being shown the sheets, the model and route, the budget and the notice; the approval expires, is used by one task, and can be revoked. A task that sends drawings cannot be queued without a live approval for its own model (or a standing one), and the database now enforces the link. Workers hold no keys: they ask `POST /api/v1/ductwork/worker/tasks/{id}/vision`, and the gateway checks the switch, the task and its lease, the approval, the model, the sheets and the image budget, reserves the images before anything leaves, finds the approved model by its snapshot (an edited provider means nothing is sent), and records counts and hashes only (never images or prompts). A send refused before it left the server gives its images back; one that may have left stays counted. Switching the capability off revokes unfinished approvals, cancels vision tasks and makes the gateway refuse at once. The worker has a client for the call that never retries a send. 20 new core tests, 14 web tests, 3 worker tests.
  - *1k:* the workbench and viewer UI (`/Ductwork`, plain ES modules under `wwwroot/js/ductwork`, no bundler, built on the site's Bootstrap). Jobs list; a job page with tabs for Overview (details, review level, progress, members, tags, lifecycle, time on job), Drawings (chunked resumable upload with per-piece hashes, pages recorded automatically by reading the PDF in the browser, sheet details), Setup (building types and multipliers, views and scales), Detection (run enabled kinds of work, live progress), Review, Questions and rules, Count sheet (CSV and print), Hosted vision (disclosure, approval, revoke, what was sent) and Audit (filter, verify the chain); and an admin page for enabled task types and the vision policy. The lock is taken by the first edit or the Start editing button, kept alive by a heartbeat, handed over or taken on request, and a lost lock is reported rather than silently retried. The viewer draws the sheet with pdf.js (vendored 4.10.38, Apache-2.0, served from the site) instead of a tile pipeline, which is enough for the drawings seen so far and needs no server-side rendering; boxes are drawn on a canvas over it in PDF points on the displayed page, which is the space the detectors report in (so a sheet's recorded width and height are the displayed ones, after rotation). Review has a tree by building type and kind of item, filters, keyboard review (J/K/V/X/U), bulk verify of chosen or high-confidence proposals, correction, disposition, undo, and tools to add a missed item, calibrate a scale from two points, and mark an area to leave out. Verified without a person: 29 Node tests of the pure logic (geometry, grouping, the lock, the uploader, the API client), and a browser run in headless Edge against a mock of the API that loads every tab with the real scripts and real pdf.js, draws a real PDF with its overlay, and exercises verify, add, upload, multiplier, rule and approval flows; plus a test that every URL the scripts call is a route a controller really has. Not yet done: a real session against a deployed site, listing more than 5,000 items per job, and Excel and PDF report files.
  - *1l:* the worker registry (migration 0012, `ductwork.WorkerInstance`). A worker that completes the handshake is recorded (name, host, build, contract, task types, concurrency) and kept current by a cheap, rate-limited touch on heartbeat, claim and result, so the table is bookkeeping about workers only and never about jobs. Status: Online (heard from in the last 60 s), Draining (asked to stop and still holding work), Drained, Offline. An administrator can ask a worker to drain before an upgrade; this is enforced on the server (a draining worker is refused work with 204, and a re-handshake does not clear the request) and the worker also polls `GET worker/status` so it stops and, with ExitWhenDrained, exits when idle. Forgetting is allowed only for a worker that is gone and holds nothing. An administrator creates a worker key from the Workers card: it makes an API consumer and a key holding exactly `ductwork:worker` and `tool:DuctworkTakeoff`, shows the key and a ready settings snippet once, and audits that a key was issued without ever logging it. Any user sees only counts (`workers/summary`), used for a Detection-tab banner when no worker is connected so queued work does not just look slow. Handshake, drain, forget and key issue are audited; the touch is the one pinned `[NoAudit]` write.
  - *1m:* archive, purge and restore (migration 0013, section 8.3). A job that is Complete or Closed can be archived as a checked copy (zip only) or archived and purged. A request freezes the job (Archiving) and queues a server-run operation; it runs in recorded steps (Write, Verify, Purge, or Locate and Import for a restore) under a lease, so a restart or a dead runner is picked up and finished, never left half-done. The zip is format version 1, versioned apart from the schema: manifest.json (hash of every part, row counts, a hash over all detail rows, the audit chain head), data/*.jsonl (the heavy rows, in key order), reference/*.jsonl (rows that stay in SQL), provenance/*.jsonl (the producers and provider snapshots the job points at, copied in), tags.json, summary.json and sources/ with every source file. Nothing is removed unless the zip has been read back from where it now is and proven: whole-file hash, every part's hash, row counts, every reference resolving, and a hash of what is in SQL equal to the hash in the zip with the job frozen. A purge then deletes in resumable batches (item history through one narrow trigger exception that needs the job Archiving and its archive PurgePending), and a half-purged job is still restorable because the zip was proven first. Restore refuses over live rows, checks every source file against its hash, inserts with the original ids (bulk copy with constraints checked), compares the restored rows to the archive hash inside the same transaction, and rolls everything back if they differ. A missing zip stops with 'Archive file not found. Contact your administrator.' and is recorded as a problem on the Archives page. Reopening marks earlier archives out of date and never deletes them. A purge keeps the job record, members, audit trail, tasks, building types, rules, vision records and tags (rules and vision records because they are immutable, so two rule references to sheets and findings became soft). Job Manager is now grantable (admin page, audited). Owners archive and restore their own jobs; Job Managers any job from the new Archives page, seeing only the master record. Admins can check an archive, relink a moved one (hash-verified) and retry a stopped purge. Departures from the plan, on purpose: the work runs on the server, not as a worker task, because a worker is never given the database or the stores; restore keeps original ids instead of remapping by PublicId, because the rules that stay in SQL point at sheets by id; the Complete-to-Archived audit categories are kept whole instead of selectively. Not done: moving a zip to secondary storage (relink covers a move made outside the app), a golden zip per released format version (there is only version 1), and a real purge of a large job on production-sized data.
  - *1n:* count sheet files (migration 0014, section 12). A neutral report model (`ReportModel`: job header, the count sheet, rules applied, the detail list with evidence, job-level time) drawn from the job's effective state, with a hash of its content, rendered by `ExcelReportRenderer` (ClosedXML: Summary, Count sheet by building type, Job totals, Holds, Detail, Rules applied) and `HtmlReportRenderer` (one self-contained page that prints cleanly). Quantities are numbers, extended quantity is computed, units are never mixed, and an unknown quantity reads TBD, never zero. Anything from the drawings is written as text (a leading = + - @ is marked as a quote-prefixed text cell and a test confirms the workbook has no formula) and HTML is encoded and carries a policy that forbids scripts. A draft is drawn on demand from the job as it is, stamped as a draft, never stored. Issued reports are made while a job is Completing, stored in the artifact store with their hash and the model hash (so the two formats provably came from one state), recorded in an immutable `JobReport` table, and served exactly as stored after the hash is checked. Finishing completion issues them first, so a failed run leaves the job Completing (the Overview offers 'Finish completing') and a job is never Complete without its reports. A report that is not final says so in its own first line and names why. Metrics are job-level aggregates only. Departures from the plan: rendering runs in the web app, not as a worker task (the worker has no database; the cost is bounded by job size and the renderer sits behind `IReportRenderer` so it can move); no PDF renderer yet (print the HTML, or the on-screen sheet); issued report files are not bundled into the archive zip and stay in storage with their rows. Not done: PDF, a marked-up drawing PDF, the audit and exceptions list as its own output, per-user time for Job Managers.
  - *1o:* sheet identification (the first Phase 2 piece the detectors need). The sidecar's `sheets` command reads every page of a PDF in canonical space (after /Rotate, so a sheet stored sideways reads like an upright one) and reports sheet number, title, discipline, kind and a content hash: the sheet number is the largest sheet-number-like text in the title-block strip, the title is the stacked block of text nearest above it that carries a title word, the discipline comes from the number's prefix, and demolition sheets are kind Other. The content hash covers the drawing area only (words and shapes, with the title-block strip left out), so the same drawing re-issued under another project name, number or date hashes the same, and it does not depend on how the page is stored. A page with no text layer has its title-block strip read by local OCR (the strip is rendered upright for the number and turned a quarter turn for a title written along the edge; the narrow strip is tried before the wide one because the notes column beside it outshouts the title; run-together OCR text is re-spaced only when it is made entirely of known title words). In the worker, `SheetIdentification` runs the command per file, records only pages not yet recorded (a retry does the rest) and skips copies of files; `identify-sheets` is a handler for it, and the residential detector now reads the sheets itself when a job has none, then counts only mechanical, in-scope, non-duplicate plans. The server records a page whose discipline is plainly another one (electrical, plumbing, architectural) as out of scope; a person can bring it back. Measured (identification was not tuned on the holdout): Limestone, the audited 27-sheet set, 27/27 sheet numbers and 27/27 titles; Lex (holdout) 7/7 numbers, titles plausible; Redwood live-text plans 30 files, every mechanical plan found as a Plan and nothing else (11 of 11 expected), electrical sheets identified as Electrical; Whiteland text-less bound set 12/12 sheet numbers by OCR (they agree with the live-text re-plots), 8 of 8 mechanical plans recognised as plans with their building names, 6 to 30 s a page. No two pages in these samples share a content hash, so duplicate grouping is proven on synthetic pages only (the bound set and its re-plots differ as vectors). Live-text reading of the 27-sheet set takes about 55 s, mostly extracting vector drawings. Not done: more than one view per sheet, an `identify-sheets` binding (a task type needs an evaluation run to be bound, so the residential task does this step itself for now), reading a sheet number that OCR misreads.
  - *1p:* views on a sheet. A CAD sheet often carries several views, each with its own scale, so the scale belongs to the view. The sidecar's `views.py` takes each scale label (a printed scale, or NTS / NO SCALE standing alone) as an anchor: the view's title is the text block directly above the label, its number is the bubble beside the title, its scale is converted to real length per drawn length (1/4" = 1'-0" is 48, which is what the Setup tab and the scale endpoint already use), and its kind comes from its own title or else from the sheet's kind. A sheet with one label is one view over the whole drawing area; a sheet with several is divided: labels group into columns, each row runs from one label to the next on whichever side the view sits (decided by where the line-work is hard against the labels), and the cut between two columns is the widest empty stretch of line-work between them. 'NTS' as a word in an abbreviations table is not a label (a label stands alone), and an electrical or architectural page gets no views. The views travel with the sheet: `NewSheet` carries them, and registration creates them in the same transaction as the page, with the scale stated and the reader named as who set it, extraction method Geometry, and no views for a page that repeats one already on the job. A view's number leads its title ("3. ROOF DETAIL"). The viewer now outlines each view that has an area of its own with its title and scale, has a Views toggle, and lists the views as links that bring one into view. Measured: Lex four-view sheets divide correctly (checked on the rendered overlay), Limestone floor plans are one view each, the unit-plan sheets give one view per unit with the unit name (6 on M2-05), detail sheets give one per detail (5 on M4-01, 8 on M4-00), Redwood plans one each. Known weaknesses: a calculation line above a label on a Redwood notes sheet is taken for a title; the area of a view is a bounding box between neighbours, not its true outline, and can take in a notes column (a person can redraw it); a scale written as a ratio (1:50) or in metric is not read; views on a text-less page are not found yet.
  - *1q:* schedule reading (detector family A). `schedules.py` reads equipment schedules from where the text sits, not from ruled lines (so a table drawn without rules reads the same, and no line-work is analysed, which is slow on a sheet with a hundred thousand paths): a title containing SCHEDULE in larger type, the MARK (or TYPE MARK, TAG) header under it, columns taken from the bands of the page that hold data in some row, names taken from the header text over them (stacked header lines and a header over several columns are folded in, and a header over two columns is split a word each), wrapped cells kept with their row. Side-by-side tables are told apart by pairing titles with mark columns so each title sits nearest the middle of its table's header row (one title over two MARK words is one table with a second mark column), and a MARK that belongs to a table further down is not taken for this table's header. A row gives mark, description, location, maker, model, capacity (airflow, tons, kW and so on with units), size and a quantity only if the schedule has a quantity column. A schedule says what a mark is, not how many there are, so marks are then counted on the plan sheets: a tag is a short label, written on one line or stacked in a bubble (prefix over the rest, as `EUH` over `S1-A`), and a sentence in a note that names the mark does not count. `ScheduleAssembler` turns that into proposals: a schedule quantity is used as written and a plan count that disagrees raises a question, otherwise one proposal per plan sheet with the tags counted there (so a typical unit plan gives the per-building quantity the multiplier is applied to), and a scheduled mark found on no plan is listed as to be determined, quantity not counted, with a question. `DetectionProposal` gained optional Manufacturer and Model, which the ingestor stores (schema regenerated; additive). The worker's `read-schedules` handler runs the two sidecar commands per file and reads the sheets first when the job has none. Measured against the engine's audited reference on Limestone (not the holdout): 69 of 69 scheduled marks read, none extra, 0 field differences in maker, model and capacity, and tag counts agree for 67 of 69 (the two that differ are tags that are not in the text layer on their sheets). On the Lex holdout all nine schedules read (25 rows, including the grille and diffuser schedule whose header is TYPE MARK). Reading the four Limestone schedule pages takes about 1.5 s. Not done: tags set at an angle, a quantity from the typical-unit multipliers (that is the building types' job), air-device quantities by counting symbols, schedules with no MARK-like first column, and a `read-schedules` binding (a task type needs an evaluation run before it can be queued).
  - *Not yet built:* detector families B and D, symbol and air-device counting, vision on crops, views on text-less pages, a PDF report renderer, SignalR progress, the calibration loader, and requirement rules (Phase 4).

**Phase 2: ingestion and sheet setup (L).** Split, rasterize, tile, extract vectors to the contract; our own sheet classification (rotated title blocks, mechanical-only filter, several views per sheet); content-hash deduplication of files and sheets across building files; scale calibration per view; sheet and view setup UI; building-type setup with suggested multipliers the user confirms. **Exit:** synthetic fixtures pass (ducts, elbows, symbols; rotation, y-flip, scale, tolerance); all 20 Sample C plan files classify correctly with the duplicate sheets grouped; the Phase 0 thresholds are met on real sets. **Risks:** rotated pages and CropBox, mixed raster/vector pages, huge vector counts, duplicate detection across near-identical sheets.

**Phase 3: detectors, symbols and coverage (L).** The wrapped engine as detector families A and B; the residential label and flag-note counter (family C); symbol-by-example with the instance symbol library and copy-to-tenant-library (D); vision and OCR on crops (E) where needed; `schedule-extract`, `legend-propose`, `crop-classify`; CFM-only terminals, two-dimension boot labels, symbol-only items and reference disagreements raised as findings, with the interpretation-rule and override workflow (section 6.11); schedule-versus-count reconciliation; unclaimed-geometry red overlay. **Exit:** per-type and per-building-type precision and recall reported by an eval run and recorded against the binding; Sample A parity still holds. **Risks:** symbol variance across firms; an embedding or ONNX fallback may force a GPU decision.

**Phase 4: notes and rules engine (M).** `notes-to-rules` emitting the constrained schema (requirement rules; interpretation rules arrive in Phase 3); rules review UI; deterministic evaluator in Core (add-child-per-parent, allowances, attribute assignment, scope/exclusions); derived leaves. The engine's Sample A-specific logic and the estimator's purchasing rules, technical gates and intake template become data-driven rules and directives, not code. **Exit:** the evaluator is pure and property-tested and only approved rules reach it. **Risks:** schema too narrow or too loose; conflicting rules (define precedence up front).

**Phase 5: review, refine, count sheet and archiving (L).** Edit tools (add/delete/reclassify/resize/merge/split, lasso, bulk), undo, review queue, per-sheet sign-off, measure tool, holds and RFIs, **the count sheet and its supporting outputs** (Excel and PDF as tasks), correction logging into the audit log and gold-set candidates; **archive, purge and restore**, the Archives admin page (move, relink, missing-file alert), round-trip and golden-zip tests. **Gates:** the estimator completes a real job faster than the manual process, measured by the activity log (section 10.1) against a baseline timing taken on one real job, with no pass/fail number invented in advance; overlay registration holds across zoom and rotation; edits survive reload with undo; dependent rows are updated or flagged stale; the screen, Excel and PDF show the same effective counts; holds and unknown quantities stay visible and cannot become silent zeros; the holdout is run once. **Risks:** count-sheet fidelity against estimator expectations; archive importers must be maintained for every shipped archive version.

**v2.** P21-importable file and catalog or part-code mapping (fitting plus size to P21 items), addendum diff, layer/system mapping, duct-network graph reconstruction, fitting inference, cross-view linking, MCP tools over takeoff data (`get_job_totals`, `list_fittings_by_floor`; hosting side undecided, since AIToolbox is currently only an MCP client), cross-tenant symbol sharing.

---

## 15. Open items

**Resolved by the standing rule "the user arbitrates, and the tool flags and displays the discrepancy well".** These need no answer up front, and each becomes a finding the user resolves with a rule or an override (section 6.11):
- The baked-in markup in eight Sample C files (green diamonds, purple routes, pink lines): treated as ordinary drawing content, never trusted as labels. If it interferes with a count, it becomes a finding.
- Two grilles per pass-through flag, and why Type 6 has 4 pass-throughs against 3 flags: an interpretation rule plus a finding on the disagreement.
- A two-dimension boot label (`4x12`) and its depth, and CFM-only terminals: findings by default, with an interpretation rule the user may create at any scope.
- Type 7 present in the plans but not in the worksheet: building-type setup suggests it and the user confirms or excludes it.
- Other designers' drawing conventions: handled by rules at the scope where they apply.
- Hosted-vision scope: per-run approval by default, with an administrator-granted standing approval for named task types (section 6.12).
- Accuracy thresholds: replaced by the configurable review level (section 6.13). No threshold is invented.

**Still wanted from the user, not blocking.** A baseline manual takeoff time on one real job (timing it by hand, or from a trial of the tool's activity log, section 10.1) and, when available, another completed commercial job with human counts, since Sample B is sealed.

**Needs the company.** The legal view on PyMuPDF (AGPL, or a commercial license from Artifex). Whether per-user time-on-job reporting is acceptable (section 10.1 defaults to job-level aggregates).

**Deferred.** Cross-tenant symbol sharing. Where v2 MCP tools are hosted. The P21-importable file.

**Verified from public sources, with limits.** .NET 10 lists Windows Server 2019 as supported. PyMuPDF is dual-licensed (AGPL or a commercial license from Artifex), so the decision is legal, not technical. PDFsharp and MigraDoc are MIT and free per their official site. ClosedXML (MIT) and PdfPig (Apache-2.0) are from secondary sources and should be confirmed in their repositories. QuestPDF's community license is free only below US$1M annual revenue.

**Still to verify.** Whether Foundry authenticates by API key or Entra only; what the EUR 750 PDFsharp quote actually covers; Edge/Chromium availability for headless printing on the worker VM; whether SharePoint access can be `Sites.Selected` per tenant; one real chunked-upload test through YARP; P21 catalog conventions for the later import file (the P21 quote's line columns are Item ID, UOM, Unit Size, Unit Price and Ext Price; P21Toolbox connectivity code not read); the Sample A title block.

---

## 16. Existing code issues noticed (out of scope)

- `GetActiveDirectiveAsync` returns an arbitrary first active directive when several exist.
- Startup requeue of "active" jobs is not lease-safe across overlapped IIS recycles.
- Provider `ModelName` edits rewrite the apparent model of historical ProductParser jobs.
- `FileStorageService` buffers entire uploads in memory.
