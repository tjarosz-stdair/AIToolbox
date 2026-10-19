// Tests of the Ductwork screens' pure logic, run with `node --test`. The modules are the real ones the browser loads.
import test from "node:test";
import assert from "node:assert/strict";
import * as g from "../../AIToolbox.Web/wwwroot/js/ductwork/geometry.js";
import * as m from "../../AIToolbox.Web/wwwroot/js/ductwork/model.js";
import { createApi, ApiError, qs } from "../../AIToolbox.Web/wwwroot/js/ductwork/api.js";
import { LockManager } from "../../AIToolbox.Web/wwwroot/js/ductwork/lock.js";
import { Upload, sha256Hex, UploadCancelled } from "../../AIToolbox.Web/wwwroot/js/ductwork/uploader.js";

const close = (a, b, eps = 1e-9) => assert.ok(Math.abs(a - b) < eps, `${a} is not within ${eps} of ${b}`);

// ------------------------------------------------------------------ geometry

test("a page is fitted inside the viewport and centred", () => {
  const v = g.fitView(2592, 1728, 1000, 800);
  close(v.scale, (1000 - 32) / 2592);
  close(v.x + 2592 * v.scale / 2, 500);
  close(v.y + 1728 * v.scale / 2, 400);
  assert.equal(g.fitScale(0, 0, 100, 100), 1);
});

test("zooming keeps the point under the cursor where it was and respects the limits", () => {
  const v = { scale: 1, x: 10, y: 20 };
  const z = g.zoomAt(v, 2, 300, 200);
  const before = g.toPage(v, 300, 200);
  const after = g.toPage(z, 300, 200);
  close(before[0], after[0]);
  close(before[1], after[1]);
  assert.equal(g.zoomAt({ scale: g.MAX_SCALE, x: 0, y: 0 }, 10, 0, 0).scale, g.MAX_SCALE);
  assert.equal(g.zoomAt({ scale: g.MIN_SCALE, x: 0, y: 0 }, 0.01, 0, 0).scale, g.MIN_SCALE);
});

test("screen and page coordinates round trip", () => {
  const v = { scale: 0.37, x: 41, y: -9 };
  const [sx, sy] = g.toScreen(v, 123.4, 567.8);
  const [px, py] = g.toPage(v, sx, sy);
  close(px, 123.4);
  close(py, 567.8);
});

test("the smallest box under the point is hit, and a tiny box is still reachable", () => {
  const v = { scale: 1, x: 0, y: 0 };
  const items = [
    { id: "region", box: { x0: 0, y0: 0, x1: 500, y1: 500 } },
    { id: "label", box: { x0: 100, y0: 100, x1: 160, y1: 120 } },
    { id: "dot", box: { x0: 300, y0: 300, x1: 301, y1: 301 } },
    { id: "none" }
  ];
  assert.equal(g.hitTest(v, items, 110, 110).id, "label");
  assert.equal(g.hitTest(v, items, 400, 400).id, "region");
  assert.equal(g.hitTest(v, items, 304, 303).id, "dot");
  assert.equal(g.hitTest(v, items, 900, 900), null);
});

test("only boxes that touch the viewport are drawn", () => {
  const v = { scale: 1, x: 0, y: 0 };
  const items = [{ id: "in", box: { x0: 10, y0: 10, x1: 20, y1: 20 } }, { id: "out", box: { x0: 5000, y0: 10, x1: 5010, y1: 20 } }, { id: "edge", box: { x0: 990, y0: 10, x1: 1100, y1: 20 } }];
  assert.deepEqual(g.visibleItems(v, items, 1000, 800).map((i) => i.id), ["in", "edge"]);
});

test("focusing a box centres it and zooms in, but never beyond the limit", () => {
  const v = g.focusBox({ scale: 0.2, x: 0, y: 0 }, { x0: 1000, y0: 500, x1: 1040, y1: 520 }, 800, 600);
  const [sx, sy] = g.toScreen(v, 1020, 510);
  close(sx, 400);
  close(sy, 300);
  assert.ok(v.scale > 0.2 && v.scale <= 4);
  assert.equal(g.focusBox({ scale: 8, x: 0, y: 0 }, { x0: 0, y0: 0, x1: 1, y1: 1 }, 800, 600).scale, 4);
});

