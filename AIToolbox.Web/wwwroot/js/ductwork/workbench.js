// The job workbench: one page per job with a tab for each stage of the work. This file owns what every tab shares: the
// job itself, the editing lock, the cached lists (sheets, items, building types), error handling that says what happened,
// and the time-on-job heartbeat. A tab is a module with `render(container, ctx)`; it never talks to the lock directly.

import { createApi, ApiError } from "./api.js";
import { LockManager } from "./lock.js";
import { ProgressClient } from "./progress.js";
import { h, clear, toast, badge, titleCase, confirmDialog } from "./util.js";

const TABS = [
  { id: "overview", title: "Overview", step: "Setup", load: () => import("./tab-overview.js") },
  { id: "drawings", title: "Drawings", step: "Setup", load: () => import("./tab-drawings.js") },
  { id: "setup", title: "Setup", step: "Setup", load: () => import("./tab-setup.js") },
  { id: "tasks", title: "Detection", step: "Process", load: () => import("./tab-tasks.js") },
  { id: "review", title: "Review", step: "Review", load: () => import("./tab-review.js") },
  { id: "findings", title: "Questions & rules", step: "Review", load: () => import("./tab-findings.js") },
  { id: "count", title: "Count sheet", step: "Report", load: () => import("./tab-countsheet.js") },
  { id: "vision", title: "Hosted vision", step: "Process", load: () => import("./tab-vision.js") },
  { id: "audit", title: "Audit", step: "Review", load: () => import("./tab-audit.js") }
];

import { readBoot, antiforgeryFrom } from "./boot.js";

export { readBoot, antiforgeryFrom };

/** Everything a tab needs. Built once per page. */
export class JobContext {
  constructor(boot, api) {
    this.boot = boot;
    this.api = api;
    this.jobId = boot.jobId;
    this.job = null;
    this.listeners = new Map();
    this.cache = {};
    this.lock = new LockManager(api, boot.jobId, { onChange: (s) => this.emit("lock", s) });
    this.progress = new ProgressClient(boot);
  }

  // ---- events

  on(name, fn) {
    if (!this.listeners.has(name)) this.listeners.set(name, new Set());
    this.listeners.get(name).add(fn);
    return () => this.listeners.get(name)?.delete(fn);
  }

  emit(name, payload) {
    for (const fn of this.listeners.get(name) || []) {
      try { fn(payload); } catch (e) { console.error(e); }
    }
  }

  // ---- the job

  get me() { return this.boot.email; }
  get role() { return this.job?.myRole || "Viewer"; }
  get isOwner() { return this.role === "Owner"; }
  /** Edit permission on the job (Owner or Editor), whatever its state. */
  get mayEdit() { return !!this.job?.canEdit; }
  /** The job's state allows edits (not Complete, Closed or archived). */
  get editable() { return !!this.job?.lifecycleEditable; }
  get editing() { return this.lock.holding; }

  async reload() {
    this.job = await this.api.get(`/jobs/${this.jobId}`);
    this.emit("job", this.job);
    return this.job;
  }

  // ---- cached lists

  async cached(name, loader, force = false) {
    if (force || !this.cache[name]) this.cache[name] = loader();
    try { return await this.cache[name]; } catch (e) { delete this.cache[name]; throw e; }
  }

  sheets(force) { return this.cached("sheets", () => this.api.get(`/jobs/${this.jobId}/sheets`), force); }
  entities(force) { return this.cached("entities", () => this.api.get(`/jobs/${this.jobId}/entities?limit=5000`), force); }
  buildingTypes(force) { return this.cached("types", () => this.api.get(`/jobs/${this.jobId}/building-types`), force); }
  files(force) { return this.cached("files", () => this.api.get(`/jobs/${this.jobId}/files`), force); }

  invalidate(...names) {
    for (const n of names) delete this.cache[n];
    this.emit("invalidated", names);
  }

  // ---- doing things

  /** Runs a read or a change that needs no lock, showing a failure in plain words. Returns undefined if it failed. */
  async run(fn, { ok } = {}) {
    try {
      const result = await fn();
      if (ok) toast(ok, "success", 3000);
      return result;
    } catch (e) {
      this.report(e);
      return undefined;
    }
  }

  report(e) {
    if (e instanceof ApiError) {
      if (e.isLockLost) {
        this.lock.markLost(e.message);
        toast("You no longer hold the editing lock. Take it again to carry on; nothing you did before is lost.", "error");
      } else if (e.isVersionConflict) {
        this.invalidate("entities", "sheets", "types");
        toast(`${e.message} The screen has been refreshed.`, "error");
      } else {
        toast(e.message, "error");
      }
    } else if (e?.name !== "AbortError") {
      console.error(e);
      toast("Something went wrong in the page. Reload it and try again.", "error");
    }
  }

