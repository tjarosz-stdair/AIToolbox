# Ductwork Takeoff tool: discovery findings and implementation plan

Status: **Steps 1 and 2 complete. All decisions resolved except: D1 (sample drawings, blocks Phase 0), D11 (CAD formats, decided once D1 arrives), and the deferred D10 (MCP hosting) and D33 (cross-tenant symbol sharing). Plan is on hold until sample drawings arrive; awaiting explicit go-ahead before any Phase 0 work (Step 3).**
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
- `JobsController` is hard-coded to ProductParser (`canCancel`, `GetTitle`). The navbar Jobs menu polls `/Jobs/InProgress`, so a Ductwork processing task would show there only if we generalize those two spots.
- `dbo.AiBatchJobs` + `AiBatchRequests` is the only parent/child structure. It models **provider batch APIs** (submit, poll, retrieve), not general work. It is useful later for cheap bulk crop classification, not as our job framework.
- Call-entity extraction has a second in-memory channel queue (same pattern).
- No Hangfire/Quartz in AIToolbox. (The P21Toolbox repo uses Hangfire, per my notes. Not verified here.)

**Verdict:** nothing here satisfies parent/child, per-job progress, retry/resume, or out-of-process execution. We build a processing-task table. We reuse only the *user-facing* surface (Jobs menu, SignalR hub pattern).

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
- Usage/cost: `ToolUsageLog` + `ModelPricing` exist and can be reused for Ductwork LLM spend.

### 4. "Tools": registry vs LLM function tools

- **Registry (what the brief likely means by "defined tools"):** `ToolDefinitions` + `ToolUserAllowList`. Registering a new tool today takes **four hand-edits**: a seed `INSERT` in `DatabaseInitializer.SchemaSql`, an authorization policy in `Program.cs` (`Tool:ProductParser`), the card link switch in `Views/Home/Index.cshtml`, and (for API) scope `tool:{ToolKey}` (data-driven). The Serilog per-tool log sink is also hard-coded in `Program.cs`.
- **LLM function tools:** only in the PicSearch chat agent loop. MCP tools come from the **P21 Toolbox** MCP server via `ModelContextProtocol.Core` (AIToolbox is an MCP *client*, and the package is `.Core`, not `.AspNetCore`, so it cannot host an MCP server today). `AiChatClientFactory` wraps `FunctionInvocation` with `MaximumIterationsPerRequest = 10`. The loop supports OpenAiCompatible, AzureOpenAi and Anthropic only. There is no registry a worker or sidecar could call.
- **Consequence:** the v2 Ductwork MCP tools (`get_job_totals` etc.) need a decision on which side hosts the server (D10). A Ductwork worker has no existing tool mechanism to reuse, and the extraction/rules work doesn't need one.

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
- **Project-level access:** only `IsPublic` bool exists. Ductwork needs real per-job membership (owner/editor/viewer).
- **Machine auth we can reuse for the worker:** `ApiConsumers` + `ApiConsumerKeys` (hashed, scoped, revocable, `tool:{ToolKey}` scope + `KnownApiScopes`). Precedent for a Windows-service client using it: `RingCXAgentHubAPI` calls AIToolbox with `X-AIToolbox-Api-Key`; `RingCXStreamListener` is a .NET Windows service (`UseWindowsService`).
- **Real-time:** SignalR is wired (`CallMonitorHub`, AdminOnly). Can host a progress hub.
- **UI conventions:** single flat navbar (Admin Dashboard + Jobs menu); tools are cards on Home. **No Areas, no bundler, no libman, no npm.** `wwwroot/lib` holds only bootstrap/jquery, and JS is hand-written vanilla (`site.js` 847 lines). Leaflet would be vendored by hand.
- **Framing:** `FrameGuardMiddleware` sends `X-Frame-Options: DENY` + `frame-ancestors 'none'` on every path except `/embed`. **A same-origin iframe panel is blocked today.** The only iframe pattern is the strict-CSP P21 embed (no inline script/style). Recommendation: a normal Razor page hosting the viewer, **no iframe**.
- **Persistence conventions:** hand-written ADO.NET repositories; schema is a single ~1,200-line idempotent T-SQL script run at startup (`DatabaseInitializer`). No EF, no migration framework. Test project is xUnit referencing the web project.

### 7. Items that conflict with the brief's architecture

1. **Provenance is by convention today** (nullable snapshot FK, caller-computed hash, mutable provider rows). We must not reuse `ToolJobs` for Ductwork processing tasks.
2. **Provenance model is too narrow for non-LLM producers.** Vector extraction, geometry and template matching have no directive or model. Replace "directive@version + model definition" with a **Producer** that is exactly one of: LLM (directive snapshot + provider snapshot), Algorithm (name + version + params hash), Manual (user). Enforced by a CHECK constraint (D5).
3. **No image input** to LLMs (see 3). Required for crop classification.
4. **No capability model**, so we cannot refuse binding a non-vision model to `crop-classify` without adding flags.
5. **Upload path cannot carry the files** (see 5).
6. **LLM keys live only in the web host**, so a worker needs a gateway (D4).
7. **Directive/provider admin screens edit live rows**, which bypasses the "no directive change or model swap without an eval run" rule. Ductwork bindings must pin **immutable snapshots** and be changed only through a gated path.
8. **Startup SQL script will not scale** to ~15 new tables (D7).
9. **iframe/viewer pattern**: not available. Use a plain page.

---

## Part 2. Recommended placement

```
AIToolbox.Web                      existing: auth, admin, tool registry, providers, directives, usage, SignalR
  Areas/Ductwork/                   Razor UI, controllers, viewer JS (vendored Leaflet), ingest + LLM-gateway API
AIToolbox.Ductwork.Contracts        net10 class library, zero dependencies: detection-result contract, job payloads,
                                   Producer/Provenance types, generated JSON Schema (source of truth = C#)
AIToolbox.Ductwork.Core             net10 library: deterministic rules engine, aggregation, reconciliation, report model
                                   (pure logic, no web/CV deps, heavily unit-tested)
AIToolbox.Ductwork.Worker           net10 Worker Service / Windows service: claims jobs, runs pipeline steps, calls the sidecar
ductwork-sidecar/ (Python)          optional, vector/CV compute only; speaks the JSON contract; no shared logic
AIToolbox.Ductwork.Tests            xUnit + synthetic CAD-PDF fixtures with ground-truth JSON
```

Why an **Area inside AIToolbox.Web** and not a separate web app: authentication, tool access, ApiConsumer keys, providers, directives, usage logging, Jobs menu and Admin all live there. The Area keeps `Views/Controllers/wwwroot` self-contained. Constraint kept: **AIToolbox.Web references only Contracts and Core**, never the worker, CV or PDF-processing packages.

New SQL: a dedicated **`ductwork` schema** with an ordered, versioned migration runner (embedded `.sql` files + `ductwork.SchemaVersion`), separate from `DatabaseInitializer`.

---

## Part 3. Data model (Phase 1 target)