test("a scale calibrated from two points is the real length per drawn length", () => {
  // A quarter inch on paper is 18 points. If it stands for one foot, the scale is 48.
  close(g.ratioFromCalibration([0, 0], [18, 0], 1), 48);
  close(g.ratioFromCalibration([0, 0], [0, 36], 2), 48);
  assert.equal(g.ratioFromCalibration([5, 5], [5, 5], 3), null);
  assert.equal(g.ratioFromCalibration([0, 0], [10, 0], 0), null);
  close(g.feetFor(18, 48), 1);
});

test("standard scales are named and an odd one is shown as a ratio", () => {
  assert.equal(g.scaleLabel(48), "1/4\" = 1'-0\"");
  assert.equal(g.scaleLabel(null), "not set");
  assert.equal(g.scaleLabel(47.5), "1 : 47.5");
});

test("a rectangle becomes a four-point polygon whichever corner is first, and a placed box stays on the page", () => {
  assert.deepEqual(g.rectPolygon([10, 20], [4, 2]), [[4, 2], [10, 2], [10, 20], [4, 20]]);
  assert.deepEqual(g.boxAround([5, 5], 20, 100, 100), { x0: 0, y0: 0, x1: 15, y1: 15 });
  assert.deepEqual(g.boxAround([500, 500], 20, 100, 100), { x0: 90, y0: 90, x1: 100, y1: 100 });
});

// ------------------------------------------------------------------ model

const sheet = { sheetId: "s1", sheetNumber: "M1.2", pageNo: 2, title: "Level 1 plan" };
const e = (id, over = {}) => ({ entityId: id, publicId: `e${id}`, type: "Boot", size: "4x10x7", tag: null, quantity: 1, confidence: 0.9, reviewStatus: "Proposed", disposition: "Included", buildingTypeId: 1, sheetId: null, sheetViewRef: "M1.2", ...over });

test("a detector's sheet reference matches the sheet number loosely, and a placed item wins over its reference", () => {
  assert.ok(m.refMatchesSheet("m1.2", sheet));
  assert.ok(m.refMatchesSheet(" M1 .2 ", sheet));
  assert.ok(m.refMatchesSheet("p2", sheet));
  assert.ok(!m.refMatchesSheet("M1.3", sheet));
  assert.ok(!m.refMatchesSheet(null, sheet));
  const items = [e(1), e(2, { sheetId: "s1", sheetViewRef: "elsewhere" }), e(3, { sheetId: "s9", sheetViewRef: "M1.2" })];
  assert.deepEqual(m.entitiesForSheet(items, sheet).map((x) => x.entityId), [1, 2]);
  assert.equal(m.sheetOf(items[1], [sheet]).sheetId, "s1");
  assert.equal(m.sheetOf(items[2], [sheet]), null);
});

test("filters combine, rejected items are hidden unless asked for, and an empty filter keeps everything visible", () => {
  const items = [e(1), e(2, { reviewStatus: "Verified" }), e(3, { reviewStatus: "Rejected" }), e(4, { confidence: 0.3, disposition: "TBD", tag: "100 CFM" })];
  assert.deepEqual(m.filterEntities(items, {}).map((x) => x.entityId), [1, 2, 4]);
  assert.deepEqual(m.filterEntities(items, { showRejected: true }).map((x) => x.entityId), [1, 2, 3, 4]);
  assert.deepEqual(m.filterEntities(items, { statuses: new Set(["Rejected"]) }).map((x) => x.entityId), [3]);
  assert.deepEqual(m.filterEntities(items, { dispositions: new Set(["TBD"]) }).map((x) => x.entityId), [4]);
  assert.deepEqual(m.filterEntities(items, { maxConfidence: 0.5 }).map((x) => x.entityId), [4]);
  assert.deepEqual(m.filterEntities(items, { text: "100cfm" }).map((x) => x.entityId), [4]);
  assert.deepEqual(m.filterEntities(items, { buildingTypeId: "2" }).map((x) => x.entityId), []);
});

