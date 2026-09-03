# Call Data API Boundary — Data Contract

**Status:** IMPLEMENTED on both sides. AI Toolbox implements both HTTP calls described below
(`CallDataApiNotifier`/`CallEventDispositionController`); the Call Data API (RingCXAgentHubAPI,
"the Hub") implements the receiving/sending halves (`CallEventsController`/
`AiToolboxRelayService.ReportDispositionAsync`) plus the SignalR push to the Caller Tray, the
tray's UI, and call-to-rep correlation.
**Date:** 2026-09-01, updated after implementation.

**Auth correction from the original proposal below:** outbound (§1, AI Toolbox → Call Data API)
uses the Hub's own shared-secret scheme (`X-API-Key`, checked by `ApiKeyMiddleware` — the same
mechanism protecting every other Hub endpoint), not AI Toolbox's `ApiConsumerKey` scheme. Inbound
(§2, Call Data API → AI Toolbox) does use AI Toolbox's `ApiConsumerKey` scheme as originally
described (`X-AIToolbox-Api-Key` header, scopes `calltranscripts:write`/`callevents:disposition`).
**Source:** extracted from the "Real-Time Call Entity Extraction Pipeline" plan
(`C:\Users\tjarosz.STDAIR\.claude\plans\i-am-designing-a-zazzy-torvalds.md`), which covers the
full pipeline this contract is one boundary of — see that plan for how entities are detected and
validated before this fires, and for the P21 item/model/serial validation rules that back
`validation.status` below.

---

## Context

AI Toolbox runs a real-time entity-extraction pipeline over a live RingCentral RingCX call
transcript: it spots spoken model/part/serial-number candidates, validates them against P21, and
needs to notify a rep's desktop with an actionable prompt (e.g. "Search Carrier PIC for this
model"). That desktop notification is not AI Toolbox's job — it belongs to a separate Call Data
API / Caller Tray system (SignalR-based, OS-level toast notifications) owned by a different
effort. This document is the wire contract at that boundary, in both directions:

1. **Outbound** — AI Toolbox tells the Call Data API an entity was detected and validated.
2. **Inbound** — the Call Data API tells AI Toolbox what happened to that prompt (shown, picked,
   dismissed, expired), purely for logging/telemetry — this pipeline never triggers action
   execution itself.

Both directions authenticate using AI Toolbox's existing `ApiConsumerKey` scheme
(`AIToolbox.Web/Security/ApiKeyAuthenticationHandler.cs`), the same mechanism already used for
other machine-to-machine callers in this app — no new auth technology, just new scoped
credentials issued to/by the Call Data API.

---

## 1. Outbound: entity detected (AI Toolbox → Call Data API)

`POST /api/call-events/entity-detected` — implemented by `CallEventsController.PostEntityDetected`
on the Hub. Authenticated with the Hub's shared `X-API-Key` (not a `callevents:write`-scoped
AI Toolbox credential as originally proposed — see the auth correction above). The Hub resolves
which agent's tray to route the resulting prompt card to via `CallAgentRegistry`, an in-memory
`callId → agentKey` map populated on every transcript-segment relay (§ RingCXAgentHubAPI's own
`docs/AIToolboxContract.md`) — AI Toolbox's payload deliberately doesn't carry agent identity.

### Request body

```json
{
  "callId": "rcx-8f3ac2e1",
  "entityId": "ent-01J8Z3Q6X6C6R6F6",
  "utteranceId": "u-482",
  "detectedAtUtc": "2026-09-01T14:02:12.100Z",
  "entityType": "model_number",
  "canonicalValue": "24ABB324ABN3",
  "rawSpokenText": "24 ABB 324 ABN 3",
  "confidence": 0.93,
  "validation": {
    "status": "validated",
    "candidateValues": ["24ABB324ABN3"],
    "source": "p21_mcp:resolve_model"
  },
  "sourceUtterance": {
    "text": "the model number is 24 ABB 324 ABN 3, can you check on it",
    "offsetStart": 19,
    "offsetEnd": 39,
    "side": "External"
  },
  "actions": [
    { "actionId": "add-to-order", "label": "Add to Order", "requiresConfirmation": false },
    { "actionId": "search-carrier-pic", "label": "Open in PIC Search", "requiresConfirmation": false },
    { "actionId": "check-stock", "label": "Check Stock", "requiresConfirmation": false }
  ]
}
```

### Field notes