Provenance and supersession are structural from the first migration. Vocabulary: a **Ductwork Job** is an entity (one building) with a lifecycle, edit lock and audit log. A **Processing Task** is a workflow step run by the worker (ingest, rasterize, extract, classify, evaluate, report, archive). Tasks belong to a job but are not jobs.

```
ductwork.Producer            (immutable)  ProducerId PK, Kind {Llm|Algorithm|Manual}, DirectiveSnapshotId?, ProviderSnapshotId?,
                                         AlgorithmName?, AlgorithmVersion?, ParamsHash?, ManualUserEmail?
                            CHECK: columns required/forbidden per Kind; UNIQUE on natural key so rows dedupe.
ductwork.ProviderSnapshot    (immutable)  type, endpoint host, model, api version, max tokens, ContentHash UNIQUE. Never stores the key.
ductwork.TaskBinding         ToolKey='DuctworkTakeoff', TaskKey (sheet-classify | schedule-extract | legend-propose | crop-classify |
                            notes-to-rules), DirectiveSnapshotId NOT NULL, ProviderSnapshotId NOT NULL, EvalRunId NOT NULL, ActiveFrom
Job                         (a single building) JobId, PublicId (guid), Name, CustomerName, ProjectName (free text), BuildingAddress,
                            State {Draft|InReview|Completing|Complete|Closed|Archiving|Archived|Restoring}, CompletionVersion, StateVersion,
                            CompletedUtc/By, ClosedUtc/By/Reason,  ** master record: NEVER deleted, not even by purge **
                            OwnerEmail, CreatedUtc/By
JobMember                   JobId, UserEmail, Role {Owner|Editor|Viewer}   (Owner and Editor = "edit permission")
Tag                         TagId, Name, NameNormalized UNIQUE   (instance DB is the tenant boundary, so no TenantKey)
JobTag                      JobId, TagId, AddedBy, AddedUtc
JobWorkflowStep             JobId, Step {Setup|Process|Review|Report}, Status {NotStarted|Active|Done}, UpdatedUtc
ToolPermission              UserEmail, Permission {JobManager}, GrantedBy, GrantedUtc   (Ductwork-tool-only, NOT a global AIToolbox role)
DrawingSetRevision          JobId, RevisionNo, SupersedesRevisionId?    <- addendum diffing later
SourceFile                  RevisionId, Sha256, StoragePath, Bytes, ContentType
Sheet                       RevisionId, SourceFileId, PageNo, SheetNumber, Floor, ViewType, Discipline, ScaleRatio, ScaleCalibrationJson,
                            CoordinateSpaceJson, SupersededBySheetId?
SheetRegion                 SheetId, Kind {Include|Exclude}, Reason, PolygonJson
Entity                      EntityId, SheetId, ProducerId NOT NULL, ProcessingTaskId NOT NULL, Type, Size, Floor, View, Source {Vector|Detector|Vlm|
                            Manual|Derived}, Confidence, Status {Proposed|Verified|Rejected|Manual}, BBox (4 float cols, indexed),
                            PolygonJson, ParentEntityId?, DerivedFromRuleId?, SupersedesEntityId?, SupersededByEntityId?
EntityRevision              (append-only) every state change; basis for undo, audit trail, correction log
Rule                        JobId, SchemaVersion, DefinitionJson (constrained schema), ProposedByProducerId, Status {Proposed|Approved|
                            Rejected}, ApprovedBy, ApprovedUtc
SymbolDefinition            (logical symbol) PublicId, Name, MeaningType, Scope {Job|Tenant} (plain column, no CHECK limiting future values),
                            JobId? (required when Scope=Job), SourceJobId? (FK to permanent Job master record, set on copy to Tenant scope),
                            CopiedFromVersionId?, State {Active|Retired}
SymbolVersion               (immutable) SymbolDefinitionId, VersionNo, ContentHash, VectorHash, TemplateRef, CropRef, MeaningJson
SymbolContext               SymbolVersionId, SheetId?, BBox?, CreatedBy, CreatedUtc   (kept apart from content, see Library model)
SymbolMatch                 SymbolVersionId, SheetId, BBox, Score, Status
JobAuditEvent               rich append-only per-job audit log with hash chain, see Part 8.5 (feeds the gold set)
ProcessingTask              (a workflow step, NOT an entity) ProcessingTaskId, ParentTaskId?, JobId, TaskType, Status, Progress{done,total,message}, Attempt, MaxAttempts,
                            LeaseOwner, LeaseExpiresUtc, ProducerId NOT NULL, InputManifestRef, ResultManifestRef, ContractVersion
EvalRun / EvalResult        GoldSetId, ProducerId or TaskBinding candidate, per-FittingType TP/FP/FN, precision, recall, ContractVersion
GoldSet / GoldLabel         Sheet crop refs + labels; fed by accepted corrections
```

Invariants enforced in the schema and types:
- Job-scoped tables (Entity, EntityRevision, Rule, SheetRegion, SymbolMatch, JobAuditEvent) carry a denormalized **`JobId NOT NULL`** and a `PublicId`, which makes the edit-lock trigger, purge and restore simple.
- `Entity.ProducerId` and `Entity.ProcessingTaskId` are **NOT NULL FKs**. The only entity-insert path is a repository method taking a non-default `Provenance` value issued by the job context. The worker never writes entity rows; it POSTs a contract-validated result and the web app is the single write path.
- `Producer` CHECK constraint rules out an LLM producer without both snapshots.
- `TaskBinding` requires an `EvalRunId`, so no directive or model can be activated without a recorded eval run.
- Large payloads (vector dumps, tiles, crops) live on disk. SQL holds paths, hashes and manifests.

**Canonical coordinate space (contract decision):** sheet space = PDF points, origin **top-left** after applying page rotation and CropBox, y down. The transform to PDF space is recorded per page. Leaflet `CRS.Simple` conversion (y-up, negative lat) happens only at the viewer boundary. This is where y-flip bugs get pinned by tests.

---

## Part 4. Phased plan

Sizing is relative (S/M/L) and not a promise.

### Phase 0: Platform prerequisites + spike + eval harness (M-L)
**Deliverables**
- Contract v0 (`AIToolbox.Ductwork.Contracts`): detection result, entity, producer/provenance, processing-task payloads. JSON Schema generated from C#, checked in, with a test that fails if regenerated schema differs.
- Eval harness: task type `eval-run`, `GoldSet/GoldLabel`, P/R per fitting type, tagged with producer; CLI + admin page for results. Gold labeling tool: a minimal viewer-based labeler (reuse Phase 1 viewer later).
- Spike (throwaway, clearly marked): vector extraction on real sets (PdfPig vs PyMuPDF, D2), schedule table extraction, sheet classification via LLM on thumbnails/title-block crops.
- Platform pieces the spike needs: image input in the provider client (Anthropic `image`, OpenAI image content), `ProviderSnapshot`, capability flags (`SupportsVision`, `SupportsStructuredOutput`), structured-output/JSON-schema validation of responses.