test("the tree groups by building type then kind of item, orders naturally and counts what is still proposed", () => {
  const types = [{ buildingTypeId: 1, name: "Type 10" }, { buildingTypeId: 2, name: "Type 2" }];
  const items = [e(1), e(2, { size: "4x10x7" }), e(3, { size: "4x9x7" }), e(4, { buildingTypeId: 2, reviewStatus: "Verified" }), e(5, { buildingTypeId: null })];
  const tree = m.buildTree(items, types);
  assert.deepEqual(tree.map((t) => t.label), ["Type 2", "Type 10", "Not assigned to a building type"]);   // natural order, unassigned last
  const t10 = tree[1];
  assert.deepEqual(t10.groups.map((x) => x.label), ["Boot 4x9x7", "Boot 4x10x7"]);
  assert.deepEqual([t10.count, t10.proposed, t10.quantity], [3, 3, 3]);
  assert.equal(tree[0].proposed, 0);
});

test("stepping through a list wraps and starts at the first item", () => {
  const list = [e(1), e(2), e(3)];
  assert.equal(m.stepItem(list, null, 1).entityId, 1);
  assert.equal(m.stepItem(list, null, -1).entityId, 3);
  assert.equal(m.stepItem(list, "e3", 1).entityId, 1);
  assert.equal(m.stepItem(list, "e1", -1).entityId, 3);
  assert.equal(m.stepItem([], null, 1), null);
});

test("the review queue is proposals by lowest confidence and bulk verification never includes undetermined items", () => {
  const items = [e(1, { confidence: 0.9 }), e(2, { confidence: 0.4 }), e(3, { reviewStatus: "Verified" }), e(4, { confidence: 0.99, disposition: "TBD" })];
  assert.deepEqual(m.reviewQueue(items).map((x) => x.entityId), [2, 1, 4]);
  assert.deepEqual(m.bulkVerifiable(items, 0.8).map((x) => x.entityId), [1]);
});

test("the count sheet says in plain words what stops it being final", () => {
  const blockers = m.countSheetBlockers({ unconfirmedBuildingTypes: ["Type 4"], openItems: [{}], openFindings: [{}, {}], totals: [{ proposedCount: 2 }, { proposedCount: 1 }] });
  assert.equal(blockers.length, 4);
  assert.match(blockers[0], /Type 4/);
  assert.match(blockers[2], /2 question/);
  assert.match(blockers[3], /3 counted item/);
  assert.deepEqual(m.countSheetBlockers({ totals: [] }), []);
});

// ------------------------------------------------------------------ api

function fakeFetch(script) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, init });
    const step = script.shift();
    if (step instanceof Error) throw step;
    const { status = 200, body = null } = step;
    const text = body === null ? "" : typeof body === "string" ? body : JSON.stringify(body);
    return { ok: status >= 200 && status < 300, status, text: async () => text };
  };
  fn.calls = calls;
  return fn;
}

test("requests carry the tab id and antiforgery token, and writes carry the fencing token once the lock is held", async () => {
  const f = fakeFetch([{ body: { a: 1 } }, { body: { b: 2 } }, { body: { c: 3 } }]);
  const api = createApi({ base: "/x/ductwork/api/", tabId: "tab-1", antiforgery: () => "csrf", fetchImpl: f });
  await api.get("/jobs");
  api.setFencing(77);
  await api.post("/jobs/1/tags", { tag: "a" });
  await api.get("/jobs/1");
  assert.equal(f.calls[0].url, "/x/ductwork/api/jobs");
  const [read, write, read2] = f.calls.map((c) => c.init.headers);
  assert.equal(read["X-Ductwork-Tab"], "tab-1");
  assert.equal(read.RequestVerificationToken, "csrf");
  assert.equal(read["X-Ductwork-Fencing-Token"], undefined);
  assert.equal(write["X-Ductwork-Fencing-Token"], "77");
  assert.equal(write["Content-Type"], "application/json");
  assert.equal(read2["X-Ductwork-Fencing-Token"], undefined);          // reads never need it
  assert.equal(f.calls[1].init.body, JSON.stringify({ tag: "a" }));
});