- **`callId`** — the RingCX call this entity was detected on. Whatever value the Listener service
  uses to identify the call must round-trip here; the Call Data API needs it to know which live
  call (and which rep's Caller Tray) to route the notification to.
- **`entityId`** — a unique id per detected entity, minted by AI Toolbox. This is the correlation
  key for the disposition webhook (§2) and should be treated as opaque. **The Hub uses this
  value verbatim as the tray-facing `AiPromptMessage.PromptId`** — it must never mint its own id
  for a card built from this notification, or disposition correlation breaks.
- **`entityType`** — one of `model_number` | `part_number` | `serial_number`, all three genuinely
  implemented (`CallEntityExtractionBackgroundService`/`P21ValidationResultMapper` distinguish
  Model from Part post-validation via presence of a PIC `pimCode` vs. a bare
  `primarySalesPackageId` — a heuristic on today's `resolve_model` response shape worth validating
  against real P21 data, not a distinct P21-side capability). Customer/contact/PO/job-name entity
  types are an explicit future phase, not yet built — do not assume this enum is closed
  indefinitely.
- **`canonicalValue`** — the P21/Carrier-canonical form of the identifier (e.g. despaced,
  correctly-cased), not necessarily identical to what was literally spoken.
- **`rawSpokenText`** — the despaced-candidate form as extracted from the transcript, kept for
  debugging/audit even though it's not what should be displayed or searched on.
- **`validation.status`** — one of:
  - `validated` — confirmed against P21 via the MCP facade.
  - `ambiguous` — P21 returned multiple plausible candidates; `candidateValues` has more than one
    entry and the Caller Tray should offer a quick picker rather than assuming the first one.
  - `not_found_in_window` — **serial numbers only.** The candidate is shape-valid but P21 has no
    record of it. This is *not* the same confidence class as a rejected guess — P21's own data
    only goes back roughly 10 years, and a serial referenced on a call can be for equipment up to
    20 years old. Treat this as "P21 has no record, not necessarily wrong" and still offer
    relevant actions (e.g. a Carrier PIC/warranty-history search that isn't bounded by P21's
    retention), rather than suppressing it as noise.
- **`actions`** — a list of `{actionId, label, requiresConfirmation}` entries, sourced from
  `StaticEntityActionCatalog`: Model Number and Serial Number both get `add-to-order`/
  `search-carrier-pic`/`check-stock`; Part Number gets `add-to-order`/`check-stock` only (no PIC
  search — PIC is a model/equipment catalog, not a supplier-part lookup). **This remains a
  deliberate placeholder seam** — it is not yet backed by a live P21 action-capability system.
  The Call Data API should treat this list as authoritative and opaque (render `label`, post back
  whichever `actionId` the rep picks) rather than special-casing specific action ids. What
  eventually happens after a rep clicks one of these buttons (driving P21's GUI via a Caller
  Tray → Chrome-extension message-passing path) is a separate, out-of-scope effort — clicking a
  button today only produces the disposition telemetry described in §2, nothing executes.

### Expected response

`202 Accepted`, body `{ "accepted": true }` or equivalent. AI Toolbox does not need a synchronous
result — only delivery confirmation. If this call fails or times out, AI Toolbox logs and
continues (does not retry indefinitely, does not crash the extraction pipeline) — treat a missed
notification as a real but non-fatal gap, not a signal to build elaborate retry logic into this
first version.

---

## 2. Inbound: disposition feedback (Call Data API → AI Toolbox)

`POST /api/call-events/{entityId}/disposition` — the Call Data API calls AI Toolbox using an
`ApiConsumerKey`-issued credential scoped `callevents:disposition`.

### Request body

```json
{
  "entityId": "ent-01J8Z3Q6X6C6R6F6",
  "callId": "rcx-8f3ac2e1",
  "disposition": "action_selected",
  "selectedActionId": "search-carrier-pic",
  "respondedAtUtc": "2026-09-01T14:02:20Z"
}
```

### Field notes

- **`disposition`** — one of `action_selected` | `dismissed` | `expired` | `delivery_failed`.
- **`selectedActionId`** — present only when `disposition == "action_selected"`; must be one of
  the `actionId` values AI Toolbox sent in the original entity-detected notification.
- No user-identifying fields (name, email) belong in this payload beyond whatever opaque id the
  Call Data API already uses internally for its own audit trail — this webhook doesn't need one
  for AI Toolbox's purposes.

### What this is for

Purely telemetry/logging on the AI Toolbox side:
- Feeds `ConversationUsageRollup`'s `EntitiesPromptedCount`/`ActionsAcceptedCount` counters
  (admin-visible usage stats — see the parent plan's "Logging, replay & admin reporting" section).
- Lets AI Toolbox avoid re-firing a notification for the same canonical value if it was just
  dismissed and the rep repeats it moments later (dedup suppression).

**This endpoint does not trigger any action execution.** AI Toolbox only records what happened —
actually running a PIC search, adding to an order, etc. is not this pipeline's responsibility in
this phase.

### Expected response

`202 Accepted`, body `{ "recorded": true }` or equivalent.

---

## 3. Open items

1. ~~**Call-to-rep correlation.**~~ **Resolved.** The Hub's `CallAgentRegistry` maps `callId` →
   `agentKey` in-memory, populated from every transcript-segment relay it already makes (which
   carries a resolved `agentKey`) before any entity is ever detected for that call. A callId the
   Hub has no record of (e.g. registry entry expired after its 4-hour TTL) is logged and dropped
   rather than guessed at.
2. **Retry/redelivery semantics.** If the Call Data API is briefly unreachable, AI Toolbox
   currently just logs and moves on (§1). If guaranteed delivery matters more than that, the Call
   Data API team should specify what they need (e.g. an idempotent redelivery window keyed on
   `entityId`) and it can be added without changing the payload shape. Still open.
3. **Notification expiry.** How long a prompt should stay live in the Caller Tray before the Call
   Data API reports `disposition: "expired"` is a UX decision for that team. Still open — the
   Caller Tray doesn't yet report `dismissed`/`expired` dispositions at all (only
   `action_selected` today); see RingCXAgentHubAPI's own contract doc for that follow-up work.