**Uses:** `AiProviders`, `ToolDirectives` + snapshots, `ToolUsageLog`/`ModelPricing`, `ApiConsumers`.
**New:** everything under Contracts, eval tables, image path in `AiProviderClient`.
**Exit criteria:** the vector-vs-raster mix of real drawings is known and written down. Schedule extraction and sheet classification hit a threshold agreed up front (propose: ≥95% sheet-type, ≥98% schedule-row accuracy on gold set **[numbers need your input]**). A go/no-go on vector-first is recorded.
**Risks:** no real drawings, so no meaningful gold set (**D1**). If the sets are mostly scanned or rasterized, the vector-first premise weakens and OCR/detector cost enters the plan. Confidentiality of drawings sent to hosted LLMs (D3).

### Phase 1: Foundations (L)
**Deliverables:** `ductwork` schema + migration runner; data model above; Producer/Provenance types and enforcement tests; processing-task table with lease/claim/heartbeat/retry/parent-child/progress; Job entity with lifecycle, membership, edit lock and audit log; worker skeleton (claims via the API, heartbeats, posts results); chunked upload + `IArtifactStore` abstraction; tool registration (`DuctworkTakeoff` `ToolDefinition`, policy, Home card, log sink, scope `tool:DuctworkTakeoff`); project membership; Jobs menu generalization; viewer + tree + filters + two-way hover/selection sync (one selection store keyed by EntityId) **on fake detections**; SignalR progress.
**Uses:** auth, `ApiConsumers` for worker, SignalR, Jobs menu, Admin shell.
**Exit:** upload a 100 MB PDF through IIS and the proxy; a fake processing task runs end to end through the worker with retry after a killed worker; fake entities render and sync tree↔overlay at 10k+ entities (canvas renderer); no code path inserts an entity without provenance (test + CHECK). Edit lock, preemption and completion rules (Part 8) verified with two browsers.
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
`notes-to-rules` directive emitting the constrained rule schema; rules review UI; deterministic evaluator in `Ductwork.Core` (add-child-per-parent, allowances, attribute assignment, scope/exclusions); derived leaves with link to rule.
**Exit:** evaluator is pure and property-tested; **LLM output never touches counts** (type boundary: only an approved `Rule` row feeds the evaluator).
**Risks:** rule schema too narrow or too loose; conflicting rules (define precedence up front).

### Phase 5: Review, refine, reports (L)
Edit tools (add/delete/reclassify/resize/merge/split, lasso, bulk), undo via `EntityRevision`, review queue (confidence-sorted, keyboard), per-sheet sign-off, measure tool, assumptions/RFIs, floor and job reports, Excel + PDF as **processing tasks** (PDF via HTML + headless browser for the PoC, see Part 8.4), correction logging with crop and context into `JobAuditEvent` and `GoldLabel` candidates.
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
| **D3** | **Data handling:** may customer drawings go to hosted LLM providers? Which configured providers qualify? | Crops and tables only (per your design) and an admin-visible "allowed for Ductwork" flag on providers. Your call on policy. |
| **D4** | **LLM access from worker:** worker holds provider keys vs calls a web-app **LLM gateway** endpoint. | Gateway. Keys stay on the web host (Data Protection), and directive+provider snapshots and usage logging are stamped in one place. |
| **D5** | **Provenance generalization:** replace "directive@version + model" with the Producer concept (LLM / Algorithm / Manual). | Yes. The brief's wording cannot cover vector and CV outputs. |
| **D6** | **Per-task models:** new `TaskBinding` table (recommended) vs one `ToolKey` per task (would pollute the tool directory and API scope list). | `TaskBinding` with required `EvalRunId`. |
| **D7** | **Schema management:** separate `ductwork` schema + migration runner vs extend the startup script. | Separate. It also keeps the existing script untouched. |
| **D8** | **Scope/permissions:** symbol libraries deployment-wide + per-job, per-job membership roles, whether Koch Air and Standard Air users share jobs. | Deployment-wide library, per-project membership, no cross-project visibility by default. |
| **D9** | **Licensing:** QuestPDF (commercial license thresholds **[unverified]**), PyMuPDF AGPL, Apryse/Nutrient (noted alternative only). | Confirm before Phase 5. ClosedXML for Excel is permissive **[unverified]**. |
| **D10** | **v2 MCP tools** hosted where: new MCP server in AIToolbox (needs `ModelContextProtocol.AspNetCore`) vs in P21 Toolbox. | Defer; revisit after Phase 3. |
| **D11** | **CAD formats:** PDF only for v1. DXF later (open parsers exist **[unverified]**). DWG needs vendor libraries/licensing. | PDF-only v1. |
| **D12** | **Worker topology: local-per-tenant vs central service** (you asked me to surface this). | **Resolved: central worker pool, API-only with per-instance tokens. See Part 9.** |
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
| D3 | Providers need a "vision processing enabled" tag. Vision/document-ingesting LLMs must route through **Microsoft Foundry** for governance. | Add provider capability flags `SupportsVision`, `AllowedForDuctwork`, and `GovernanceRoute` (e.g. Foundry). `TaskBinding` refuses a provider lacking the tag. The `ProviderSnapshot` records the Foundry endpoint host. Needs checking: Foundry endpoint shape against the existing `AzureOpenAi`/`Anthropic` provider types **[unverified]**. |
| D4 | LLM gateway in web app. | As planned. Worker has no provider keys. |
| D5 | Producer generalization. | As planned. |
| D6 | `TaskBinding`. | As planned, `EvalRunId` required. |
| D7 | Separate `ductwork` schema. Ductwork data segregated. Shared with the rest of AIToolbox: only the `ToolDefinition` row and the job kick-off/startup record. | Drop the idea of a Jobs-menu rewrite. A thin `ToolJobs` row is written at kick-off (for the navbar Jobs menu and usage), pointing at `ductwork.Job`. All progress, children, leases live in `ductwork`. |
| D8 | Libraries/symbols are **job-specific by default**, tenant-isolated, and can be promoted to a system-wide library. Promotion **copies** so deleting the original cannot affect dependents. | See "Library model" below. |
| D9 | QuestPDF Professional ($2k/yr subscription) rejected. **PoC: HTML report printed to PDF by a headless browser.** If production needs a library, user will consider licensing PDFsharp (quoted EUR 750 perpetual). | See Part 8.4. Report layer sits behind a renderer interface. Excel stays ClosedXML. |
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