test("a failure carries the server's code and words, and the helpers recognise the cases screens care about", async () => {
  const f = fakeFetch([
    { status: 409, body: { code: "lock-lost", error: "You no longer hold the lock." } },
    { status: 409, body: { code: "version-conflict", error: "Changed by someone else." } },
    { status: 404, body: { code: "not-found", error: "No such job." } },
    { status: 500, body: "<html>boom</html>" },
    new TypeError("failed to fetch"),
    { status: 204 }
  ]);
  const api = createApi({ base: "/api", tabId: "t", fetchImpl: f });
  const lost = await api.post("/a").catch((x) => x);
  assert.ok(lost instanceof ApiError && lost.isLockLost && lost.message === "You no longer hold the lock.");
  assert.ok((await api.post("/a").catch((x) => x)).isVersionConflict);
  assert.ok((await api.get("/a").catch((x) => x)).isNotFound);
  assert.equal((await api.get("/a").catch((x) => x)).message, "The server answered 500.");   // markup is never shown to a person
  const net = await api.get("/a").catch((x) => x);
  assert.equal(net.code, "network");
  assert.equal(await api.get("/a"), null);
});

test("a query string leaves out what is empty and repeats what is a list", () => {
  assert.equal(qs({ a: 1, b: "", c: null, d: undefined }), "?a=1");
  assert.equal(qs({ state: ["Draft", "Complete"], q: "a b" }), "?state=Draft&state=Complete&q=a%20b");
  assert.equal(qs({}), "");
});

// ------------------------------------------------------------------ lock

function fakeLockApi(script) {
  const calls = [];
  return {
    calls, fencing: null, script,
    setFencing(v) { this.fencing = v; },
    async get(path) { calls.push(["GET", path]); return this.script.get?.(path); },
    async post(path) { calls.push(["POST", path]); return this.script.post?.(path); },
    async del(path) { calls.push(["DELETE", path]); return this.script.del?.(path); },
    request(method, path) { calls.push([method, path, "keepalive"]); }
  };
}

const noTimers = { setInterval: () => 1, clearInterval: () => {} };

test("taking the lock gives the api its fencing token and keeps it alive until it is lost", async () => {
  let beat = () => {};
  const timers = { setInterval: (fn) => { beat = fn; return 1; }, clearInterval: () => { beat = null; } };
  const api = fakeLockApi({ post: (p) => (p.endsWith("/lock") ? { outcome: "Acquired", fencingToken: 42, holder: { email: "me" } } : { stillHolder: true }) });
  const lock = new LockManager(api, 5, { ...timers });

  assert.equal(await lock.acquire(), true);
  assert.equal(api.fencing, 42);
  await lock.beat();
  assert.ok(lock.holding);

  api.script.post = async () => ({ stillHolder: false, message: "Someone took over." });
  await lock.beat();
  assert.equal(lock.holding, false);
  assert.equal(api.fencing, null);
  assert.equal(lock.state.lost, true);
  assert.equal(lock.state.message, "Someone took over.");
  assert.equal(beat, null);                                              // the heartbeat stopped
});

test("a lock held by someone else is not taken and says who has it", async () => {
  const api = fakeLockApi({ post: () => ({ outcome: "HeldByOther", fencingToken: null, holder: { name: "Pat", email: "pat@example.com" } }) });
  const lock = new LockManager(api, 5, noTimers);
  assert.equal(await lock.acquire(), false);
  assert.equal(api.fencing, null);
  assert.match(lock.state.message, /Pat is editing/);
});

test("a takeover request either takes the lock at once or waits, and the holder can hand over", async () => {
  const api = fakeLockApi({ post: () => ({ granted: false, message: "Asked Pat.", request: { id: 9 } }) });
  const lock = new LockManager(api, 5, noTimers);
  const r = await lock.requestTakeover();
  assert.equal(r.granted, false);
  assert.equal(lock.state.pendingRequest.id, 9);

  api.script.post = async () => ({ granted: true, fencingToken: 55, holder: { email: "me" } });
  await lock.requestTakeover();
  assert.equal(lock.holding, true);
  assert.equal(api.fencing, 55);

  api.script.post = async () => ({ handedOver: true });
  await lock.respond(9, true);
  assert.equal(lock.holding, false);
  assert.equal(api.fencing, null);
});