  /** Takes the editing lock if this tab does not hold it. Resolves true if it does now. */
  async ensureLock() {
    if (!this.mayEdit) { toast("You have view-only access to this job.", "error"); return false; }
    if (!this.editable) { toast(`This job is ${titleCase(this.job.job.state)}. Reopen it to make changes.`, "error"); return false; }
    if (this.lock.holding) return true;
    try {
      const got = await this.lock.acquire();
      if (!got) toast(this.lock.state.message || "Someone else is editing this job.", "error");
      return got;
    } catch (e) {
      this.report(e);
      return false;
    }
  }

  /** A change to the job's content: needs the lock (taken if free), then runs and reports. */
  async edit(fn, { ok } = {}) {
    if (!(await this.ensureLock())) return undefined;
    return this.run(fn, { ok });
  }

  /** The job's own editable state: Draft or InReview with edit permission. */
  get canChange() { return this.mayEdit && this.editable; }
}

function lockBar(ctx) {
  const bar = h("div", { class: "dw-lockbar" });
  const paint = () => {
    clear(bar);
    const s = ctx.lock.state;
    const job = ctx.job;
    if (!job) return;
    if (!ctx.mayEdit) {
      bar.append(h("span", { class: "dw-lock-state dw-lock-view" }, "View only"), h("span", { class: "text-muted" }, "You can look at this job but not change it."));
    } else if (!ctx.editable) {
      bar.append(h("span", { class: "dw-lock-state dw-lock-view" }, `Job is ${titleCase(job.job.state)}`), h("span", { class: "text-muted" }, "Reopen it from the Overview tab to make changes."));
    } else if (s.youHoldIt) {
      bar.append(h("span", { class: "dw-lock-state dw-lock-mine" }, "You are editing"),
        h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: () => ctx.run(() => ctx.lock.release()) }, "Stop editing"));
      if (s.pendingRequest) {
        bar.append(h("span", { class: "dw-lock-ask" }, `${s.pendingRequest.requesterName || s.pendingRequest.requesterEmail} wants to edit.`),
          h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: () => ctx.run(() => ctx.lock.respond(s.pendingRequest.id, true), { ok: "You handed over editing." }) }, "Hand over"),
          h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: () => ctx.run(() => ctx.lock.respond(s.pendingRequest.id, false)) }, "Keep editing"));
      }
    } else if (s.holder && !s.holder.isExpired) {
      bar.append(h("span", { class: "dw-lock-state dw-lock-other" }, `${s.holder.name || s.holder.email} is editing`),
        h("button", { class: "btn btn-outline-primary btn-sm", type: "button", onclick: () => ctx.run(() => ctx.lock.requestTakeover()) }, s.pendingRequest ? "Asked, waiting…" : "Ask to edit"));
      if (s.pendingRequest?.requesterEmail === ctx.me) {
        bar.append(h("button", { class: "btn btn-link btn-sm", type: "button", onclick: () => ctx.run(() => ctx.lock.cancelTakeover(s.pendingRequest.id)) }, "Cancel request"));
      }
      if (ctx.isOwner || ctx.boot.isAdmin) {
        bar.append(h("button", {
          class: "btn btn-outline-danger btn-sm", type: "button",
          onclick: async () => { if (await confirmDialog(`Take over editing from ${s.holder.name || s.holder.email} now? Anything they have not saved is lost to them.`, { ok: "Take over", kind: "danger" })) ctx.run(() => ctx.lock.force()); }
        }, "Take over now"));
      }
    } else {
      bar.append(h("span", { class: "dw-lock-state dw-lock-free" }, s.lost ? "Lock lost" : "Not editing"),
        h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: () => ctx.ensureLock() }, "Start editing"),
        s.lost ? h("span", { class: "text-danger" }, s.message) : h("span", { class: "text-muted" }, "Editing is also started automatically by your first change."));
    }

    if (s.message && s.youHoldIt === false && !s.lost && s.holder) bar.append(h("span", { class: "text-muted small" }, s.message));
  };
  ctx.on("lock", paint);
  ctx.on("job", paint);
  paint();
  return bar;
}