Implications for Ductwork:
1. **The interface is shaped for deliver/pickup sync**, not for random-access reads. Viewer tiles need many small, fast, ranged reads. **Plan:** the configured storage (SharePoint/Direct) is the **system of record for source drawings and generated reports**. Derived artifacts (tiles, page rasters, crops, vector dumps) live in a **local, per-deployment cache** that can be rebuilt from the source. Tile serving never hits Graph per request.
2. **Isolation:** Koch Air and Standard Air intentionally share one AIToolbox instance (parent company), so no isolation is needed between them. Isolation matters **between instances** (each tenant has its own IIS site, database and storage config) and at the **central worker** (Part 9). Storage path is keyed by Ductwork Job. Every read goes through `PathSafety.IsContainedIn` against that tenant's root, and the API (not the path) is the only way a worker gets a file. Test: a request for tenant B's artifact id under tenant A's credentials must 404.
3. **SharePoint permissions:** app-only Graph with broad Sites permissions can read every site. Recommend **`Sites.Selected`** scoped to the specific Ductwork site per tenant. Needs your IT decision.
4. **Users already store jobs in SharePoint/OneDrive**: Phase 1 offers "import from SharePoint library" through Graph (picking a file, not browsing the whole tenant), in addition to direct chunked upload.
5. **Admin UI:** a Ductwork Storage tab mirroring the P21Toolbox Storage tab (provider type, base path or site/drive/path, validate-access button, secrets write-only with a "configured" badge).
6. The existing AIToolbox `Storage:*` options are PDF-only and tool-wide. Ductwork gets its own options and does not use `FileStorageService`.

### Library model (D8, revised: cross-tenant sharing deferred)

v1 builds a **tenant-isolated, instance-wide symbol library shared between users and jobs**, with an extensible foundation. Cross-tenant sharing is deferred until it is actually needed (D33).

- **Scopes:** `Job` (default, visible only inside that Ductwork Job) and `Tenant` (the instance-wide library, visible to every user and job in this instance). Scope is a plain column with no CHECK limiting it to these two, so a later cross-tenant scope is an additive migration.
- **Promotion inside the instance:** a user tags a job symbol, or a whole job library, **"Add to tenant library"**. This is a **copy**, not a move or a reference, so deleting the job symbol or purging the job never affects other jobs that came to depend on the library symbol. The copy records `SourceJobId` (a real FK to the permanent Job master record, which works because it is the same database; this satisfies D22) and `CopiedFromVersionId`.
- **Foundation seams, in from day one:**
  1. **Definition plus immutable versions:** `SymbolDefinition` (logical symbol) and `SymbolVersion` (content-hashed, immutable). Edits create a new version. Jobs and matches **pin a version**, so library changes never silently change an existing job.
  2. **Content separate from context:** crop, vector hash, template and meaning live in `SymbolVersion`; where it came from (sheet, box, who) lives in `SymbolContext`. A future anonymized export is then just "content without context".
  3. **Stable `PublicId` and `ContentHash`** so identical content is recognizable across instances later.
  4. **`ISymbolLibrary` interface** for all access. v1 has one local implementation. A remote or central provider can be added later without touching callers.
  5. The archive zip includes the symbol versions a job used.
- **Retiring, not deleting:** tenant-library symbols are retired (soft-deleted) by their owner or an admin. A retired version stays readable for any job pinned to it. A purge never touches tenant-library symbols.
- **Not built now:** System scope, any central Hub, anonymization pipeline, cross-tenant import, withdraw/flag. If cross-tenant sharing is revisited, the earlier intended behavior stands (anonymized deep copy, user-initiated import, no admin approval, withdraw and flag, soft reference to the source job). See Part 9.3.

### What can proceed without drawings (candidate first slice of Phase 0)

None of this needs real files, and none of it is built yet. Needs your go-ahead:
1. `AIToolbox.Ductwork.Contracts` v0 with generated JSON Schema and the schema-drift test.
2. `ductwork` schema migration runner and the Producer/Provenance tables with CHECK constraints and tests.
3. Provider capability flags and `ProviderSnapshot`, plus image input in the provider client (with unit tests against a stub HTTP handler).
4. Synthetic CAD-style PDF fixture generator with ground-truth JSON.
5. Eval-harness skeleton (tables, P/R computation, job type) tested against the synthetic fixtures.

Blocked until drawings arrive: vector-vs-raster survey, PdfPig vs PyMuPDF decision, sheet-classification and schedule-extraction accuracy, CAD-format decision (D11), gold set.


---

## Part 8. Job persistence, lifecycle, completion, archiving, and edit locking