test("an edit that comes back lock-lost marks the lock lost without another round trip", async () => {
  const api = fakeLockApi({ post: () => ({ outcome: "Acquired", fencingToken: 7, holder: {} }) });
  const lock = new LockManager(api, 5, noTimers);
  await lock.acquire();
  const calls = api.calls.length;
  lock.markLost("The lock moved on.");
  assert.deepEqual([lock.holding, lock.state.lost, api.fencing, api.calls.length], [false, true, null, calls]);
});

test("releasing frees the lock and a page that is closing asks without waiting", async () => {
  const api = fakeLockApi({ post: () => ({ outcome: "Acquired", fencingToken: 7, holder: {} }) });
  const lock = new LockManager(api, 5, noTimers);
  await lock.acquire();
  lock.releaseOnUnload();
  assert.deepEqual(api.calls.at(-1), ["DELETE", "/jobs/5/lock", "keepalive"]);
  await lock.release();
  assert.equal(lock.holding, false);
});

// ------------------------------------------------------------------ uploader

function fileOf(text, name = "plan.pdf") {
  const blob = new Blob([text]);
  return Object.assign(blob, { name });
}

function uploadApi({ chunk = 4, failures = [] } = {}) {
  const log = { puts: [], posts: [], gets: 0, dels: 0 };
  let received = 0;
  const api = {
    log,
    async post(path, body) {
      log.posts.push([path, body]);
      if (path.endsWith("/uploads")) return { uploadId: "u1", chunkBytes: chunk, receivedBytes: 0 };
      return { fileId: "f1", fileName: "plan.pdf" };
    },
    async get() { log.gets++; return { receivedBytes: received }; },
    async del() { log.dels++; return { cancelled: true }; },
    async request(method, path, opts) {
      const offset = Number(new URL("http://x" + path).searchParams.get("offset"));
      log.puts.push({ offset, size: opts.rawBody.size, hash: opts.headers["X-Chunk-Sha256"] });
      const failure = failures.shift();
      if (failure) {
        if (failure.landed !== undefined) received = failure.landed;
        throw failure.error;
      }
      received = offset + opts.rawBody.size;
      return { receivedBytes: received };
    }
  };
  return api;
}

test("a file is sent in the pieces the server asks for, each with its own hash, then completed with the whole file's hash", async () => {
  const api = uploadApi({ chunk: 4 });
  const file = fileOf("0123456789");
  const progress = [];
  const result = await new Upload({ api, jobId: 3, file, onProgress: (p) => progress.push(p.state) }).run();

  assert.deepEqual(api.log.puts.map((p) => [p.offset, p.size]), [[0, 4], [4, 4], [8, 2]]);
  assert.equal(api.log.puts[0].hash, await sha256Hex(fileOf("0123")));
  assert.equal(api.log.posts[0][1].sha256, await sha256Hex(file));
  assert.equal(api.log.posts[0][1].totalBytes, 10);
  assert.equal(api.log.posts[1][0], "/jobs/3/uploads/u1/complete");
  assert.equal(result.fileId, "f1");
  assert.equal(progress.at(-1), "done");
});

test("a piece that failed in transit but had landed is not sent twice", async () => {
  const net = Object.assign(new Error("net"), { code: "network", status: 0 });
  // The first piece's answer is lost, but the server has it (4 bytes). Asking tells the upload to carry on from there.
  const api = uploadApi({ chunk: 4, failures: [{ error: net, landed: 4 }] });
  await new Upload({ api, jobId: 1, file: fileOf("0123456789"), retryDelaysMs: [0, 0, 0] }).run();
  assert.deepEqual(api.log.puts.map((p) => p.offset), [0, 4, 8]);
  assert.equal(api.log.gets, 1);
});

