# HVAC Ductwork Takeoff module: discovery findings and implementation plan

Status: **Steps 1 and 2 complete. Decisions D2-D15 answered (see Part 7, which supersedes the recommendations in Part 5 where they differ). D1 (sample drawings) still open. Awaiting explicit go-ahead before any Phase 0 work (Step 3).**
Basis: read of `AIToolbox.Web` (net10.0), `AIToolbox.Web.Tests`, publish profiles, and the sibling `RingCentralProjects` repo for worker precedent. Everything below is from code unless marked **[unverified]**. Nothing was built or run.

---

## Part 1. Findings

### 0. Premise corrections

| Brief said | Code says |
|---|---|
| AIToolbox is net48 | **net10.0** (`AIToolbox.Web.csproj`, test project, `global.json` in sibling repo pins SDK 10.0.400). The NuGet-TFM constraint largely disappears: any current .NET library is usable, including PdfPig, QuestPDF, ClosedXML, ONNX Runtime. Remaining library risk is **licensing**, not target framework (see D9). |
| "defined AI tools" | In AIToolbox a *Tool* is a **feature-registry row** (`dbo.ToolDefinitions`: key, enabled, access mode, allow-list, one active provider). It is **not** an LLM function-calling tool. See 4. |
| "swappable model definitions" | `dbo.AiProviders` rows. **Mutable in place**, which matters for provenance. See 3. |

### 1. Job handling: in-process only, no parent/child, no progress, no retry

- `dbo.ToolJobs` is flat: `ToolKey, JobType, UserEmail, TenantId, IsPublic, ToolDirectiveSnapshotId?, ToolUserDirectiveSnapshotId?, Status, RequestJson, ResultJson, ErrorMessage, Created/Started/CompletedUtc`. No parent id, no progress, no attempt count, no lease/heartbeat, no provider id. Status is a free string.
- Execution is `Channel<int>` (`ProductParsingJobQueue`, SingleReader) drained by a `BackgroundService` **inside the IIS worker process**. Cancellation is an in-memory `CancellationTokenSource` plus a `CancelRequested` status.
- "Resume" = on startup, re-enqueue every job in an active state (`GetActiveToolJobsForToolAsync`). That is not a lease: during an IIS overlapped recycle, or with two instances, the same job can run twice.
- `JobsController` is hard-coded to ProductParser (`canCancel`, `GetTitle`). The navbar Jobs menu polls `/Jobs/InProgress`, so a takeoff job would show there only if we generalize those two spots.
- `dbo.AiBatchJobs` + `AiBatchRequests` is the only parent/child structure. It models **provider batch APIs** (submit, poll, retrieve), not general work. It is useful later for cheap bulk crop classification, not as our job framework.
- Call-entity extraction has a second in-memory channel queue (same pattern).
- No Hangfire/Quartz in AIToolbox. (The P21Toolbox repo uses Hangfire, per my notes. Not verified here.)

**Verdict:** nothing here satisfies parent/child, per-job progress, retry/resume, or out-of-process execution. We build a takeoff job table. We reuse only the *user-facing* surface (Jobs menu, SignalR hub pattern).

### 2. Directives: versioned by snapshot, but not schema-enforced

- `dbo.ToolDirectives(ToolKey, Name, SystemPrompt, TargetSchemaJson, Version, IsActive)`. **`Version` is a manually-edited int; edits overwrite in place.** There is no history table.
- Immutable history exists only through `dbo.ToolDirectiveSnapshots` (content-hashed, unique on `ContentHash`), created at **submission time**. A job pins one by `ToolDirectiveSnapshotId`.
- **Gaps against requirement 4:**
  - `ToolJobs.ToolDirectiveSnapshotId` is **nullable**.
  - The snapshot hash function lives privately in `ProductParsingService`. Provenance is by convention, not structure.
  - `GetActiveDirectiveAsync` is `FirstOrDefault` over all directives. Several active directives per tool are allowed but ordering is undefined. ProductParser works around it with a user-selected directive id.
- **`TargetSchemaJson` is not a JSON Schema.** The seed shows an example object with nulls, pasted into the prompt text. Responses are only `JsonDocument.Parse`d. No schema validation or provider structured-output (`response_format`/forced tool) was found. A "constrained schema" for `notes-to-rules` needs new work.
- Directive admin UI (`DirectivesController`) edits are free-form. Nothing gates activation on an eval run.

### 3. Model definitions: no vision, no capabilities, no per-task override