function header(ctx) {
  const wrap = h("div", { class: "dw-jobhead" });
  const paint = () => {
    clear(wrap);
    const j = ctx.job?.job;
    if (!j) return;
    wrap.append(
      h("div", { class: "dw-jobtitle" },
        h("a", { href: ctx.boot.base, class: "dw-back", title: "All jobs" }, "← Jobs"),
        h("h1", {}, j.name),
        badge(titleCase(j.state), j.state === "Complete" ? "success" : j.state === "Closed" ? "secondary" : "primary"),
        badge(`${j.reviewLevel} review`, "light"),
        ctx.job.tags.map((t) => h("span", { class: "dw-tag" }, t))),
      h("div", { class: "text-muted small" }, [j.customerName, j.projectName, j.siteAddress].filter(Boolean).join(" · ") || "No customer or project recorded"));
  };
  ctx.on("job", paint);
  paint();
  return wrap;
}

export async function startWorkbench(root, boot = readBoot(), api = createApi({ base: boot.api, antiforgery: antiforgeryFrom() })) {
  const ctx = new JobContext(boot, api);
  clear(root);
  root.append(h("div", { class: "dw-loading" }, "Loading job…"));

  try {
    await ctx.reload();
    await ctx.lock.refresh().catch(() => {});
  } catch (e) {
    clear(root);
    root.append(h("div", { class: "alert alert-danger" }, e instanceof ApiError && e.isNotFound ? "That job does not exist, or you are not on it." : e.message || "The job could not be loaded."));
    return ctx;
  }

  clear(root);
  const nav = h("ul", { class: "nav nav-tabs dw-tabs", role: "tablist" });
  const body = h("div", { class: "dw-tabbody" });
  root.append(header(ctx), lockBar(ctx), nav, body);

  let current = null;
  const show = async (id) => {
    const tab = TABS.find((t) => t.id === id) || TABS[0];
    if (current?.handle?.dispose) { try { current.handle.dispose(); } catch (e) { console.error(e); } }
    for (const a of nav.querySelectorAll("a")) a.classList.toggle("active", a.dataset.tab === tab.id);
    clear(body);
    body.append(h("div", { class: "dw-loading" }, "Loading…"));
    current = { tab, handle: null };
    ctx.currentTab = tab;
    try {
      const mod = await tab.load();
      const container = h("div", { class: "dw-tabpane", dataset: { tab: tab.id } });
      const handle = await mod.render(container, ctx);
      if (current.tab !== tab) return;                  // the person moved on while it loaded
      clear(body);
      body.append(container);
      current.handle = handle || null;
    } catch (e) {
      clear(body);
      body.append(h("div", { class: "alert alert-danger" }, "This tab could not be shown. " + (e?.message || "")));
      console.error(e);
    }
  };

  for (const tab of TABS) {
    nav.append(h("li", { class: "nav-item", role: "presentation" },
      h("a", { class: "nav-link", href: `#/${tab.id}`, dataset: { tab: tab.id }, role: "tab", onclick: (ev) => { ev.preventDefault(); location.hash = `#/${tab.id}`; } }, tab.title)));
  }

  const route = () => show((location.hash.match(/^#\/([a-z]+)/) || [])[1] || "overview");
  window.addEventListener("hashchange", route);
  ctx.go = (id, extra = "") => { location.hash = `#/${id}${extra}`; };
  await route();

  startActivity(ctx);
  window.addEventListener("pagehide", () => { ctx.lock.releaseOnUnload(); });
  return ctx;
}

/** Tells the server every few seconds that this person is here, and whether they are working. The server measures the time. */
function startActivity(ctx) {
  let lastInput = Date.now();
  for (const ev of ["mousemove", "keydown", "pointerdown", "wheel", "touchstart"]) document.addEventListener(ev, () => { lastInput = Date.now(); }, { passive: true });
  const beat = async () => {
    if (!ctx.job) return;
    const active = !document.hidden && Date.now() - lastInput < 60000;
    try {
      await ctx.api.post(`/jobs/${ctx.jobId}/activity/heartbeat`, { state: active ? "Active" : "Idle", step: ctx.currentTab?.step, surface: ctx.currentTab?.id || "workbench" });
    } catch { /* time on job is best effort and must never interrupt work */ }
  };
  setInterval(beat, 10000);
  window.addEventListener("pagehide", () => { try { ctx.api.request("POST", `/jobs/${ctx.jobId}/activity/end`, { keepalive: true }); } catch { /* best effort */ } });
  beat();
}

// The module runs itself on the job page. Tests import the pieces above and never reach this.
if (typeof document !== "undefined" && document.getElementById("dw-app")?.dataset.page === "job") {
  startWorkbench(document.getElementById("dw-app"));
}