test("transient failures are retried a limited number of times and a refusal is not retried", async () => {
  const unavailable = Object.assign(new Error("busy"), { status: 503 });
  let api = uploadApi({ chunk: 100, failures: [{ error: unavailable }, { error: unavailable }] });
  await new Upload({ api, jobId: 1, file: fileOf("hello"), retryDelaysMs: [0, 0, 0] }).run();
  assert.equal(api.log.puts.length, 3);

  const forbidden = Object.assign(new Error("no"), { status: 403, code: "forbidden" });
  api = uploadApi({ chunk: 100, failures: [{ error: forbidden }] });
  await assert.rejects(new Upload({ api, jobId: 1, file: fileOf("hello"), retryDelaysMs: [0, 0, 0] }).run(), (x) => x.status === 403);
  assert.equal(api.log.puts.length, 1);

  api = uploadApi({ chunk: 100, failures: Array.from({ length: 10 }, () => ({ error: unavailable })) });
  await assert.rejects(new Upload({ api, jobId: 1, file: fileOf("hello"), retryDelaysMs: [0, 0] }).run(), (x) => x.status === 503);
  assert.equal(api.log.puts.length, 3);                                  // the first try and two retries
});

test("a server that is somewhere else tells the upload where to carry on from", async () => {
  const mismatch = Object.assign(new Error("offset"), { status: 409, code: "offset-mismatch", body: { expectedOffset: 8 } });
  const api = uploadApi({ chunk: 4, failures: [{ error: mismatch }] });
  await new Upload({ api, jobId: 1, file: fileOf("0123456789") }).run();
  assert.deepEqual(api.log.puts.map((p) => p.offset), [0, 8]);
});

test("cancelling stops sending and tells the server to drop what it holds", async () => {
  const api = uploadApi({ chunk: 4 });
  const upload = new Upload({ api, jobId: 1, file: fileOf("0123456789"), onProgress: ({ sent }) => { if (sent >= 4) upload.cancel(); } });
  await assert.rejects(upload.run(), UploadCancelled);
  assert.equal(api.log.dels, 1);
  assert.ok(api.log.puts.length <= 2);
});

test("a big file skips the whole-file hash but every piece still carries one", async () => {
  const api = uploadApi({ chunk: 4 });
  await new Upload({ api, jobId: 1, file: fileOf("01234567"), hashFile: false }).run();
  assert.equal(api.log.posts[0][1].sha256, null);
  assert.ok(api.log.puts.every((p) => p.hash.length === 64));
});

// ------------------------------------------------------------------ archive wording

test("an operation is described in plain words, with progress while it runs and the reason when it stopped", async () => {
  const { operationLine, isActive } = await import("../../AIToolbox.Web/wwwroot/js/ductwork/archive-ui.js");
  assert.equal(operationLine({ kind: "ArchivePurge", status: "Running", message: "Removing Entity.", progressDone: 25, progressTotal: 100 }), "Archiving and removing from the database: Removing Entity. (25%)");
  assert.equal(operationLine({ kind: "Restore", status: "Queued" }), "Restoring: waiting for the server to start");
  assert.equal(operationLine({ kind: "ArchiveZip", status: "Failed", error: "The archive failed verification." }), "Archiving stopped: The archive failed verification.");
  assert.equal(operationLine(null), "");
  assert.ok(isActive({ status: "Queued" }) && isActive({ status: "Running" }) && !isActive({ status: "Failed" }) && !isActive(null));
});

test("progress never reads past one hundred percent", async () => {
  const { operationLine } = await import("../../AIToolbox.Web/wwwroot/js/ductwork/archive-ui.js");
  assert.match(operationLine({ kind: "Restore", status: "Running", message: "x", progressDone: 150, progressTotal: 100 }), /\(100%\)$/);
});

// ------------------------------------------------------------------ views on a sheet

test("a view is named by its title and its scale, or just its title when no scale is set", () => {
  assert.equal(g.viewLabel({ title: "1. LEVEL 1 PLAN", scaleRatio: 96 }), "1. LEVEL 1 PLAN: 1/8\" = 1'-0\"");
  assert.equal(g.viewLabel({ title: "2. ROOF DETAIL", scaleRatio: null }), "2. ROOF DETAIL");
  assert.equal(g.viewLabel({ title: "ODD", scaleRatio: 37 }), "ODD: 1 : 37");
});

// ------------------------------------------------------------------ live progress