Terminology: a **Ductwork Job** is **one building** (the contractor's meaning of "job"). Each Job is its own entity with its own lifecycle (complete, archive), edit lock, membership and audit log. A **Processing Task** (aka processing job) is a workflow step run by the worker on behalf of a Job and is not an entity (Part 3). Addenda are a `DrawingSetRevision` within the same Job. Roles per Job: **Owner**, **Editor**, **Viewer**; "edit permission" means Owner or Editor. There is no Project grouping above a Job in v1 (D26).

### 8.1 Where a Ductwork Job lives, by lifecycle stage

| Stage | Authoritative store | Notes |
|---|---|---|
| **Working** (Draft / InReview) | **SQL Server, `ductwork` schema**: sheets, entities, append-only entity revisions, rules, symbols, audit events, lock | Needs transactions, concurrent readers, per-edit append, undo, queries by sheet/type/confidence, an atomic lock. A JSON file cannot do partial updates, concurrent access, or undo safely. |
| **Complete / Closed** | Same SQL rows, now **read-only**. Issued reports (xlsx/pdf) as files in tenant storage | Everything is still instantly viewable, reportable, and reopenable. |
| **Archived, zip only** | SQL rows **kept** plus a zip in archive storage | A safety copy. Job remains `Complete` and fully viewable. |
| **Archived and purged** | **Zip in archive storage** is the only full copy. SQL keeps **job metadata only** | Job is `Archived`. Not viewable until restored. |

Large binaries (source PDFs, reports, archive zips): configured tenant storage (SharePoint / Direct / Relay, Part 7). Rebuildable derivatives (tiles, rasters, vector dumps, crops cache): local cache, purged on archive-and-purge. SQL holds only paths, SHA-256 and sizes for files.

**Not** flat-JSON as the working store. Working data is relational because it is edited. The archive is a self-describing file because it is kept and must be restorable.

### 8.2 Complete, Close and Archive

**Workflow steps** tracked per job (`JobWorkflowStep`): **Setup** (sheets set up, scale calibrated), **Process** (detection run finished), **Review** (every in-scope sheet signed off), **Report** (report generated). This follows the brief's Setup -> Process -> Review/Refine -> Report.

- **Complete** = "the job is done, all work is complete": all four steps are Done. Editing disabled.
- **Close** = "we're not working on this anymore": a deliberate end for a job that did **not** run through all steps (for example a bid that was lost or a partial scope). Editing disabled, reason required. Distinct from Complete, so the record says honestly that the workflow was not finished (D30).
- **Neither is allowed while any processing task on the job is active** (queued, running or retrying). The user must wait or cancel the tasks first.
- Both are read-only, archivable, and reopenable by any user with edit permission.

| | **Complete / Closed** | **Archive (zip only)** | **Archive and purge** |
|---|---|---|---|
| Meaning | Estimator is done (Complete) or ended the job early (Closed). Editing disabled. | Make a full restorable copy. Nothing removed. | Make the copy, verify it, then remove the SQL detail. |
| Editing | Blocked | Blocked (job stays `Complete`/`Closed`) | Blocked |
| SQL job detail | Kept | Kept | **Purged**. Only metadata remains |
| Zip written | No (issued reports only) | Yes | Yes |
| Viewable in app | Yes | Yes | **No** until restored (metadata and summary totals only) |
| Reversible | Reopen (any editor, with reason) | Delete the copy | **Restore** from zip, then optionally reopen |
| Who | Any user with edit permission (takes the edit lock for the action) | Job owner, **Job Manager**, or admin | Job owner, **Job Manager**, or admin, explicit confirmation |

**Complete:**
- Allowed to any user with edit permission on the job. The action needs the edit lock, and acquires it if free. **Refused while any processing task is active, and refused unless all four workflow steps are Done** (otherwise the user can Close instead). Unresolved items that remain (unreconciled schedule counts, open RFIs) are listed for information.
- **Close** follows the same rules except that it does not require all steps Done. It needs a reason, still requires no active processing task, and lists which steps were not finished. State goes straight to `Closed`.
- State `Completing` produces the issued reports (processing task), then `Complete`. A failed report run leaves it in `Completing` with retry, never silently `Complete`.
- **Enforced in three places, not only the UI:** (1) every mutating endpoint returns 409 when the job is not editable; (2) data-layer mutation methods take an `EditToken` that only the lock service issues, and only for an editable job (type-level, like `Provenance`); (3) a SQL trigger on `Entity`, `EntityRevision`, `Rule`, `SheetRegion`, `SymbolMatch` rejects writes when the owning Ductwork Job is not editable, with one narrow audited path for reopen and restore.
- **Reopen:** **any user with edit permission on the job** (Owner or Editor), not only the owner. A reason is captured (can be made optional), it is audited, the reopening user acquires the edit lock, and the completion version increments. A reopened-and-recompleted job is a new completion version. Any earlier archive is flagged `Superseded` but never deleted.

**Archive:**
- Triggered **manually only**, by the job owner, a **Job Manager**, or an admin. **There is no automatic archiving** (D23). Only a `Complete` or `Closed` job can be archived.
- Runs as a **processing task**. States: `Complete`/`Closed` -> `Archiving` -> back to `Complete`/`Closed` (zip only) or `Archived` (and purged). `Archiving` blocks everything else on the job.
- **Zip-only** creates the package and a `JobArchive` row, then returns to `Complete`.
- **Archive and purge** adds these ordered, safety-gated steps:
  1. Write the zip to archive storage.
  2. **Read it back** and verify its SHA-256 and every file hash in the manifest.
  3. **Verify counts:** per-table row counts in the manifest equal the live row counts, read from one consistent snapshot while the job is frozen.
  4. **Dry-run restore:** parse every JSON file against the archive schema for its version and validate referential integrity in memory (no DB writes).
  5. Persist `PurgePending`, then delete detail rows in **resumable batches** (large jobs may have 100k+ rows). A crash resumes at the same step. At every point after step 2 the zip is verified, so a half-purged job is still restorable.
  6. Delete the local cache for the job, set state `Archived`, write an audit event.
  Any failed gate aborts before step 5. **Nothing is deleted unless steps 1-4 all pass.** The UI states that purge is irreversible except via the zip, and requires typing the job name to confirm.
- **Master record, never purged (D22):** the `Job` row is a permanent master/base record kept for tracking. Purge removes only the job's detail rows. There is no hard-delete of a Job anywhere in the product, enforced by no DELETE permission for the application login, a trigger, and `NO ACTION` foreign keys from anything that must outlive the job (the `JobArchive` row and tenant-library symbols that record their `SourceJobId`). This is what lets system-library symbols keep a link back to their source job.
- **What remains in SQL after purge (metadata):** the Job master record (name, customer/job reference, owner, revision, dates, state, completion version), a **summary** (sheet/entity counts, floor and job totals JSON so lists and dashboards still work), the `JobArchive` row (storage provider and root key, path, SHA-256, bytes, archive schema version, contract version, per-table row counts, created/verified/purged timestamps), and a job-level audit trail (completed, archived, purged, restored).
- **Shared immutable records are not purged:** `Producer`, directive snapshots, and provider snapshots are shared across jobs and deduplicated by content hash. The zip contains copies of the ones the job used, so restore can recreate any that are missing.
- **Library symbols:** job-scoped symbols are job detail (purged, kept in the zip). **Tenant-library symbols are never deleted by a purge** (D22) and keep `SourceJobId`, a real FK to the permanent Job master record, so the link never dangles. The zip holds the symbol versions the job actually used.

**Archive package** (one zip per archive operation):
```
ductwork-{jobId}-v{completionVersion}-{yyyyMMdd}.zip
  manifest.json            archive schema version, contract version, app version, job id, completion version, archivedBy/Utc,
                           mode (ZipOnly|ZipAndPurge), per-table row counts, SHA-256 of every file below
  data/*.json              one file per table (job, sheets, regions, entities, entity-revisions, rules, symbols, symbol-matches,
                           audit-events, processing-tasks, locks excluded), JSON Lines for large tables
  provenance.json          every Producer used, with directive snapshot text and provider snapshot values copied in
  summary.json             floor and job totals as issued
  reports/                 issued report.xlsx and report.pdf
  sources/                 **all source PDFs, always bundled** (D20), plus sources.json with names, SHA-256 and original storage locations
  symbols/                 crops, vector hashes and templates for the job-scoped symbols
```
Archives can be large (plan sets run 20-100+ MB), so storage sizing matters. Reading a zip needs only JSON tooling. The `data/` files are a **row-level export sufficient to re-populate the job**, not just a report.

**Restore (retrieve from archive):**
- Job owner, **Job Manager** or admin selects an `Archived` job (or a zip-only copy for disaster recovery) and chooses **Restore**. Runs as a processing task.
- **Missing archive:** if the zip cannot be found at the recorded location (or any admin-configured secondary location), restore stops with a clear warning: *"Archive file not found. Contact your administrator."* No partial restore. The failure is written to the job audit log and to an admin-visible alert, with the last known path and SHA-256 so the admin can find it.
- Steps: locate the zip through `JobArchive`, verify SHA-256, check the archive schema version and run its versioned importer (older archive versions keep working), validate, then insert in dependency order in one transaction per table group, re-point to existing shared records by content hash or recreate them, and rebuild the local cache (tiles, rasters) from the sources.
- **ID strategy:** job-scoped tables get a stable `PublicId` (GUID) used in the archive and in all cross-references, so restore does not depend on identity values. Restored rows get new integer keys, mapped through the `PublicId`s.
- Result: job returns as `Complete` (read-only) with `RestoredUtc`. Reopen then works as normal. Restore refuses if live rows for that job already exist (avoids a double import).
- **Same tenant only** for v1: a zip is the tenant's data. Cross-tenant or cross-deployment restore is not supported (D24).
- **Structural test:** a round-trip test archives a fixture job, purges it, restores it, and compares a canonical hash of all job data before and after. Golden zips from each released archive version are kept in the test suite so old archives stay restorable.

**Archive storage management (D25):** zips are **never deleted automatically**. An admin can **move an archive to secondary storage** from an Archives admin page (copy, verify SHA-256 at the destination, update `JobArchive` location and keep a location history, optionally remove the original, audit-logged). If an admin moves a file outside the app, a **Relink** action points the record at the new path and verifies the hash. Restore uses the recorded location, so moves are transparent to users.

**Lifecycle states:** `Draft` -> `InReview` -> `Completing` -> `Complete`; `Draft`/`InReview` -> `Closed`; `Complete`/`Closed` -> `Archiving` -> `Archived` (or back, for zip only); `Archived` -> `Restoring` -> `Complete`/`Closed` (the state it had); `Complete`/`Closed` -> reopened -> `Draft`.

**Permissions (Ductwork only):**

| Who | Can |
|---|---|
| Viewer (per job) | View the job, reports, audit log |
| Editor (per job) | Hold the edit lock, edit, complete, close, reopen |
| Owner (per job) | Everything an Editor can, manage members, archive/purge/restore that job |
| **Job Manager** (Ductwork tool permission) | **Archive, purge and restore any job**, whether or not a member. Does not grant view or edit access to job contents. Granted by an AIToolbox admin in the Ductwork admin tab. Not a global AIToolbox permission. |
| AIToolbox admin | Grant Job Manager, manage storage and move/relink archives, force lock takeover |

### 8.3 Single editor, view-only others, takeover

New tables:
```
ductwork.JobEditLock     JobId PK, HolderUserEmail, HolderSessionId, FencingToken bigint (monotonic),
                        AcquiredUtc, LastHeartbeatUtc, ExpiresUtc
ductwork.PreemptRequest  Id, JobId, RequesterEmail, RequesterSessionId, RequestedUtc, ExpiresUtc,
                        Status {Pending|Granted|Cancelled|Denied|Expired}, ResolvedUtc
```
Lock identity is the **browser session**, not the user, so a second tab by the same person is view-only too.

**Open:** one atomic statement either acquires the lock (none held, expired, or same session) or returns the holder. A successful acquirer gets a `FencingToken`. Only users with edit permission on the job (Owner or Editor) can hold the lock; Viewers can only view.

**Heartbeat:** the editing page heartbeats (default 15 s) over SignalR (the app already has a hub pattern), with HTTP fallback. Lock TTL defaults to 60 s without a heartbeat. Graceful exit releases immediately (explicit Close, plus `sendBeacon` on page unload). Ungraceful exit (crash, closed laptop) is covered by the TTL.

**Read-only experience:** banner "Being edited by *Name* (active 12 s ago)" and a **Take over editing** button. Read-only views get a live "state changed" push (`StateVersion`) and refresh, so they never show a long-stale picture.

**Takeover flow:**
1. *Stale lock* (no heartbeat within the TTL): takeover is **immediate**, no prompt, audit-logged. This is the common ungraceful-exit case.
2. *Live lock:* the requester creates a `PreemptRequest`. The holder immediately gets a modal: "*Name* wants to take over editing. **[Keep editing (cancel request)]** **[Hand over now]**" with a countdown.
   - **Holder cancels:** request ends `Denied`; requester is notified.
   - **Holder hands over:** lock transfers at once.
   - **No response within the countdown** (default 60 s): the request is granted, because the holder's tab is open but unattended (D17).
   - The requester can also **cancel their own pending request**.
3. A job owner/admin can force takeover (audit-logged).

**Preempted editor notification (stale-edit protection):**
- Push `LockLost(by, atUtc)` over SignalR. The page goes read-only immediately and shows a blocking notice: "Your editing session was taken over by *Name* at *time*. This view may be out of date." with **Reload latest** and **Stay read-only**.
- **Fencing makes this safe even if the push is missed.** Every write carries the `FencingToken`. After a takeover the token changes, so a late write from the old browser is rejected with 409 `LockLost` and the client shows the same notice. A preempted user cannot overwrite the new editor's work.
- Edits are saved **per operation** (each add/reclassify/merge writes an `EntityRevision` immediately), so there is no large unsaved buffer to lose. At most the single in-flight operation is rejected, and the notice says so.

**Interactions to define:**
- **Reprocessing** (re-run detection/ingest) is a mutation: blocked while a user holds the lock or the job is complete. It must wait or ask the user to release. The worker follows the same fencing rules.
- **Undo** is per user and only valid while that session holds the lock.
- **Audit:** acquire, release, expiry, takeover request, cancel, deny, grant, forced takeover, complete, reopen are all `AuditEvent`s.

### 8.4 PDF generation and licensing (replaces the earlier D9 text)

- **Proof of concept:** the report is a neutral **report model** in `Ductwork.Core`, rendered to **HTML** (also used for the on-screen preview) and printed to PDF by a **headless browser** on the worker. On the worker VM (Windows Server 2019) install a **pinned Edge (Chromium) or Chromium build**, which can print headless (`--print-to-pdf`); to my knowledge Server 2019 does not include Chromium-based Edge by default **[unverified]**, which corrects my earlier remark that servers normally have it. A dedicated VM makes installing it easy. Use CSS `@page` for size, headers and page breaks. Cost: free.
- **Excel:** ClosedXML (license not re-verified; already used in other repos of yours).
- **Production options if that approach is rejected:**
  - **PDFsharp/MigraDoc:** you quoted EUR 750 perpetual. I could not verify the edition or terms. As far as I know the core PDFsharp library is open source under MIT, so confirm what the quote covers before paying **[unverified]**.
  - **QuestPDF:** $2k/year subscription, which conflicts with your stated preference for free or perpetual.
- **Cost of switching later:** PDFsharp and QuestPDF do **not** render HTML. Moving to either means **re-implementing the report layout** in code. Mitigation: all content comes from the neutral report model and rendering sits behind an `IReportRenderer` interface (Html, Xlsx, Pdf), so only layout code is rewritten. Headless-browser printing may be acceptable in production and avoid any license, so the PoC doubles as the test of that.
- Operational rules for headless printing: pin and patch the browser, run it in the worker (never in IIS), cap concurrency and time per render, render from local files only (no external network).

### 8.5 Job audit log

Every Ductwork Job has a rich, append-only audit log. It answers who did what, when, from where, to which object, why, and what changed, and it is the user-facing history (the `EntityRevision` table is the machine-facing state history used for undo).

**Table `ductwork.JobAuditEvent`** (append-only):
```
EventId (bigint, monotonic per job), JobId NOT NULL, OccurredUtc (server clock),
ActorType {User|System|Worker|Llm}, ActorEmail, ActorDisplayName, ActorSessionId, ClientIp, UserAgent,
Category, Action, SubjectType, SubjectPublicId, Summary (human readable sentence),
BeforeJson, AfterJson (changed fields only), Reason (free text, where one was given),
CorrelationId (groups one bulk operation or workflow), ProcessingTaskId?, ProducerId?, RevisionId? (link to EntityRevision),
FencingToken?, StateVersion, PrevHash, Hash (hash chain over the job's events)
```

**What is recorded, by category:**
- **Lifecycle:** job created, renamed, details edited, completed (with the unresolved items acknowledged), reopened (who and why), archived (mode), purged, restore started/finished.
- **Lock:** acquired, released, heartbeat-expired, takeover requested, cancelled, denied, granted, forced, `LockLost` delivered, write rejected for a stale fencing token.
- **Access and membership:** members added/removed/role changed, job opened (session start, not every pan or zoom), read-only view opened, report downloaded, archive downloaded, data exported.
- **Sources and processing:** file uploaded (name, size, SHA-256), sheets split, each processing task started/finished/failed/retried with the producer (directive snapshot and model snapshot, or algorithm and version), token usage and cost, and the configuration used.
- **Setup:** sheet floor/view/discipline set, regions added/changed, scale calibrated (points, distance, resulting scale, title-block cross-check result).
- **Entity edits:** add, delete, reclassify, resize, merge, split, accept, reject, per-sheet sign-off, each with before/after of the changed fields and the `RevisionId`. A lasso or bulk operation writes **one audit event** with the count and a `CorrelationId`, plus one `EntityRevision` per entity, so the log stays readable and undo stays exact.
- **Rules and symbols:** rule proposed (by which producer), approved/rejected, edited; symbol defined, matches accepted/rejected in bulk, symbol imported from the system library, promoted, withdrawn.
- **Reports and settings:** report generated, assumptions/exclusions/RFI flags changed.

**Structural guarantees (not conventions):**
- **No mutation without an audit event, and no audit event without a mutation.** Every data-layer mutating method takes an `AuditContext` (actor, session, reason, correlation) and writes the change and its audit event in **one transaction**. An architecture test fails the build if a public mutating repository method does not accept an `AuditContext`.
- **Append-only:** the application's SQL login has no UPDATE or DELETE on the table, and a trigger rejects them as well. The one exception is the audited purge path.
- **Tamper evidence:** `Hash = SHA-256(PrevHash + canonical event JSON)` per job. A verify action recomputes the chain and reports the first break. The chain head is written into the archive manifest.
- Server time and server-derived actor identity only. The client never supplies the actor, timestamp or session.

**UI:** an Audit tab per job with a filterable timeline (user, category, date range, sheet, entity type, free-text search), a **History** drawer on any entity leaf (its revisions and who changed it), CSV/JSON export, and a "verify integrity" button. Read-only users with access to the job can view the audit log.

**Retention across archive and purge:**
- The complete audit log is always in the archive zip (`data/audit-events.json`, with the hash chain).
- After **archive and purge**, SQL keeps the **lifecycle, lock, access/membership and archive** categories (low volume, needed to answer "who touched this job and when" without restoring). The high-volume categories (entity edits, processing detail) live only in the zip. Restore re-inserts them and re-verifies the chain (D27).
- Correction events (accept/reject/reclassify with crop and context) are also what feeds gold-set candidates (brief item 7). See D22 for how purge treats those.

### 8.6 Decisions needed

| # | Decision | Recommendation |
|---|---|---|
| D16 | **Resolved:** a Ductwork Job = one building. Each Job is a separate entity that completes, holds edit locks, and is archived on its own. | n/a |
| D17 | **Resolved:** takeover with no holder response is granted after a **configurable** timeout, default 60 s. | n/a |
| D18 | **Resolved:** complete or close is refused while any processing task is active. Complete also requires all workflow steps Done. A job that did not finish the workflow is **Closed** instead. | See D30 |
| D19 | **Resolved:** any user with edit permission on the job may reopen it. | Reason captured and audited. |
| D20 | **Resolved:** source PDFs are always included in the archive zip. | n/a |
| D21 | **Resolved** via D17 and D32. | n/a |
| D22 | **Resolved:** tenant-library symbols are never deleted by purge and keep a real FK (`SourceJobId`) to the Job master record, which is never purged. A job's gold-set crops are kept as separate tenant-isolated copies with their own retention. | n/a |
| D23 | **Resolved:** no automatic archiving. | n/a |
| D24 | **Resolved:** same-tenant restore only. | n/a |
| D25 | **Resolved:** no automatic deletion of archives. Admin can move zips to secondary storage. Restoring a missing archive shows "Archive file not found. Contact your administrator." | See 8.2 |
| D26 | **Resolved:** no grouping above a Job. Instead, jobs get free-form **Tags** (see 8.7) used to filter job lists, so one tag such as "Orion Building" can span many jobs. | n/a |
| D27 | **Resolved:** after purge, SQL keeps lifecycle, lock, access/membership and archive audit categories. Full log in the zip with the hash chain. | n/a |
| D28 | **Resolved:** archive/purge/restore by job owner or admin, plus a new Ductwork-only **Job Manager** permission that can archive any job. | Restore included for Job Managers (confirm in D31). |
| D29 | **Resolved with a correction:** "takeoff" is the industry term for identifying all ductwork on a plan and producing the required-material report, so it is the right name for this **tool**. Tool name: **Ductwork Takeoff** (ToolKey `DuctworkTakeoff`). `ductwork` stays as the domain prefix (schema, projects, Job entity) so future sibling tools (bid management, market intelligence, as on withrebar.ai) can share Jobs, storage and audit. | n/a |
| D30 | **Resolved:** Closed = "we're not working on this anymore". Complete = "the job is done, all work is complete". Distinct states. | n/a |
| D31 | **Resolved:** a Job Manager can archive, purge and restore any job. | n/a |
| D32 | **Resolved:** heartbeat 15 s and lock expiry 60 s as admin-configurable defaults. | n/a |

### 8.7 Job tags

- Free-form labels on a Ductwork Job used to **filter the job list**. One tag (e.g. "Orion Building") can be put on many jobs, so there is no separate grouping system (D26).
- **Tenant-isolated:** tags are unique within the instance (case-insensitive, trimmed). Each tenant has its own instance database, so tags can never be visible across tenants by construction.
- Any user with edit permission on a job can add or remove its tags. Viewers cannot. **Tags are job-header metadata, not job content**, so they can be edited on `Complete`, `Closed` and `Archived` jobs without reopening or taking the edit lock. Every change is audited (`JobAuditEvent`, category Lifecycle).
- Tags live on the permanent Job master record, so they survive purge and are also written to the archive zip (`data/tags.json`) and restored with it.
- Job list: filter by one or more tags (all-of by default), plus state, owner, date. Unused tags can be removed by an admin.
- Tag names are plain text, length-limited, and HTML-encoded on display.

### 8.8 Plan impact

- **Phase 1:** `PublicId` and `JobId` on job-scoped tables, `JobAuditEvent` with hash chain and the `AuditContext` architecture test, `JobArchive` table, job state machine, summary table, and the **archive schema versioning rule** (archive format is versioned independently of the DB schema and is never broken without an importer).
- **Phase 5:** archive, purge, and restore jobs, the round-trip and golden-zip tests, and the confirmation UX. Moves from "nice to have" to a gate: **an archive-and-purge must never ship without a passing round-trip test.**
- **Phase 1 also:** tags (`Tag`, `JobTag`, job-list filter), Job master-record protection (no DELETE grant, trigger, NO ACTION FKs), `JobWorkflowStep`, `ToolPermission` with the Job Manager grant UI, `Closed` state.
- **Phase 5 also:** Archives admin page (move, relink, missing-file alert) and the "Archive file not found" restore path.
- **Risk:** archive importers must be maintained for every shipped archive version. Mitigation: freeze the format early, add fields only additively, keep golden zips in CI.


---

## Part 9. Worker topology (D12) and the tenant boundary

**Resolved:** a **central worker pool** serves all tenants. The worker has no direct disk or DB access. It reaches everything through the AIToolbox APIs using tenant-specific tokens.

**Tenant boundary (clarified):** each P21Toolbox tenant that wants AI tools gets **its own AIToolbox instance** (its own IIS site and SQL database). Koch Air and Standard Air intentionally share one instance (Koch Air is the parent), each pointing its own P21Toolbox instance at it. New tenants never share an instance. So **tenant isolation = isolation between instances**, and the central worker is the only component that touches more than one tenant. Cross-tenant symbol sharing is deferred (9.3).

### 9.1 What this simplifies
- No tenant-key columns are needed inside an instance database (`Tag.TenantKey` and `OwnerTenantKey` are dropped). `Tenant` library scope means instance-wide.
- No isolation is needed between Koch Air and Standard Air users inside their shared instance.
- Storage configuration, providers, directives, Foundry governance settings, and credentials are all per instance, so they never mix.

### 9.2 Central worker design rules
- **Pull model:** the worker keeps a registry of instances (name, base URL, API key with scope `tool:DuctworkTakeoff` plus a worker scope) and polls each instance's claim endpoint. No inbound connection to the worker is ever needed. Instances never call the worker.
- **One task, one instance, one process:** each claimed task runs in its own OS process (or container) started with only that instance's URL, token and task id. No other instance's credentials are in its memory or environment. The credential store is readable only by the supervisor.
- **Scratch only:** the only disk the task process may use is `scratch/{instanceId}/{taskId}`, ACL'd to the task account, wiped on completion, failure, and at worker startup for crashed leftovers. Sources are streamed from the artifact API (hash-verified, resumable), and results are pushed back through the API.
- **LLM calls go through the instance's own gateway** (D4), so each instance's providers and Foundry configuration and keys stay in that instance. The worker never sees a provider key.
- **Fairness:** per-instance concurrency caps, round-robin or weighted claiming, global CPU/RAM limits, per-task time and memory limits (kill on breach), and small interactive tasks ahead of bulk work.
- **Version skew:** instances may run different AIToolbox versions. A claim carries the worker version and the contract versions it supports, and an instance only hands out tasks it can serialize for those versions. Policy (D34): an instance's web app and database always match each other, and tenants may be on different versions of the product. The only place versions meet is the central worker, so **the worker is upgraded first and stays backward-compatible with the previous contract major**. Each instance's admin page shows the worker's last-seen time and version, and a clear message if the worker does not support that instance's version.
- **Revocation:** an instance's worker key is a normal revocable `ApiConsumer` key. Revoking it cuts that tenant off immediately.
- **Observability:** per-instance health on each instance's admin page, a central ops view, logs tagged with instance id, and no drawing content in logs.
- **Failure behavior:** if the worker is down, processing waits, but viewing and editing existing jobs keeps working because tiles and results already live in the instance. Claims are lease-based, so a second worker node can be added without code changes.
- **Large files:** a 100 MB source goes through the instance's YARP route per task. The artifact API supports ranged, resumable transfers so a dropped connection does not restart a task.
- **Licensing:** a central worker on your server is a network-use case for AGPL software (PyMuPDF), so the D2 spike should weigh that against PdfPig.

**Automated spillage tests (must pass before any second tenant):** two fake instances served by one worker. A task for instance A cannot read instance B's artifact id (404 under A's token). Scratch folders are not shared. The task process environment contains no other instance's credential. Scratch is empty after every task, including after a forced kill.

