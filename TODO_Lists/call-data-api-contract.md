# Call Data API Boundary — Data Contract

**Status:** proposed, not yet implemented. This is the handoff artifact for whoever builds the
Call Data API / Caller Tray — AI Toolbox owns and implements both HTTP calls described below;
the Call Data API team owns everything on their side of them (the SignalR push to the Caller Tray,
the tray's UI, and how a call gets correlated to a signed-in rep).
**Date:** 2026-09-01
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

`POST /api/call-events/entity-detected` — **illustrative path; final route is owned by the Call
Data API team.** AI Toolbox calls this endpoint using a `callevents:write`-scoped credential the
Call Data API issues to it.

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
    "speaker": "Customer"
  },
  "actions": [
    { "actionId": "search-carrier-pic", "label": "Search Carrier PIC for this model", "requiresConfirmation": false }
  ]
}
```

### Field notes

- **`callId`** — the RingCX call this entity was detected on. Whatever value the Listener service
  uses to identify the call must round-trip here; the Call Data API needs it to know which live
  call (and which rep's Caller Tray) to route the notification to.
- **`entityId`** — a unique id per detected entity, minted by AI Toolbox. This is the correlation
  key for the disposition webhook (§2) and should be treated as opaque.
- **`entityType`** — one of `model_number` | `part_number` | `serial_number` for this phase.
  Customer/contact/PO/job-name entity types are an explicit future phase, not yet built — do not
  assume this enum is closed indefinitely.
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
- **`actions`** — a list of `{actionId, label, requiresConfirmation}` entries. **This is a
  deliberate placeholder seam on the AI Toolbox side today** (`StaticEntityActionCatalog`, a
  small hardcoded per-entity-type mapping) — it is not yet backed by a live P21 action-capability
  system. The Call Data API should treat this list as authoritative and opaque (render `label`,
  post back whichever `actionId` the rep picks) rather than special-casing specific action ids;
  the set of possible actions and what `requiresConfirmation` implies operationally may expand
  once the P21-side action/mutation work (in progress separately) lands. Nothing about this
  contract needs to change when that happens.

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

## 3. Open items for the Call Data API team to resolve

These are flagged, not decided, on the AI Toolbox side — they're the Call Data API's design
questions to answer:

1. **Call-to-rep correlation.** AI Toolbox's outbound payload identifies the call (`callId`) but
   not the rep — the Call Data API needs its own mechanism for knowing which signed-in user's
   Caller Tray a given `callId` belongs to (likely via whatever RingCX/telephony integration or
   M365-authenticated session registration the Caller Tray already does on call start). AI
   Toolbox has no visibility into that mapping and doesn't propose to.
2. **Retry/redelivery semantics.** If the Call Data API is briefly unreachable, AI Toolbox
   currently just logs and moves on (§1). If guaranteed delivery matters more than that, the Call
   Data API team should specify what they need (e.g. an idempotent redelivery window keyed on
   `entityId`) and it can be added without changing the payload shape.
3. **Notification expiry.** How long a prompt should stay live in the Caller Tray before the Call
   Data API reports `disposition: "expired"` is a UX decision for that team, not specified here.