function fakeHub({ failStart = 0 } = {}) {
  const hub = { calls: [], handlers: {}, starts: 0, stops: 0 };
  hub.connection = {
    on: (name, fn) => { hub.handlers[name] = fn; },
    onreconnecting: (fn) => { hub.handlers.reconnecting = fn; },
    onreconnected: (fn) => { hub.handlers.reconnected = fn; },
    onclose: (fn) => { hub.handlers.close = fn; },
    start: async () => { hub.starts++; if (hub.starts <= failStart) throw new Error("no hub"); },
    stop: async () => { hub.stops++; },
    invoke: async (name, ...args) => { hub.calls.push([name, ...args]); }
  };
  return hub;
}

async function progressClient(hub, extra = {}) {
  const { ProgressClient } = await import("../../AIToolbox.Web/wwwroot/js/ductwork/progress.js");
  const timers = [];
  const client = new ProgressClient({ signalr: "/lib/signalr.js", progressHub: "/hubs/x" }, {
    createConnection: async () => hub.connection, setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout: () => {}, ...extra
  });
  return { client, timers };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

test("watching a job asks the hub for it and hands each pushed change to the page", async () => {
  const hub = fakeHub();
  const { client } = await progressClient(hub);
  const seen = [];

  client.watch(7, (events, info) => seen.push([events, info]));
  await tick();
  hub.handlers.progress(7, [{ kind: "task", id: "1", status: "Running" }]);
  hub.handlers.progress(8, [{ kind: "task", id: "2" }]);   // another job's change is not this page's

  assert.deepEqual(hub.calls, [["Watch", 7]]);
  assert.equal(client.connected, true);
  assert.deepEqual(seen, [[[{ kind: "task", id: "1", status: "Running" }], { resync: false }]]);
});

test("a page that stops watching tells the hub, and a second page on the same job keeps it", async () => {
  const hub = fakeHub();
  const { client } = await progressClient(hub);
  const stopA = client.watch(7, () => {});
  const stopB = client.watch(7, () => {});
  await tick();

  stopA();
  assert.deepEqual(hub.calls.filter((c) => c[0] === "Unwatch"), []);
  stopB();
  assert.deepEqual(hub.calls.filter((c) => c[0] === "Unwatch"), [["Unwatch", 7]]);
});

test("after the connection comes back every job is watched again and the page is told to re-read", async () => {
  const hub = fakeHub();
  const { client } = await progressClient(hub);
  const states = [];
  client.onState((s) => states.push(s));
  const seen = [];
  client.watch(7, (events, info) => seen.push(info.resync));
  await tick();

  hub.handlers.reconnecting();
  assert.equal(client.connected, false);
  await hub.handlers.reconnected();

  assert.equal(client.connected, true);
  assert.deepEqual(hub.calls, [["Watch", 7], ["Watch", 7]]);
  assert.deepEqual(seen, [true]);
  assert.deepEqual(states, [true, false, true]);
});

test("a hub that will not start leaves the page polling, and the client tries again later", async () => {
  const hub = fakeHub({ failStart: 1 });
  const { client, timers } = await progressClient(hub);

  client.watch(7, () => {});
  await tick();
  assert.equal(client.connected, false);
  assert.equal(timers.length, 1);

  await timers[0].fn();
  assert.equal(client.connected, true);
  assert.deepEqual(hub.calls, [["Watch", 7]]);
});

test("without the hub address or script the client does nothing at all", async () => {
  const { ProgressClient } = await import("../../AIToolbox.Web/wwwroot/js/ductwork/progress.js");
  const client = new ProgressClient({});

  const stop = client.watch(7, () => assert.fail("no events"));
  stop();

  assert.equal(client.connected, false);
});

test("settle runs once after a burst of requests", async () => {
  const { settle } = await import("../../AIToolbox.Web/wwwroot/js/ductwork/progress.js");
  const timers = new Map();
  let n = 0;
  const run = settle(() => n++, 400, { setTimeout: (fn) => { const id = timers.size + 1; timers.set(id, fn); return id; }, clearTimeout: (id) => timers.delete(id) });

  run(); run(); run();
  assert.equal(timers.size, 1);
  [...timers.values()][0]();

  assert.equal(n, 1);
});