### 9.3 Cross-tenant symbol sharing: deferred (D33)
Not built in v1. The instance-wide library and its foundation seams are described under "Library model" in Part 7. Notes for when it is revisited:
- Separate instance databases mean a cross-tenant library needs a neutral central home. My earlier recommendation was a small central "Ductwork Hub" service with its own database (like the existing RingCX hub), not a tenant instance and not the stateless worker.
- Anonymization would run in the source instance before anything is sent. A system symbol's link back to its source job would be a **soft reference** (instance id + Job `PublicId`), because a foreign key cannot span databases, and the source instance would keep never hard-deleting the Job master record.
- Nothing in v1 needs to change to allow this later, thanks to the foundation seams.

### 9.4 Plan impact
- **Phase 1:** worker instance registry and per-instance API keys, claim with version handshake, per-task process isolation with scratch folders, per-instance concurrency caps, the spillage test suite, worker health on the instance admin page.
- **Phase 3:** the instance-wide symbol library with immutable versions, `ISymbolLibrary` and copy-to-tenant-library. No Hub.
- **Phase 0 measurements** (seconds per sheet, peak RAM, disk per job) size the worker host and the concurrency caps.

### 9.5 New decisions
| # | Decision | Recommendation |
|---|---|---|
| D33 | **Deferred:** cross-tenant symbol sharing. v1 is tenant-isolated with an extensible library foundation. | Revisit when needed (9.3). |
| D34 | **Resolved:** web app and DB always match within an instance. The worker is upgraded first and remains backward-compatible with the previous contract major, so tenants can be on different versions. | n/a |
| D35 | **Resolved:** dedicated worker VM on Windows Server 2019 Datacenter (Hyper-V cluster). See 9.6. | n/a |

