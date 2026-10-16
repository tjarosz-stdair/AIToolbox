// A stand-in for the Ductwork API and pages, enough to run the real screens in a real browser. It serves the site's static
// files, three page shells (the same hooks the Razor views render), a one-page PDF, and the JSON the screens read, with a
// little state so that edits can be seen to happen. It also checks that every request carries what the real API requires.

import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const wwwroot = path.resolve(here, "../../../AIToolbox.Web/wwwroot");

export const PAGE_W = 612;
export const PAGE_H = 792;

const TYPES = { ".js": "text/javascript", ".mjs": "text/javascript", ".css": "text/css", ".html": "text/html", ".json": "application/json", ".pdf": "application/pdf" };

/** A small but valid PDF with live text, so pdf.js has something real to read and draw. */
export function makePdf(pages = 2) {
  const objects = [];
  const add = (body) => { objects.push(body); return objects.length; };
  add("<< /Type /Catalog /Pages 2 0 R >>");
  const kids = [];
  add("PLACEHOLDER");
  const fontId = add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
  for (let i = 1; i <= pages; i++) {
    const content = `BT /F1 14 Tf 40 700 Td (Unit plan ${i}) Tj ET BT /F1 18 Tf 500 40 Td (M1.${i}) Tj ET 0 0 1 RG 100 100 200 150 re S`;
    const contentId = add(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
    const pageId = add(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${PAGE_W} ${PAGE_H}] /Resources << /Font << /F1 ${fontId} 0 R >> >> /Contents ${contentId} 0 R >>`);
    kids.push(`${pageId} 0 R`);
  }

  objects[1] = `<< /Type /Pages /Kids [${kids.join(" ")}] /Count ${pages} >>`;
  let out = "%PDF-1.4\n";
  const offsets = [];
  objects.forEach((body, i) => { offsets.push(out.length); out += `${i + 1} 0 obj\n${body}\nendobj\n`; });
  const xref = out.length;
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => String(o).padStart(10, "0") + " 00000 n ").join("\n")}\ntrailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, "latin1");
}

const shell = (page, extra = "") => `<!doctype html><html><head><meta charset="utf-8"><title>smoke</title>
<link rel="stylesheet" href="/css/ductwork.css"></head><body>
<div id="dw-boot" hidden data-api="/ductwork/api" data-base="/Ductwork" data-job-id="${page === "job" ? 1 : 0}" data-email="owner@example.com" data-admin="true"
 data-pdfjs="/lib/pdfjs/pdf.min.mjs" data-pdfjs-worker="/lib/pdfjs/pdf.worker.min.mjs"></div>
<form id="dw-token-form"><input name="__RequestVerificationToken" value="test-token"></form>
<main class="container-fluid app-shell" style="padding:1rem"><div id="dw-app" data-page="${page}"></div></main>
<script type="module" src="/js/ductwork/${{ job: "workbench", jobs: "jobs-list", admin: "admin" }[page]}.js"></script>${extra}</body></html>`;

export function createState() {
  const iso = "2026-10-16T12:00:00Z";
  const item = (n, over = {}) => ({
    entityId: n, publicId: `e${n}`, jobId: 1, buildingTypeId: 1, producerId: 1, processingTaskId: 1, detectionId: `d${n}`, type: "Boot", category: null, tag: null, size: "4x10x7", manufacturer: null, model: null,
    customerPartCode: null, quantity: 1, unit: "EA", method: "Text", family: "LabelFlagCount", confidence: 0.9, surfacedAs: "Batch", reviewLevelAtCreation: "Sensitive", reviewStatus: "Proposed",
    disposition: "Included", resolutionSource: "Detector", isStale: false, sheetViewRef: "M1.1", box: { x0: 100 + n * 40, y0: 120, x1: 130 + n * 40, y1: 140 }, evidence: "label", revision: 1, sheetViewId: null,
    dispositionReason: null, resolvedByRuleId: null, viewId: null, sheetId: null, ...over
  });
  return {
    violations: [], calls: [], lockHeld: false, fencing: 0,
    job: { jobId: 1, publicId: "j1", name: "Whiteland Apartments", customerName: "Acme", projectName: "Phase 2", siteAddress: "1 Main St", ownerEmail: "owner@example.com", state: "Draft", completionVersion: 0, stateVersion: 3,
      reviewLevel: "Sensitive", baselineManualHours: null, createdUtc: iso, createdBy: "owner@example.com", updatedUtc: iso },
    members: [{ jobId: 1, userEmail: "owner@example.com", role: "Owner", addedUtc: iso, addedBy: "owner@example.com" }, { jobId: 1, userEmail: "ed@example.com", role: "Editor", addedUtc: iso, addedBy: "owner@example.com" }],
    tags: ["Bid"], steps: { Setup: "Done", Process: "Active", Review: "NotStarted", Report: "NotStarted" },
    files: [{ fileId: "f1", fileName: "mech.pdf", bytes: 4096, sha256: "a".repeat(64), contentType: "application/pdf", revision: 1, duplicateOf: null, uploadedBy: "owner@example.com", uploadedUtc: iso }],
    sheets: [
      { sheetId: "s1", fileId: "f1", pageNo: 1, sheetNumber: "M1.1", title: "Level 1", discipline: "Mechanical", kind: "Plan", widthPts: PAGE_W, heightPts: PAGE_H, rotation: 0, hasTextLayer: true, inScope: true, contentHash: null, duplicateOf: null, views: [], regions: [] },
      { sheetId: "s2", fileId: "f1", pageNo: 2, sheetNumber: "M1.2", title: "Level 2", discipline: "Mechanical", kind: "Plan", widthPts: PAGE_W, heightPts: PAGE_H, rotation: 0, hasTextLayer: false, inScope: true, contentHash: null, duplicateOf: null, views: [], regions: [] }
    ],
    types: [{ buildingTypeId: 1, publicId: "b1", jobId: 1, name: "Type 4", kind: "UnitType", multiplier: 26, multiplierSource: "Manual", multiplierEvidence: null, confirmed: true, confirmedBy: "owner@example.com", notes: null },
      { buildingTypeId: 2, publicId: "b2", jobId: 1, name: "Type 5", kind: "UnitType", multiplier: null, multiplierSource: null, multiplierEvidence: null, confirmed: false, confirmedBy: null, notes: null }],
    entities: [item(1), item(2, { size: "4x12x7", confidence: 0.5 }), item(3, { reviewStatus: "Verified", revision: 2 }), item(4, { disposition: "TBD", tag: "100 CFM", confidence: 0.3 }), item(5, { buildingTypeId: 2 }), item(6, { sheetViewRef: "M1.2" })],
    findings: [{ findingId: "fi1", entityId: "e2", item: "Boot 4x12x7", kind: "GrammarVariant", question: "A boot labelled 4x12: is the depth 7?", candidates: ["4x12x7", "4x12x4"], evidence: "label 4x12", state: "Open", resolvedByKind: null, resolvedRuleId: null, resolvedBy: null, resolvedUtc: null, resolutionNote: null, createdUtc: iso }],
    rules: [{ ruleId: "r1", ruleKey: "k1", version: 1, name: "Depth 7", description: "Convention", scope: "Job", buildingTypeId: null, sheetId: null, match: { type: "Boot", sizePattern: "^\\d+x\\d+$" }, action: { sizeSuffix: "x7" }, status: "Approved", createdBy: "owner@example.com", createdUtc: iso, approvedBy: "owner@example.com", approvedUtc: iso, retiredBy: null, retiredUtc: null, retireReason: null }],
    tasks: [{ processingTaskId: 1, publicId: "t1", jobId: 1, taskType: "detect-residential-labels", status: "Succeeded", cancelRequested: false, progressDone: 4, progressTotal: 4, progressMessage: "Done", attempt: 1, maxAttempts: 3, sendsDrawingContentToHostedModel: false, createdUtc: iso, createdBy: "owner@example.com", startedUtc: iso, finishedUtc: iso, errorMessage: null }],
    audit: [{ jobId: 1, eventId: 1, occurredUtc: iso, actorType: "User", actorEmail: "owner@example.com", actorDisplayName: "owner", category: "job", action: "job.created", summary: "Created the job.", beforeJson: null, afterJson: "{\"name\":\"x\"}", reason: null }],
    approvals: [], transmissions: [],
    uploads: new Map(), nextEntity: 100
  };
}

export function startServer(state = createState()) {
  const pdf = makePdf(2);
  const json = (res, status, body) => { res.writeHead(status, { "Content-Type": "application/json" }); res.end(body === undefined ? "" : JSON.stringify(body)); };
  const readBody = (req) => new Promise((resolve) => { const chunks = []; req.on("data", (c) => chunks.push(c)); req.on("end", () => resolve(Buffer.concat(chunks))); });
  const err = (res, status, code, error) => json(res, status, { code, error });

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, "http://x");
    const p = url.pathname;
    try {
      if (p === "/__state") return json(res, 200, { violations: state.violations, calls: state.calls, entities: state.entities, lockHeld: state.lockHeld, job: state.job, uploads: [...state.uploads.values()].map((u) => ({ id: u.id, received: u.received, total: u.total })), files: state.files, sheets: state.sheets, types: state.types, findings: state.findings, rules: state.rules, tags: state.tags, approvals: state.approvals, steps: state.steps });
      if (p === "/Ductwork") { res.writeHead(200, { "Content-Type": "text/html" }); return res.end(shell("jobs")); }
      if (/^\/Ductwork\/Jobs\/\d+$/.test(p)) { res.writeHead(200, { "Content-Type": "text/html" }); return res.end(shell("job")); }
      if (p === "/Ductwork/Admin") { res.writeHead(200, { "Content-Type": "text/html" }); return res.end(shell("admin")); }
      if (/\/files\/[^/]+\/content$/.test(p)) {
        res.writeHead(200, { "Content-Type": "application/pdf", "Content-Length": pdf.length, "Accept-Ranges": "bytes" });
        return res.end(pdf);
      }

      if (p.startsWith("/ductwork/api/")) return await api(req, res, url, p.slice("/ductwork/api".length), await readBody(req));

      const file = path.join(wwwroot, decodeURIComponent(p));
      if (!file.startsWith(wwwroot) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); return res.end("not found: " + p); }
      res.writeHead(200, { "Content-Type": TYPES[path.extname(file)] || "application/octet-stream" });
      return fs.createReadStream(file).pipe(res);
    } catch (e) {
      res.writeHead(500);
      res.end(String(e.stack || e));
    }
  });

  async function api(req, res, url, route, body) {
    const method = req.method;
    state.calls.push(`${method} ${route}${url.search}`);
    // What the real API demands of every request.
    if (!req.headers["x-ductwork-tab"] || req.headers["x-ductwork-tab"].length < 8) state.violations.push(`no tab id: ${method} ${route}`);
    if (method !== "GET" && req.headers.requestverificationtoken !== "test-token") state.violations.push(`no antiforgery token: ${method} ${route}`);
    // Everything that changes a job's content needs the fencing token. These do not: they are not edits of content.
    const exempt = /(\/tags|\/members|\/baseline|\/activity|\/lock|\/vision|\/uploads\/[^/]+$|\/rules\/preview|\/cancel|^\/jobs\/1\/(complete|close|reopen)|^\/admin)/;
    const needsLock = (m, r) => m !== "GET" && r.startsWith("/jobs/1") && !exempt.test(r);
    if (needsLock(method, route) && !req.headers["x-ductwork-fencing-token"]) {
      state.violations.push(`no fencing token: ${method} ${route}`);
      return err(res, 400, "bad-request", "Send the fencing token.");
    }

    const bodyJson = () => { try { return body.length ? JSON.parse(body.toString("utf8")) : {}; } catch { return {}; } };
    const m = (re) => route.match(re);
    const entity = (id) => state.entities.find((e) => e.publicId === id);
    const bump = (e, patch) => { Object.assign(e, patch); e.revision += 1; return e; };
    let r;

    if (method === "GET") {
      if (route === "/jobs") return json(res, 200, [state.job]);
      if (route === "/jobs/1") return json(res, 200, { job: state.job, myRole: "Owner", canEdit: true, members: state.members, tags: state.tags, steps: state.steps, lifecycleEditable: state.job.state === "Draft" });
      if (route === "/jobs/1/lock") return json(res, 200, { holder: null, youHoldIt: state.lockHeld, fencingToken: state.lockHeld ? state.fencing : null, pendingRequest: null });
      if (route === "/jobs/1/sheets") return json(res, 200, state.sheets);
      if (route === "/jobs/1/files") return json(res, 200, state.files);
      if (route === "/jobs/1/entities") return json(res, 200, state.entities);
      if (route === "/jobs/1/building-types") return json(res, 200, state.types);
      if (route === "/jobs/1/findings") return json(res, 200, state.findings);
      if (route === "/jobs/1/rules") return json(res, 200, state.rules);
      if ((r = m(/^\/jobs\/1\/rules\/([^/]+)\/preview$/))) return json(res, 200, { changes: [{ entityId: 2, entityPublicId: "e2", item: "Boot 4x12x7", change: "size 4x12 -> 4x12x7" }], skipped: [], conflicts: [] });
      if (route === "/jobs/1/tasks") return json(res, 200, state.tasks);
      if (route === "/jobs/1/audit") return json(res, 200, state.audit);
      if (route === "/jobs/1/audit/verify") return json(res, 200, { isValid: true, eventCount: 1, firstBadEventId: null, problem: null, headHash: "x" });
      if (route === "/jobs/1/activity") return json(res, 200, { jobId: 1, activeSeconds: 7200, idleSeconds: 600, sessions: 3, byUser: [{ userEmail: "owner@example.com", activeSeconds: 7200 }] });
      if (route === "/jobs/1/vision-approvals") return json(res, 200, state.approvals);
      if (route === "/jobs/1/vision-transmissions") return json(res, 200, state.transmissions);
      if (route === "/vision/policy") return json(res, 200, { enabled: true, noticeText: "Drawing content leaves this server.", noticeVersion: 2, approverRule: "EditPermission", requireReason: true, maxPagesPerApproval: 10, maxImagesPerPage: 6, approvalLifetimeMinutes: 60, providers: [{ providerId: 7, name: "Foundry Vision", model: "vision-1", governanceRoute: "Microsoft Foundry" }] });
      if (route === "/task-types") return json(res, 200, [{ taskType: "detect-residential-labels", sendsDrawingContent: false, maxAttempts: 3 }, { taskType: "classify-crops", sendsDrawingContent: true, maxAttempts: 2 }]);
      if ((r = m(/^\/jobs\/1\/entities\/([^/]+)\/history$/))) return json(res, 200, [{ entityRevisionId: 1, entityId: 3, revisionNo: 2, action: "Reviewed", changedBy: "owner@example.com", changedUtc: "2026-10-16T12:00:00Z", reason: null, beforeJson: "{}", afterJson: "{}", undoOfRevisionNo: null }]);
      if (route === "/jobs/1/count-sheet") {
        return json(res, 200, {
          jobId: 1, isFinal: false, unconfirmedBuildingTypes: ["Type 5"], openFindings: [{ findingId: "fi1", kind: "GrammarVariant", question: "A boot labelled 4x12: is the depth 7?", item: "Boot 4x12x7" }],
          openItems: [{ buildingType: "Type 4", type: "CfmOnlyTerminal", tag: "100 CFM", size: null, quantity: 1, unit: "EA", disposition: "TBD", reason: "Not decided" }],
          blocks: [{ buildingTypeId: 1, name: "Type 4", kind: "UnitType", multiplier: 26, multiplierSource: "Manual", multiplierEvidence: null, multiplierConfirmed: true, isUnassigned: false,
            lines: [{ category: null, tag: null, type: "Boot", size: "4x10x7", manufacturer: null, model: null, customerPartCode: null, unit: "EA", disposition: "Included", quantityPerBuilding: 3, multiplier: 26, extendedQuantity: 78, state: "Known", entityCount: 3, verifiedCount: 1, proposedCount: 2, staleCount: 0 }] },
          { buildingTypeId: 2, name: "Type 5", kind: "UnitType", multiplier: null, multiplierSource: null, multiplierEvidence: null, multiplierConfirmed: false, isUnassigned: false,
            lines: [{ category: null, tag: null, type: "Boot", size: "4x10x7", manufacturer: null, model: null, customerPartCode: null, unit: "EA", disposition: "Included", quantityPerBuilding: 1, multiplier: null, extendedQuantity: null, state: "MultiplierUnconfirmed", entityCount: 1, verifiedCount: 0, proposedCount: 1, staleCount: 0 }] }],
          totals: [{ category: null, tag: null, type: "Boot", size: "4x10x7", manufacturer: null, model: null, customerPartCode: null, unit: "EA", total: null, knownPart: 78, isComplete: false, pendingBuildingTypes: ["Type 5"], proposedCount: 3 }]
        });
      }
      if (route === "/admin/task-bindings") return json(res, 200, [{ taskBindingId: 1, taskType: "detect-residential-labels", producerId: 1, evalRunId: 1, sendsDrawingContentToHostedModel: false, maxAttempts: 3, activeFromUtc: "2026-10-16T12:00:00Z", retiredUtc: null, createdBy: "admin@example.com" }]);
      if (route === "/admin/task-bindings/producers") return json(res, 200, [{ producerId: 1, kind: "Algorithm", label: "residential-label-counter 0.1.0", supportsVision: false, evalRuns: [{ evalRunId: 1, taskKey: "bind", startedUtc: "2026-10-16T12:00:00Z", finishedUtc: null }] }]);
      if (route === "/admin/vision-policy") return json(res, 200, { policy: { enabled: false, allowedProviderIds: [], approverRule: "EditPermission", noticeText: "Notice", noticeVersion: 1, maxPagesPerApproval: 10, maxImagesPerPage: 6, requireReason: true, approvalLifetimeMinutes: 60, standingTaskTypes: [], standingMaxPagesPerJob: 0, changedBy: "", changedUtc: null }, providers: [{ providerId: 7, name: "Foundry Vision", model: "vision-1", governanceRoute: "Microsoft Foundry", enabled: true, eligible: true, whyNot: null }, { providerId: 8, name: "Plain", model: "text", governanceRoute: null, enabled: true, eligible: false, whyNot: "The provider is not vision-enabled." }] });
      let uploadStatus;
      if ((uploadStatus = m(/^\/jobs\/1\/uploads\/([^/]+)$/))) { const u = state.uploads.get(uploadStatus[1]); return u ? json(res, 200, { uploadId: u.id, receivedBytes: u.received, totalBytes: u.total, status: "Open", chunkBytes: u.chunk }) : err(res, 404, "not-found", "No such upload."); }
      return err(res, 404, "not-found", `mock has no GET ${route}`);
    }

    if (route === "/jobs/1/lock" && method === "POST") { state.lockHeld = true; state.fencing += 1; return json(res, 200, { outcome: "Acquired", fencingToken: state.fencing, holder: { email: "owner@example.com", name: "owner", isExpired: false } }); }
    if (route === "/jobs/1/lock" && method === "DELETE") { state.lockHeld = false; return json(res, 200, { released: true }); }
    if (route === "/jobs/1/lock/heartbeat") return json(res, 200, { stillHolder: state.lockHeld, message: null, pendingRequest: null });
    if (route === "/jobs/1/activity/heartbeat") return json(res, 200, { activeSeconds: 1, idleSeconds: 0, heartbeats: 1 });
    if (route === "/jobs/1/activity/end") return json(res, 200, { ended: true });

    if (method === "PUT" && (r = m(/^\/jobs\/1\/entities\/([^/]+)\/review$/))) {
      const e = entity(r[1]); const b = bodyJson();
      if (!e) return err(res, 404, "not-found", "No such item.");
      if (b.expectedRevision !== e.revision) return json(res, 409, { code: "version-conflict", error: "Changed by someone else.", actualRevision: e.revision });
      return json(res, 200, bump(e, { reviewStatus: b.status }));
    }
    if (method === "POST" && route === "/jobs/1/entities/review") {
      const b = bodyJson(); let changed = 0;
      for (const id of b.entityIds || []) { const e = entity(id); if (e && e.reviewStatus !== b.status) { bump(e, { reviewStatus: b.status }); changed++; } }
      return json(res, 200, { changed, requested: (b.entityIds || []).length });
    }
    if (method === "POST" && (r = m(/^\/jobs\/1\/entities\/([^/]+)\/undo$/))) { const e = entity(r[1]); return json(res, 200, bump(e, { reviewStatus: "Proposed" })); }
    if (method === "POST" && route === "/jobs/1/undo") { const e = state.entities.find((x) => x.revision > 1) || state.entities[0]; return json(res, 200, bump(e, { reviewStatus: "Proposed" })); }
    if (method === "PATCH" && (r = m(/^\/jobs\/1\/entities\/([^/]+)$/))) { const e = entity(r[1]); const b = bodyJson(); return json(res, 200, bump(e, { size: b.size ?? e.size, quantity: b.quantity ?? e.quantity })); }
    if (method === "POST" && route === "/jobs/1/entities") {
      const b = bodyJson();
      const e = { entityId: state.nextEntity, publicId: `e${state.nextEntity++}`, jobId: 1, buildingTypeId: b.buildingTypeId, type: b.type, size: b.size, tag: b.tag, quantity: b.quantity, unit: b.unit, method: "Manual", family: null, confidence: 1,
        reviewStatus: "Manual", disposition: "Included", resolutionSource: "UserAdded", isStale: false, sheetViewRef: null, box: { x0: b.box[0], y0: b.box[1], x1: b.box[2], y1: b.box[3] }, evidence: b.evidence, revision: 1, viewId: b.viewId, sheetId: "s1" };
      state.entities.push(e);
      return json(res, 201, e);
    }
    if (method === "POST" && (r = m(/^\/jobs\/1\/sheets\/([^/]+)\/views$/))) {
      const b = bodyJson(); const sheet = state.sheets.find((s) => s.sheetId === r[1]); const v = { viewId: `v${sheet.views.length + 1}`, title: b.title, floor: b.floor, type: b.type, buildingTypeId: b.buildingTypeId, polygon: null, scaleRatio: null, scaleMethod: null, scaleCalibration: null, scaleSetBy: null, scaleSetUtc: null, extractionMethod: "Manual" };
      sheet.views.push(v); return json(res, 201, v);
    }
    if (method === "PUT" && (r = m(/^\/jobs\/1\/views\/([^/]+)\/scale$/))) {
      const b = bodyJson(); for (const s of state.sheets) for (const v of s.views) if (v.viewId === r[1]) Object.assign(v, { scaleRatio: b.ratio, scaleMethod: b.method, scaleSetBy: "owner@example.com" });
      return json(res, 200, { viewId: r[1], scaleRatio: b.ratio });
    }
    if (method === "POST" && (r = m(/^\/jobs\/1\/sheets\/([^/]+)\/regions$/))) { const b = bodyJson(); const s = state.sheets.find((x) => x.sheetId === r[1]); const reg = { regionId: `rg${s.regions.length + 1}`, kind: b.kind, reason: b.reason, polygon: b.polygon }; s.regions.push(reg); return json(res, 201, reg); }
    if (method === "PATCH" && (r = m(/^\/jobs\/1\/sheets\/([^/]+)$/))) { const b = bodyJson(); const s = state.sheets.find((x) => x.sheetId === r[1]); for (const k of ["sheetNumber", "title", "kind", "inScope"]) if (b[k] !== undefined && b[k] !== null) s[k] = b[k]; return json(res, 200, s); }
    if (method === "POST" && (r = m(/^\/jobs\/1\/files\/([^/]+)\/sheets$/))) { const b = bodyJson(); return json(res, 201, (b.sheets || []).map((x) => ({ ...x, sheetId: `n${x.pageNo}`, fileId: r[1], views: [], regions: [], inScope: true, kind: x.kind || "Plan" }))); }
    if (method === "PUT" && route === "/jobs/1/details") { const b = bodyJson(); Object.assign(state.job, { name: b.name || state.job.name, stateVersion: state.job.stateVersion + 1 }); return json(res, 200, state.job); }
    if (method === "PUT" && (r = m(/^\/jobs\/1\/building-types\/(\d+)\/multiplier$/))) { const b = bodyJson(); const t = state.types.find((x) => x.buildingTypeId === Number(r[1])); Object.assign(t, { multiplier: b.multiplier, multiplierSource: b.source, confirmed: true }); return json(res, 200, t); }
    if (method === "POST" && route === "/jobs/1/building-types") { const b = bodyJson(); const t = { buildingTypeId: state.types.length + 1, name: b.name, kind: b.kind, multiplier: null, confirmed: false, notes: b.notes }; state.types.push(t); return json(res, 201, t); }
    if (method === "POST" && route === "/jobs/1/tags") { state.tags.push(bodyJson().tag); return json(res, 200, state.tags); }
    if (method === "PUT" && (r = m(/^\/jobs\/1\/workflow\/(\w+)$/))) { state.steps[r[1]] = bodyJson().status; return json(res, 200, state.steps); }
    if (method === "POST" && route === "/jobs/1/rules/preview") return json(res, 200, { changes: [{ entityId: 2, entityPublicId: "e2", item: "Boot 4x12x7", change: "size 4x12 -> 4x12x7" }], skipped: [], conflicts: [] });
    if (method === "POST" && route === "/jobs/1/rules") { const b = bodyJson(); const rule = { ruleId: `r${state.rules.length + 1}`, ruleKey: `k${state.rules.length + 1}`, version: 1, ...b, status: "Proposed", createdBy: "owner@example.com", createdUtc: "2026-10-16T12:00:00Z" }; state.rules.push(rule); return json(res, 201, rule); }
    if (method === "POST" && (r = m(/^\/jobs\/1\/rules\/([^/]+)\/approve$/))) { const rule = state.rules.find((x) => x.ruleId === r[1]); rule.status = "Approved"; return json(res, 200, rule); }
    if (method === "POST" && (r = m(/^\/jobs\/1\/rules\/([^/]+)\/apply$/))) { const e = entity("e2"); if (e) bump(e, { size: "4x12x7", resolutionSource: "Rule" }); return json(res, 200, { batchId: "b", changed: 1, skipped: 0, conflicts: 0, findingsRaised: 0 }); }
    if (method === "POST" && (r = m(/^\/jobs\/1\/findings\/([^/]+)\/resolve$/))) { const f = state.findings.find((x) => x.findingId === r[1]); Object.assign(f, { state: "Resolved", resolvedByKind: bodyJson().kind, resolvedBy: "owner@example.com" }); return json(res, 200, f); }
    if (method === "POST" && route === "/jobs/1/tasks") { const t = { ...state.tasks[0], processingTaskId: state.tasks.length + 1, status: "Queued", progressDone: 0, progressTotal: null, finishedUtc: null }; state.tasks.push(t); return json(res, 201, t); }
    if (method === "POST" && route === "/jobs/1/vision/disclosure") return json(res, 200, { canApprove: true, problems: [], pageCount: 1, imageBudget: 6, lifetimeMinutes: 60, noticeText: "Drawing content leaves this server.", noticeVersion: 2, requireReason: true, provider: { providerId: 7, name: "Foundry Vision", model: "vision-1", host: "foundry.example", governanceRoute: "Microsoft Foundry" }, sheets: [{ sheetId: "s2", sheetNumber: "M1.2", title: "Level 2", pageNo: 2, fileName: "mech.pdf" }] });
    if (method === "POST" && route === "/jobs/1/vision-approvals") { const a = { approvalId: "a1", kind: "PerRun", status: "Active", approvedBy: "owner@example.com", approvedUtc: "2026-10-16T12:00:00Z", expiresUtc: "2099-01-01T00:00:00Z", isExpired: false, sheetIds: ["s2"], pageCount: 1, imageBudget: 6, imagesSent: 0, reason: bodyJson().reason, noticeVersion: 2 }; state.approvals.push(a); return json(res, 201, a); }
    if (method === "PUT" && route === "/admin/vision-policy") return json(res, 200, bodyJson());
    if (method === "POST" && route === "/admin/task-bindings") return json(res, 201, bodyJson());

    // ---- uploads
    if (method === "POST" && route === "/jobs/1/uploads") {
      const b = bodyJson(); const id = `u${state.uploads.size + 1}`; const u = { id, total: b.totalBytes, received: 0, chunk: 4096, name: b.fileName, sha: b.sha256, bytes: [] };
      state.uploads.set(id, u); return json(res, 201, { uploadId: id, fileName: b.fileName, totalBytes: b.totalBytes, receivedBytes: 0, status: "Open", chunkBytes: u.chunk });
    }
    if (method === "PUT" && (r = m(/^\/jobs\/1\/uploads\/([^/]+)$/))) {
      const u = state.uploads.get(r[1]); const offset = Number(url.searchParams.get("offset"));
      if (offset !== u.received) return json(res, 409, { code: "offset-mismatch", error: "wrong offset", expectedOffset: u.received });
      if (!req.headers["x-chunk-sha256"] || req.headers["x-chunk-sha256"].length !== 64) state.violations.push("no chunk hash");
      u.bytes.push(body); u.received += body.length;
      return json(res, 200, { uploadId: u.id, receivedBytes: u.received, totalBytes: u.total, status: "Open", chunkBytes: u.chunk });
    }
    if (method === "POST" && (r = m(/^\/jobs\/1\/uploads\/([^/]+)\/complete$/))) {
      const u = state.uploads.get(r[1]); const all = Buffer.concat(u.bytes);
      const { createHash } = await import("node:crypto");
      if (u.sha && createHash("sha256").update(all).digest("hex") !== u.sha) return err(res, 422, "rule", "checksum mismatch");
      return json(res, 201, { fileId: `f-${u.id}`, fileName: u.name, bytes: all.length, sha256: u.sha, contentType: "application/pdf", revision: 1, duplicateOf: null, uploadedBy: "owner@example.com", uploadedUtc: "2026-10-16T12:00:00Z" });
    }

    return err(res, 404, "not-found", `mock has no ${method} ${route}`);
  }

  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => resolve({ server, state, port: server.address().port, close: () => new Promise((r) => server.close(r)) }));
  });
}