- `AiProviders`: type enum (OpenAiCompatible, AzureOpenAi, Anthropic, OpenAiResponses, +Background, +Batch variants), endpoint, **model name**, api version, encrypted key (ASP.NET Data Protection), extra headers, `MaxOutputTokens`, timeout.
- **Mutable:** editing `ModelName` rewrites history. `ProductParseJob` stores only `AiProviderId` and displays model name by join, so past runs silently change identity. For requirement 4 we need an **immutable provider snapshot** (type, endpoint host, model, api version, max tokens; never the key).
- No capability flags (vision, PDF, structured output, tools) and no temperature/top-p.
- **Vision input does not exist.** `AiProviderClient.SendDocumentPromptAsync` sends PDFs only: `input_file` (OpenAI Responses) or Anthropic `type:"document"`. The media type is passed through, so a PNG crop would be sent as a `document` block, which Anthropic should reject (it needs `type:"image"`). The default branch pastes base64 into prompt text. `AiChatClient` (Microsoft.Extensions.AI) maps only `TextContent` and `FunctionCallContent`. **Image-crop input must be added.**
- **Per-task override is not supported.** `ToolProviderAssignments` has a unique filtered index: one active provider per `ToolKey`. Per-task models (e.g. `sheet-classify` vs `crop-classify`) need a new binding concept (D6).
- Provider keys are decrypted with Data Protection keys that live on the web host. Any LLM call from a separate worker either needs those keys or must go through the web app (D4).
- Usage/cost: `ToolUsageLog` + `ModelPricing` exist and can be reused for takeoff LLM spend.

### 4. "Tools": registry vs LLM function tools

- **Registry (what the brief likely means by "defined tools"):** `ToolDefinitions` + `ToolUserAllowList`. Registering a new tool today takes **four hand-edits**: a seed `INSERT` in `DatabaseInitializer.SchemaSql`, an authorization policy in `Program.cs` (`Tool:ProductParser`), the card link switch in `Views/Home/Index.cshtml`, and (for API) scope `tool:{ToolKey}` (data-driven). The Serilog per-tool log sink is also hard-coded in `Program.cs`.
- **LLM function tools:** only in the PicSearch chat agent loop. MCP tools come from the **P21 Toolbox** MCP server via `ModelContextProtocol.Core` (AIToolbox is an MCP *client*, and the package is `.Core`, not `.AspNetCore`, so it cannot host an MCP server today). `AiChatClientFactory` wraps `FunctionInvocation` with `MaximumIterationsPerRequest = 10`. The loop supports OpenAiCompatible, AzureOpenAi and Anthropic only. There is no registry a worker or sidecar could call.
- **Consequence:** the v2 takeoff MCP tools (`get_job_totals` etc.) need a decision on which side hosts the server (D10). A takeoff worker has no existing tool mechanism to reuse, and the extraction/rules work doesn't need one.

### 5. Storage and upload limits end to end