### 9.6 Worker VM (D35)
Dedicated VM on your Windows Server 2019 Datacenter Hyper-V cluster. Notes and checks:
- **Runtime:** install the .NET 10 runtime and Python 3.12 in a pinned virtual environment with an offline wheel cache. Confirm .NET 10 supports Windows Server 2019 before provisioning **[unverified]**.
- **PDF printing:** a pinned Edge (Chromium) or Chromium build, updated only deliberately (see 8.4).
- **Failover:** the VM can restart on another cluster host. The worker is stateless, scratch is wiped at start, and task leases expire so the owning instance requeues interrupted tasks (attempt count applies). Nothing on the VM needs backup except its configuration and encrypted credentials.
- **Isolation on the VM:** a supervisor Windows service starts each task as a separate low-privilege process (a virtual or local account), limited with Windows **Job Objects** for memory, CPU and wall-clock time. The task account can write only to its scratch folder.
- **Secrets:** per-instance API keys are protected (DPAPI machine scope or a certificate-protected file) and readable only by the supervisor's service account.
- **Network:** outbound HTTPS to each instance's URL only. No inbound ports. Foundry calls are made by the instances, not the worker, so the VM needs no direct LLM access.
- **Operations:** a **drain mode** (finish current tasks, claim no new ones) for Windows patching and upgrades. Add capacity by adding VMs, since claims are lease-based. No GPU is planned. If one is ever needed, Hyper-V on 2019 supports passthrough only with specific hardware.
- **Sizing:** from the Phase 0 measurements (seconds per sheet, peak RAM, disk per job).