| Layer | Finding |
|---|---|
| `FileStorageService` | **PDF only** (extension, content-type, `%PDF` header). **Buffers the whole upload into a `MemoryStream` and returns `byte[]`** (later base64'd to the LLM). Writes `{BasePath}/{ToolSubpath}/{yyyy}/{MM}/{guid}.pdf`. No hash, no chunking, no download, no tenant/project folder. |
| `Storage:MaxUploadBytes` | 15 MiB (`appsettings.json`). |
| Controller attribute | `[RequestSizeLimit(50 MiB)]` on ProductParser `Parse` and its API; 100 MiB on batches. Kestrel default is ~28.6 MB when no attribute. |
| Form limits | `FormOptions` defaults (multipart 128 MB). Not customized. |
| IIS | Deployed via MSDeploy to IIS app `stdair.com_aitools/aicompare`. **No `web.config` in the repo** (SDK-generated), so IIS `maxAllowedContentLength` (default ~30 MB) applies unless the server was changed. **[unverified on server]** |
| Reverse proxy | Unknown from code. Two public hostnames appear in config (`aitools.stdair.com/aicompare`, `stdair.ai.p21toolbox.com`). **[need user to confirm proxy limits/timeouts]** |
| Hosting path base | App runs under **`/aicompare`**. All JS/tile URLs must be built with `Url.Content("~/…")`; `Jobs/Cancel/__id__` already does this. |

**Verdict:** 50–100 MB plan sets fail at IIS (and the 15 MiB option) before reaching the app. Needs a chunked-upload endpoint writing straight to disk, hash-verified, plus a separate large-capacity storage root. Tiles must be served through an authenticated controller (`PhysicalFile` with ETag/immutable caching). Static assets (`MapStaticAssets`) are unauthenticated.

### 6. Tenancy, auth, and where a module plugs in

- **Auth:** Microsoft 365 OIDC cookie (`.AIToolsCompare.Auth`). A policy scheme also accepts API keys (`X-AIToolbox-Api-Key` / `Bearer aitb_…`) and Entra JWTs on `/api`. Global `AuthorizeFilter("ActiveUser")`. Allowed Entra tenants: Koch Air and Standard Air (two orgs **share one database**). `AuthorizedUsers` carries email, TenantId, IsAdmin.
- **Tenancy model:** `TenantId` is stored on jobs but **queries filter by `UserEmail`/`IsPublic`, not tenant**. There is no per-customer data partition. The deployment itself is the tenant boundary (config such as `ExpectedAudience: stdair.ai.p21toolbox.com` is per-tenant), which matches "deployment is per-tenant". Brief item "symbol libraries persist per tenant/customer" therefore maps to a **deployment-wide library with project overlays**, not a tenant key we can reuse (D8).
- **Project-level access:** only `IsPublic` bool exists. Takeoff needs real project membership (owner/editor/viewer).
- **Machine auth we can reuse for the worker:** `ApiConsumers` + `ApiConsumerKeys` (hashed, scoped, revocable, `tool:{ToolKey}` scope + `KnownApiScopes`). Precedent for a Windows-service client using it: `RingCXAgentHubAPI` calls AIToolbox with `X-AIToolbox-Api-Key`; `RingCXStreamListener` is a .NET Windows service (`UseWindowsService`).
- **Real-time:** SignalR is wired (`CallMonitorHub`, AdminOnly). Can host a progress hub.
- **UI conventions:** single flat navbar (Admin Dashboard + Jobs menu); tools are cards on Home. **No Areas, no bundler, no libman, no npm.** `wwwroot/lib` holds only bootstrap/jquery, and JS is hand-written vanilla (`site.js` 847 lines). Leaflet would be vendored by hand.
- **Framing:** `FrameGuardMiddleware` sends `X-Frame-Options: DENY` + `frame-ancestors 'none'` on every path except `/embed`. **A same-origin iframe panel is blocked today.** The only iframe pattern is the strict-CSP P21 embed (no inline script/style). Recommendation: a normal Razor page hosting the viewer, **no iframe**.
- **Persistence conventions:** hand-written ADO.NET repositories; schema is a single ~1,200-line idempotent T-SQL script run at startup (`DatabaseInitializer`). No EF, no migration framework. Test project is xUnit referencing the web project.

### 7. Items that conflict with the brief's architecture

1. **Provenance is by convention today** (nullable snapshot FK, caller-computed hash, mutable provider rows). We must not reuse `ToolJobs` for takeoff runs.
2. **Provenance model is too narrow for non-LLM producers.** Vector extraction, geometry and template matching have no directive or model. Replace "directive@version + model definition" with a **Producer** that is exactly one of: LLM (directive snapshot + provider snapshot), Algorithm (name + version + params hash), Manual (user). Enforced by a CHECK constraint (D5).
3. **No image input** to LLMs (see 3). Required for crop classification.
4. **No capability model**, so we cannot refuse binding a non-vision model to `crop-classify` without adding flags.
5. **Upload path cannot carry the files** (see 5).
6. **LLM keys live only in the web host**, so a worker needs a gateway (D4).
7. **Directive/provider admin screens edit live rows**, which bypasses the "no directive change or model swap without an eval run" rule. Takeoff bindings must pin **immutable snapshots** and be changed only through a gated path.
8. **Startup SQL script will not scale** to ~15 new tables (D7).
9. **iframe/viewer pattern**: not available. Use a plain page.

---

## Part 2. Recommended placement

```
AIToolbox.Web                      existing: auth, admin, tool registry, providers, directives, usage, SignalR
  Areas/Takeoff/                   Razor UI, controllers, viewer JS (vendored Leaflet), ingest + LLM-gateway API
AIToolbox.Takeoff.Contracts        net10 class library, zero dependencies: detection-result contract, job payloads,
                                   Producer/Provenance types, generated JSON Schema (source of truth = C#)
AIToolbox.Takeoff.Core             net10 library: deterministic rules engine, aggregation, reconciliation, report model
                                   (pure logic, no web/CV deps, heavily unit-tested)
AIToolbox.Takeoff.Worker           net10 Worker Service / Windows service: claims jobs, runs pipeline steps, calls the sidecar
takeoff-sidecar/ (Python)          optional, vector/CV compute only; speaks the JSON contract; no shared logic
AIToolbox.Takeoff.Tests            xUnit + synthetic CAD-PDF fixtures with ground-truth JSON
```

Why an **Area inside AIToolbox.Web** and not a separate web app: authentication, tool access, ApiConsumer keys, providers, directives, usage logging, Jobs menu and Admin all live there. The Area keeps `Views/Controllers/wwwroot` self-contained. Constraint kept: **AIToolbox.Web references only Contracts and Core**, never the worker, CV or PDF-processing packages.

New SQL: a dedicated **`takeoff` schema** with an ordered, versioned migration runner (embedded `.sql` files + `takeoff.SchemaVersion`), separate from `DatabaseInitializer`.

---

## Part 3. Data model (Phase 1 target)

Provenance and supersession are structural from the first migration.

```
takeoff.Producer            (immutable)  ProducerId PK, Kind {Llm|Algorithm|Manual}, DirectiveSnapshotId?, ProviderSnapshotId?,
                                         AlgorithmName?, AlgorithmVersion?, ParamsHash?, ManualUserEmail?
                            CHECK: columns required/forbidden per Kind; UNIQUE on natural key so rows dedupe.
takeoff.ProviderSnapshot    (immutable)  type, endpoint host, model, api version, max tokens, ContentHash UNIQUE. Never stores the key.
takeoff.TaskBinding         ToolKey='HvacTakeoff', TaskKey (sheet-classify | schedule-extract | legend-propose | crop-classify |
                            notes-to-rules), DirectiveSnapshotId NOT NULL, ProviderSnapshotId NOT NULL, EvalRunId NOT NULL, ActiveFrom
Project                     owner, name, status;  ProjectMember (role)
DrawingSetRevision          ProjectId, RevisionNo, SupersedesRevisionId?    <- addendum diffing later
SourceFile                  RevisionId, Sha256, StoragePath, Bytes, ContentType
Sheet                       RevisionId, SourceFileId, PageNo, SheetNumber, Floor, ViewType, Discipline, ScaleRatio, ScaleCalibrationJson,
                            CoordinateSpaceJson, SupersededBySheetId?
SheetRegion                 SheetId, Kind {Include|Exclude}, Reason, PolygonJson
Entity                      EntityId, SheetId, ProducerId NOT NULL, JobRunId NOT NULL, Type, Size, Floor, View, Source {Vector|Detector|Vlm|
                            Manual|Derived}, Confidence, Status {Proposed|Verified|Rejected|Manual}, BBox (4 float cols, indexed),
                            PolygonJson, ParentEntityId?, DerivedFromRuleId?, SupersedesEntityId?, SupersededByEntityId?
EntityRevision              (append-only) every state change; basis for undo, audit trail, correction log
Rule                        ProjectId, SchemaVersion, DefinitionJson (constrained schema), ProposedByProducerId, Status {Proposed|Approved|
                            Rejected}, ApprovedBy, ApprovedUtc
SymbolDefinition            ScopeKind {Deployment|Project}, ProjectId?, Name, MeaningType, VectorHash, TemplateRef, SourceCropRef
SymbolMatch                 SymbolDefinitionId, SheetId, BBox, Score, Status
AuditEvent                  actor, action, subject, before/after JSON, CropRef, context JSON  (feeds the gold set)
Job                         JobId, ParentJobId?, ProjectId, JobType, Status, Progress{done,total,message}, Attempt, MaxAttempts,
                            LeaseOwner, LeaseExpiresUtc, ProducerId NOT NULL, InputManifestRef, ResultManifestRef, ContractVersion
EvalRun / EvalResult        GoldSetId, ProducerId or TaskBinding candidate, per-FittingType TP/FP/FN, precision, recall, ContractVersion
GoldSet / GoldLabel         Sheet crop refs + labels; fed by accepted corrections
```

Invariants enforced in the schema and types:
- `Entity.ProducerId` and `Entity.JobRunId` are **NOT NULL FKs**. The only entity-insert path is a repository method taking a non-default `Provenance` value issued by the job context. The worker never writes entity rows; it POSTs a contract-validated result and the web app is the single write path.
- `Producer` CHECK constraint rules out an LLM producer without both snapshots.
- `TaskBinding` requires an `EvalRunId`, so no directive or model can be activated without a recorded eval run.
- Large payloads (vector dumps, tiles, crops) live on disk. SQL holds paths, hashes and manifests.

**Canonical coordinate space (contract decision):** sheet space = PDF points, origin **top-left** after applying page rotation and CropBox, y down. The transform to PDF space is recorded per page. Leaflet `CRS.Simple` conversion (y-up, negative lat) happens only at the viewer boundary. This is where y-flip bugs get pinned by tests.

---

## Part 4. Phased plan

Sizing is relative (S/M/L) and not a promise.

### Phase 0: Platform prerequisites + spike + eval harness (M-L)
**Deliverables**
- Contract v0 (`AIToolbox.Takeoff.Contracts`): detection result, entity, producer/provenance, job payloads. JSON Schema generated from C#, checked in, with a test that fails if regenerated schema differs.
- Eval harness: job type `eval-run`, `GoldSet/GoldLabel`, P/R per fitting type, tagged with producer; CLI + admin page for results. Gold labeling tool: a minimal viewer-based labeler (reuse Phase 1 viewer later).
- Spike (throwaway, clearly marked): vector extraction on real sets (PdfPig vs PyMuPDF, D2), schedule table extraction, sheet classification via LLM on thumbnails/title-block crops.
- Platform pieces the spike needs: image input in the provider client (Anthropic `image`, OpenAI image content), `ProviderSnapshot`, capability flags (`SupportsVision`, `SupportsStructuredOutput`), structured-output/JSON-schema validation of responses.

**Uses:** `AiProviders`, `ToolDirectives` + snapshots, `ToolUsageLog`/`ModelPricing`, `ApiConsumers`.
**New:** everything under Contracts, eval tables, image path in `AiProviderClient`.
**Exit criteria:** the vector-vs-raster mix of real drawings is known and written down. Schedule extraction and sheet classification hit a threshold agreed up front (propose: ≥95% sheet-type, ≥98% schedule-row accuracy on gold set **[numbers need your input]**). A go/no-go on vector-first is recorded.
**Risks:** no real drawings, so no meaningful gold set (**D1**). If the sets are mostly scanned or rasterized, the vector-first premise weakens and OCR/detector cost enters the plan. Confidentiality of drawings sent to hosted LLMs (D3).

### Phase 1: Foundations (L)
**Deliverables:** `takeoff` schema + migration runner; data model above; Producer/Provenance types and enforcement tests; job table with lease/claim/heartbeat/retry/parent-child/progress; worker skeleton (claims via the API, heartbeats, posts results); chunked upload + `IArtifactStore` abstraction; tool registration (`HvacTakeoff` `ToolDefinition`, policy, Home card, log sink, scope `tool:HvacTakeoff`); project membership; Jobs menu generalization; viewer + tree + filters + two-way hover/selection sync (one selection store keyed by EntityId) **on fake detections**; SignalR progress.
**Uses:** auth, `ApiConsumers` for worker, SignalR, Jobs menu, Admin shell.
**Exit:** upload a 100 MB PDF through IIS and the proxy; a fake job runs end to end through the worker with retry after a killed worker; fake entities render and sync tree↔overlay at 10k+ entities (canvas renderer); no code path inserts an entity without provenance (test + CHECK).
**Risks:** IIS/proxy limits (must test on the real path); viewer performance; hand-vendored Leaflet and no bundler (keep JS modular by convention).

### Phase 2: Ingestion and sheet setup (L)
Split, rasterize, tile (pyramid), vector extraction to contract; `sheet-classify` directive and binding; scale calibration (two-point + title-block cross-check); sheet setup UI (floor, view, discipline, regions).
**Exit:** synthetic CAD-PDF fixtures with known ground truth pass (ducts, elbows, symbols; rotation, y-flip, scale, tolerance); real-set sheet classification at the Phase 0 threshold; calibration error shown to user.
**Risks:** rotated pages/CropBox, mixed raster+vector pages, huge vector counts.

### Phase 3: Schedules, terminals, symbols, coverage (L)
`schedule-extract`, `legend-propose`, `crop-classify` directives; symbol-by-example (vector hash first, template/embedding fallback); schedule-vs-count reconciliation; unclaimed-geometry red overlay.
**Exit:** per-type precision/recall on the gold set reported by an eval run, recorded against the binding; coverage overlay verified on fixtures.
**Risks:** symbol variance across firms; ONNX/embedding fallback may need a decision on central GPU (D1).

### Phase 4: Notes and rules engine (M)
`notes-to-rules` directive emitting the constrained rule schema; rules review UI; deterministic evaluator in `Takeoff.Core` (add-child-per-parent, allowances, attribute assignment, scope/exclusions); derived leaves with link to rule.
**Exit:** evaluator is pure and property-tested; **LLM output never touches counts** (type boundary: only an approved `Rule` row feeds the evaluator).
**Risks:** rule schema too narrow or too loose; conflicting rules (define precedence up front).

### Phase 5: Review, refine, reports (L)
Edit tools (add/delete/reclassify/resize/merge/split, lasso, bulk), undo via `EntityRevision`, review queue (confidence-sorted, keyboard), per-sheet sign-off, measure tool, assumptions/RFIs, floor and job reports, Excel + PDF as **worker jobs**, correction logging with crop and context into `AuditEvent` and `GoldLabel` candidates.
**Gate:** a real estimator completes a real job faster than manual (need baseline timing from you, D1).
**Risks:** QuestPDF licensing (D9); report fidelity vs estimator expectations.

### v2 (unchanged from brief, plus)
P21 catalog mapping (reuse the Direct/Relay connectivity conventions from P21Toolbox **[not read this session]**), addendum diff (columns already present), network graph reconstruction, cross-view linking, MCP tools (D10).

---

## Part 5. Decisions needed from you

| # | Decision | My recommendation |
|---|---|---|
| **D1** | **Inputs for Phase 0:** real plan sets (how many, what vector/raster mix), who hand-labels the gold set, baseline manual takeoff time, and numeric accuracy thresholds. | Provide 5–10 representative sets across at least 2 designers. Without them Phase 0 cannot gate anything. |
| **D2** | **Extraction stack:** pure C# (PdfPig) vs Python sidecar (PyMuPDF/OpenCV) vs both. | Spike both in Phase 0 on your real files, decide on measured vector fidelity. Python 3.12 is on this dev box. PdfPig is Apache-2.0 **[unverified]**; PyMuPDF is AGPL/commercial **[unverified, check before shipping]**. |
| **D3** | **Data handling:** may customer drawings go to hosted LLM providers? Which configured providers qualify? | Crops and tables only (per your design) and an admin-visible "allowed for Takeoff" flag on providers. Your call on policy. |
| **D4** | **LLM access from worker:** worker holds provider keys vs calls a web-app **LLM gateway** endpoint. | Gateway. Keys stay on the web host (Data Protection), and directive+provider snapshots and usage logging are stamped in one place. |
| **D5** | **Provenance generalization:** replace "directive@version + model" with the Producer concept (LLM / Algorithm / Manual). | Yes. The brief's wording cannot cover vector and CV outputs. |
| **D6** | **Per-task models:** new `TaskBinding` table (recommended) vs one `ToolKey` per task (would pollute the tool directory and API scope list). | `TaskBinding` with required `EvalRunId`. |
| **D7** | **Schema management:** separate `takeoff` schema + migration runner vs extend the startup script. | Separate. It also keeps the existing script untouched. |
| **D8** | **Scope/permissions:** symbol libraries deployment-wide + per-project, project membership roles, whether Koch Air and Standard Air users share projects. | Deployment-wide library, per-project membership, no cross-project visibility by default. |
| **D9** | **Licensing:** QuestPDF (commercial license thresholds **[unverified]**), PyMuPDF AGPL, Apryse/Nutrient (noted alternative only). | Confirm before Phase 5. ClosedXML for Excel is permissive **[unverified]**. |
| **D10** | **v2 MCP tools** hosted where: new MCP server in AIToolbox (needs `ModelContextProtocol.AspNetCore`) vs in P21 Toolbox. | Defer; revisit after Phase 3. |
| **D11** | **CAD formats:** PDF only for v1. DXF later (open parsers exist **[unverified]**). DWG needs vendor libraries/licensing. | PDF-only v1. |
| **D12** | **Worker topology: local-per-tenant vs central service** (you asked me to surface this). | See below. |
| **D13** | **Worker coupling:** direct SQL vs HTTP API (claim/heartbeat/result). | HTTP API over existing ApiConsumer keys. Worker has no SQL credentials, and the ingest endpoint validates the contract and stamps provenance. |
| **D14** | **Job framework:** custom lean table (recommended) vs Hangfire shared storage with a .NET worker. | Custom. Python sidecar + typed contract + lease semantics map poorly to Hangfire. |
| **D15** | **Proxy/IIS limits on the real path.** | You confirm the reverse proxy (if any) and IIS `maxAllowedContentLength`/timeouts. I plan chunked upload so this stops mattering, but it needs a real test. |

### D12 detail: local-per-tenant worker vs central service

| | Local per tenant (co-located with each AIToolbox deployment) | Central processing service |
|---|---|---|
| Data residency / confidentiality | Drawings never leave the tenant's environment | Drawings cross to a shared service. Needs tenant-partitioned storage, queues, and authorization |
| File transfer | Shared local path, no network hop (50–100 MB files) | Upload/download or shared storage per tenant |
| Ops burden | Install, patch, and monitor a worker (and Python/CV deps) per tenant | One deployment to update and monitor |
| Capacity / GPU | Each box sized for peak, possibly idle. Contention with IIS on the same host | Amortized, can add GPU for embedding/ONNX fallback |
| Failure domain | Isolated per tenant | Shared. One bad job or tenant can starve others |
| Matches current deployment model | Yes (AIToolbox, DB and config are already per tenant) | No existing precedent |

**Recommendation:** start local, co-located, as a separate Windows service with CPU and priority limits, because it matches how AIToolbox is deployed and keeps drawings in-tenant. Keep a central service possible by never letting code touch storage paths directly (`IArtifactStore`) and by making the worker talk only HTTP (D13). Revisit if GPU inference or tenant count makes per-tenant boxes uneconomic. **Your decision.**

---

## Part 6. Existing code issues noticed (not part of this work)

- `GetActiveDirectiveAsync` returns an arbitrary first active directive when more than one exists.
- Startup requeue of "active" jobs is not lease-safe across overlapped IIS recycles.
- Provider `ModelName` edits rewrite the apparent model of historical ProductParser jobs.
- `FileStorageService` buffers entire uploads in memory.

---

## Part 7. Decisions resolved, and what they change

| # | Decision | Plan impact |
|---|---|---|
| D1 | No sample drawings yet; user will supply. | Phase 0 **extraction spike, gold set and thresholds are blocked**. Drawing-independent Phase 0 work is listed below. |
| D2 | Python sidecar OK provided the project is .NET C#-based. | Worker is the C# host and owns orchestration. Python is a subprocess behind the JSON contract. Still spike PdfPig vs PyMuPDF on real files when they arrive. |
| D3 | Providers need a "vision processing enabled" tag. Vision/document-ingesting LLMs must route through **Microsoft Foundry** for governance. | Add provider capability flags `SupportsVision`, `AllowedForTakeoff`, and `GovernanceRoute` (e.g. Foundry). `TaskBinding` refuses a provider lacking the tag. The `ProviderSnapshot` records the Foundry endpoint host. Needs checking: Foundry endpoint shape against the existing `AzureOpenAi`/`Anthropic` provider types **[unverified]**. |
| D4 | LLM gateway in web app. | As planned. Worker has no provider keys. |
| D5 | Producer generalization. | As planned. |
| D6 | `TaskBinding`. | As planned, `EvalRunId` required. |
| D7 | Separate `takeoff` schema. Takeoff jobs segregated. Shared with the rest of AIToolbox: only the `ToolDefinition` row and the job kick-off/startup record. | Drop the idea of a Jobs-menu rewrite. A thin `ToolJobs` row is written at kick-off (for the navbar Jobs menu and usage), pointing at `takeoff.Job`. All progress, children, leases live in `takeoff`. |
| D8 | Libraries/symbols are **job-specific by default**, tenant-isolated, and can be promoted to a system-wide library. Promotion **copies** so deleting the original cannot affect dependents. | See "Library model" below. |
| D9 | QuestPDF Professional rejected. Prefer free or perpetual one-time licenses. | PDF report options to evaluate in Phase 5: PdfSharp/MigraDoc (MIT, **[unverified]**), or HTML to PDF via a headless browser (Chromium/Playwright, free). ClosedXML for Excel (**[unverified]**). No decision needed until Phase 5. |
| D10 | MCP hosting deferred. | Revisit after Phase 3. |
| D11 | PDF only for Phase 1 unless sample jobs are CAD. | Finalize when D1 arrives. |
| D12 | Tenant-isolated files, no spillage. Mirror P21Toolbox's storage config. AIToolbox admin configures storage options. SharePoint-backed storage desirable. | See "Storage model" below. |
| D13 | HTTP API only. Worker never touches the DB. | As planned. Micro-API style endpoints: claim, heartbeat, progress, result, artifact upload/download, LLM gateway. |
| D14 | Custom job table. | As planned. |
| D15 | Chunk settings admin-configurable. IIS now 100,000,000 bytes and YARP route matched. | Chunk size and max file size become admin settings. Chunking means the IIS/YARP limit only needs to exceed one chunk, so 100 MB is ample headroom. Still run one real end-to-end test through YARP. |

### Storage model (D12), from reading P21Toolbox

What exists in `P21Toolbox/FileStorage` and `Carrier.Core/Processors`:
- `IFileStorageProvider` with three backends: **Direct (UNC/SMB)**, **Relay (HTTPS agent)**, **SharePoint (Microsoft Graph)**. The provider is resolved **per root key** from per-tenant settings (`FileStorage_{prefix}_ProviderType`, two-tier tenant-over-system reader). `PathSafety` guards relative paths and containment.
- `FileStorage.csproj` multi-targets **net48 and net10.0**, so AIToolbox can reference it (project reference or package) instead of copying code. Decision: reference rather than fork, so path-safety and Graph fixes land once.
- SharePoint uses app-only client credentials with chunked upload sessions (5 MiB chunks).

Implications for takeoff:
1. **The interface is shaped for deliver/pickup sync**, not for random-access reads. Viewer tiles need many small, fast, ranged reads. **Plan:** the configured storage (SharePoint/Direct) is the **system of record for source drawings and generated reports**. Derived artifacts (tiles, page rasters, crops, vector dumps) live in a **local, per-deployment cache** that can be rebuilt from the source. Tile serving never hits Graph per request.
2. **Isolation:** one storage root per deployment is not enough if two Entra tenants share the database (Koch Air + Standard Air). Storage path is keyed by project and stamped with the owning tenant. Every read goes through `PathSafety.IsContainedIn` against that tenant's root, and the API (not the path) is the only way a worker gets a file. Test: a request for tenant B's artifact id under tenant A's credentials must 404.
3. **SharePoint permissions:** app-only Graph with broad Sites permissions can read every site. Recommend **`Sites.Selected`** scoped to the specific takeoff site per tenant. Needs your IT decision.
4. **Users already store jobs in SharePoint/OneDrive**: Phase 1 offers "import from SharePoint library" through Graph (picking a file, not browsing the whole tenant), in addition to direct chunked upload.
5. **Admin UI:** a Takeoff Storage tab mirroring the P21Toolbox Storage tab (provider type, base path or site/drive/path, validate-access button, secrets write-only with a "configured" badge).
6. The existing AIToolbox `Storage:*` options are PDF-only and tool-wide. Takeoff gets its own options and does not use `FileStorageService`.

### Library model (D8)

- `SymbolLibrary` and `SymbolDefinition` get `Scope` in {`Job`, `Tenant`, `System`} (a non-nullable enum, default `Job`) and `OwnerTenantKey` (NOT NULL for Job and Tenant scope).
- **Job scope** (default): visible only inside that project.
- **Tenant scope:** user-tagged, visible to that tenant's projects only. Never visible to another tenant by default.
- **System scope:** created by **promotion = deep copy**: a new `SymbolDefinition` row (and its crop, vector hash and template artifacts copied to a system storage root) with `PromotedFromId`, `PromotedByUser`, `PromotedUtc`, and a content hash. No foreign key back to the source, so deleting or editing the original has no effect on the copy.
- A tenant that *uses* a system symbol references the system row (immutable, versioned). Editing a system symbol creates a new version. Projects pin the version they used. System symbols are deletable only by an admin, with a "used by N projects" check.
- **Resolved (D8 follow-up):** promotion to System needs **no admin approval**, and promoted symbols are **anonymized** (only the symbol crop, vector hash and meaning are copied; no source sheet, project or customer context).
- **Import, not auto-apply:** a system symbol is never silently used by another tenant's projects. It appears in a browsable System library. A user reviews it (crop, meaning, version, promotion date) and explicitly chooses **"Add to tenant library"**, which makes a **second deep copy** into that tenant's library (`ImportedFromSystemId` for traceability, no live dependency). Users judge symbol correctness, not admins.
- Consequence: a tenant is never affected by later edits or deletion of a system symbol. A newer system version is surfaced as an optional "update available" the user can re-import.
- Remaining small items: allow the promoting user to **withdraw** a system symbol (hides it from the System library; already-imported copies are unaffected), and provide a **report/flag** action so a bad symbol can be hidden by an admin. No approval queue.

### What can proceed without drawings (candidate first slice of Phase 0)

None of this needs real files, and none of it is built yet. Needs your go-ahead:
1. `AIToolbox.Takeoff.Contracts` v0 with generated JSON Schema and the schema-drift test.
2. `takeoff` schema migration runner and the Producer/Provenance tables with CHECK constraints and tests.
3. Provider capability flags and `ProviderSnapshot`, plus image input in the provider client (with unit tests against a stub HTTP handler).
4. Synthetic CAD-style PDF fixture generator with ground-truth JSON.
5. Eval-harness skeleton (tables, P/R computation, job type) tested against the synthetic fixtures.

Blocked until drawings arrive: vector-vs-raster survey, PdfPig vs PyMuPDF decision, sheet-classification and schedule-extraction accuracy, CAD-format decision (D11), gold set.
