// Settings for administrators: which kinds of work are switched on (and for which measured detector), and the controls on
// sending drawings to a hosted model. Both are off until an administrator turns them on, and every change is recorded.

import { createApi } from "./api.js";
import { antiforgeryFrom, readBoot } from "./boot.js";
import { h, clear, field, select, badge, titleCase, fmtDate, toast, confirmDialog, dialog } from "./util.js";

export async function startAdmin(root, boot = readBoot(), api = createApi({ base: boot.api, antiforgery: antiforgeryFrom() })) {
  const run = async (fn, ok) => {
    try { const r = await fn(); if (ok) toast(ok, "success", 3000); return r === undefined ? true : r; } catch (e) { toast(e.message || "That did not work.", "error"); return undefined; }
  };

  const paint = async () => {
    const [bindings, producers, vision, workers, managers] = await Promise.all([run(() => api.get("/admin/task-bindings")), run(() => api.get("/admin/task-bindings/producers")), run(() => api.get("/admin/vision-policy")), run(() => api.get("/admin/workers")), run(() => api.get("/admin/job-managers"))]);
    clear(root);
    root.append(
      h("div", { class: "page-heading" }, h("div", {}, h("h1", {}, "Ductwork settings"), h("p", { class: "text-muted mb-0" }, "Connect workers, switch on the kinds of work that have been measured, and control hosted vision.")),
        h("div", { class: "d-flex gap-2" }, h("a", { class: "btn btn-outline-secondary btn-sm", href: `${boot.base}/Archives` }, "Archives"), h("a", { class: "btn btn-outline-secondary btn-sm", href: boot.base }, "← Jobs"))),
      workersCard(api, run, workers, paint, boot),
      bindingsCard(api, run, bindings || [], producers || [], paint),
      visionCard(api, run, vision, paint),
      managersCard(api, run, managers, paint));
  };

  await paint();
}

const WORKER_KIND = { Online: "success", Draining: "warning", Drained: "secondary", Offline: "danger" };

function ago(seconds) {
  if (seconds < 90) return `${seconds}s ago`;
  if (seconds < 5400) return `${Math.round(seconds / 60)} min ago`;
  return seconds < 172800 ? `${Math.round(seconds / 3600)} h ago` : `${Math.round(seconds / 86400)} days ago`;
}

function workersCard(api, run, workers, repaint, boot) {
  if (!workers) return h("div", { class: "dw-card" }, h("h2", {}, "Workers"), h("p", { class: "text-danger" }, "The workers could not be loaded."));
  const consumersHref = boot.base.replace(/\/Ductwork\/?$/i, "") + "/Admin/ApiConsumers";

  const drain = async (w, on) => {
    if (on && !(await confirmDialog(`Ask ${w.workerName} to finish what it is doing and take no more work? Use this before upgrading it. You can lift the request at any time.`, { ok: "Drain", kind: "danger" }))) return;
    if (await run(() => api.post(`/admin/workers/${w.workerInstanceId}/drain`, { drain: on }), on ? "Asked to drain. It stops taking work now." : "Drain lifted.")) repaint();
  };
  const forget = async (w) => {
    if (await confirmDialog(`Forget ${w.workerName}? It is listed again if it reconnects. Its key keeps working; revoke that under API Consumers.`, { ok: "Forget", kind: "danger" })
      && await run(() => api.del(`/admin/workers/${w.workerInstanceId}`), "Forgotten.")) repaint();
  };

  const row = (w) => h("tr", {},
    h("td", {}, h("strong", {}, w.workerName), h("div", { class: "small text-muted" }, w.hostName || "unknown machine"), w.keyEnabled === false ? h("div", { class: "small text-danger" }, "its key is switched off") : null),
    h("td", {}, badge(w.status, WORKER_KIND[w.status] || "light"), h("div", { class: "small text-muted" }, `seen ${ago(w.secondsSinceSeen)}`)),
    h("td", { class: "small" }, `${w.runningTasks} running, up to ${w.maxConcurrency || "?"}`, w.lastClaimUtc ? h("div", { class: "text-muted" }, `last took work ${fmtDate(w.lastClaimUtc)}`) : null),
    h("td", { class: "small" }, w.workerVersion || "?", h("div", { class: "text-muted" }, (w.taskTypes || []).join(", ") || "no task types")),
    h("td", { class: "small" }, w.drainRequested ? `Drain asked by ${w.drainRequestedBy || "?"}, ${fmtDate(w.drainRequestedUtc)}` : ""),
    h("td", { class: "text-end text-nowrap" },
      w.drainRequested ? h("button", { class: "btn btn-outline-primary btn-sm me-1", type: "button", onclick: () => drain(w, false) }, "Resume")
        : h("button", { class: "btn btn-outline-secondary btn-sm me-1", type: "button", onclick: () => drain(w, true) }, "Drain"),
      h("button", { class: "btn btn-outline-danger btn-sm", type: "button", disabled: w.status === "Online" || w.status === "Draining", title: "Only a worker that has gone can be forgotten", onclick: () => forget(w) }, "Forget")));

  return h("div", { class: "dw-card" },
    h("div", { class: "d-flex justify-content-between align-items-center" }, h("h2", {}, "Workers"),
      h("div", {}, h("button", { class: "btn btn-outline-secondary btn-sm me-1", type: "button", onclick: repaint }, "Refresh"), h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: () => createKey(api, run, consumersHref, repaint) }, "Create worker key"))),
    h("p", { class: "text-muted small" }, "Workers do the reading: they connect to this site with their own key, take queued work and hand back proposals. They hold no database access or provider keys. A worker is online if it was heard from in the last minute."),
    workers.length ? h("table", { class: "table table-sm align-middle" },
      h("thead", {}, h("tr", {}, ["Worker", "Status", "Work", "Build and task types", "Drain", ""].map((t) => h("th", {}, t)))), h("tbody", {}, workers.map(row)))
      : h("p", { class: "text-muted" }, "No worker has connected yet. Create a key, put the settings it gives you on the worker machine, and start it."));
}

async function createKey(api, run, consumersHref, repaint) {
  const name = h("input", { class: "form-control form-control-sm", maxlength: 60, placeholder: "for example farm-1", "aria-label": "Worker name" });
  const made = await dialog({
    title: "Create a worker key", body: h("div", {}, h("p", { class: "small" }, "The key can do only what a worker needs: take Ductwork work and report on it. Name the worker after the machine it will run on."), field("Worker name", name, "Letters, digits, dashes, underscores and dots.")),
    actions: [{ label: "Cancel" }, { label: "Create", kind: "primary", validate: () => (/^[A-Za-z0-9._-]{1,60}$/.test(name.value.trim()) ? null : "A worker name is 1 to 60 letters, digits, dashes, underscores or dots.") }]
  });
  if (made !== "Create") return;
  const key = await run(() => api.post("/admin/workers/keys", { workerName: name.value.trim() }));
  if (!key) return;
  const box = h("textarea", { class: "form-control font-monospace", rows: 9, readonly: true, "aria-label": "Worker settings" }, key.settings);
  await dialog({
    title: "Worker key created", wide: true,
    body: h("div", {}, h("p", { class: "text-danger small mb-2" }, "Copy this now. The key is shown only once and cannot be read again."),
      box, h("button", { class: "btn btn-outline-secondary btn-sm mt-2 me-2", type: "button", onclick: async () => { try { await navigator.clipboard.writeText(key.settings); toast("Copied.", "success", 2000); } catch { box.select(); toast("Select the text and copy it.", "info"); } } }, "Copy"),
      h("a", { class: "small", href: consumersHref }, "Manage it under API Consumers")),
    actions: [{ label: "I have copied it", kind: "primary" }]
  });
  repaint();
}

function bindingsCard(api, run, bindings, producers, repaint) {
  const type = h("input", { class: "form-control form-control-sm", placeholder: "Task type, for example detect-residential-labels", maxlength: 60, "aria-label": "Task type" });
  const producer = select(producers.map((p) => [p.producerId, `${p.label} (${titleCase(p.kind)}${p.supportsVision ? ", vision" : ""})`]), producers[0]?.producerId, { "aria-label": "Producer" });
  const runs = select([], "", { "aria-label": "Evaluation run" });
  const fillRuns = () => {
    clear(runs);
    const p = producers.find((x) => String(x.producerId) === String(producer.value));
    for (const r of p?.evalRuns || []) runs.append(h("option", { value: r.evalRunId }, `Run ${r.evalRunId} on ${r.taskKey}, ${fmtDate(r.startedUtc)}`));
  };
  producer.addEventListener("change", fillRuns);
  fillRuns();
  const sends = h("input", { type: "checkbox", class: "form-check-input", id: "dw-sends" });
  const attempts = h("input", { type: "number", min: "1", max: "10", value: "3", class: "form-control form-control-sm", "aria-label": "Attempts" });

  return h("div", { class: "dw-card" },
    h("h2", {}, "Enabled kinds of work"),
    h("p", { class: "text-muted small" }, "A kind of work can only be queued once it is bound to a detector that has been measured against checked drawings. Binding a different detector replaces the old one."),
    bindings.length ? h("table", { class: "table table-sm align-middle" },
      h("thead", {}, h("tr", {}, ["Type", "Producer", "Sends drawings", "Attempts", "Enabled by", ""].map((t) => h("th", {}, t)))),
      h("tbody", {}, bindings.map((b) => h("tr", {}, h("td", {}, h("strong", {}, b.taskType)), h("td", {}, producers.find((p) => p.producerId === b.producerId)?.label || `#${b.producerId}`),
        h("td", {}, b.sendsDrawingContentToHostedModel ? badge("yes", "danger") : "no"), h("td", {}, b.maxAttempts), h("td", { class: "small" }, `${b.createdBy}, ${fmtDate(b.activeFromUtc)}`),
        h("td", { class: "text-end" }, h("button", {
          class: "btn btn-outline-danger btn-sm", type: "button",
          onclick: async () => { if (await confirmDialog(`Switch off “${b.taskType}”? Work already queued still runs; nothing new can be queued.`, { ok: "Switch off", kind: "danger" }) && await run(() => api.del(`/admin/task-bindings/${encodeURIComponent(b.taskType)}`), "Switched off.")) repaint(); }
        }, "Switch off")))))) : h("p", { class: "text-muted" }, "Nothing is switched on yet."),
    h("h3", { class: "dw-h3" }, "Switch on a kind of work"),
    producers.length ? h("div", { class: "dw-formgrid" }, field("Task type", type), field("Detector", producer), field("Measured by", runs), field("Attempts", attempts),
      h("label", { class: "form-check w-100" }, sends, " This work sends drawing content to a hosted model (needs a vision-capable model)"),
      h("div", { class: "w-100" }, h("button", {
        class: "btn btn-primary btn-sm", type: "button",
        onclick: async () => {
          if (!type.value.trim()) { toast("Enter a task type.", "error"); return; }
          if (await run(() => api.post("/admin/task-bindings", { taskType: type.value.trim(), producerId: Number(producer.value), evalRunId: Number(runs.value), sendsDrawingContent: sends.checked, maxAttempts: Number(attempts.value) }), "Switched on.")) repaint();
        }
      }, "Switch on")))
      : h("p", { class: "text-muted small" }, "No detector has been measured yet, so there is nothing to switch on. Measure one with the evaluation tool first."));
}

/** Who may archive and restore any job. The permission gives no view of a job's contents. */
function managersCard(api, run, managers, repaint) {
  if (!managers) return h("div", { class: "dw-card" }, h("h2", {}, "Job Managers"), h("p", { class: "text-danger" }, "The list could not be loaded."));
  const email = h("input", { class: "form-control form-control-sm", type: "email", placeholder: "name@company.com", "aria-label": "Email address" });
  const add = async () => {
    if (!email.value.trim()) { toast("Enter the person’s email address.", "error"); return; }
    if (await run(() => api.post("/admin/job-managers", { email: email.value.trim() }), "Added.")) repaint();
  };
  email.addEventListener("keydown", (e) => { if (e.key === "Enter") add(); });

  return h("div", { class: "dw-card" },
    h("h2", {}, "Job Managers"),
    h("p", { class: "text-muted small" }, "A Job Manager can archive and restore any job, whether or not they are on it. The permission does not let them see a job’s drawings or items. Each grant and removal is recorded."),
    managers.length ? h("table", { class: "table table-sm align-middle" },
      h("thead", {}, h("tr", {}, ["Person", "Granted by", ""].map((t) => h("th", {}, t)))),
      h("tbody", {}, managers.map((m) => h("tr", {}, h("td", {}, m.userEmail), h("td", { class: "small" }, `${m.grantedBy}, ${fmtDate(m.grantedUtc)}`),
        h("td", { class: "text-end" }, h("button", {
          class: "btn btn-outline-danger btn-sm", type: "button",
          onclick: async () => { if (await confirmDialog(`Remove ${m.userEmail} as a Job Manager?`, { ok: "Remove", kind: "danger" }) && await run(() => api.del(`/admin/job-managers/${encodeURIComponent(m.userEmail)}`), "Removed.")) repaint(); }
        }, "Remove")))))) : h("p", { class: "text-muted" }, "Nobody is a Job Manager yet. Owners can still archive their own jobs."),
    h("div", { class: "d-flex gap-2", style: { maxWidth: "460px" } }, email, h("button", { class: "btn btn-primary btn-sm text-nowrap", type: "button", onclick: add }, "Add Job Manager")));
}

function visionCard(api, run, vision, repaint) {
  if (!vision || vision === true) return h("div", { class: "dw-card" }, h("h2", {}, "Hosted vision"), h("p", { class: "text-danger" }, "The settings could not be loaded."));
  const p = vision.policy;
  const enabled = h("input", { type: "checkbox", class: "form-check-input", id: "dw-vision-on", checked: p.enabled });
  const approver = select([["EditPermission", "Anyone with edit permission on the job"], ["JobManager", "Job Managers and administrators"], ["Admin", "Administrators only"]], p.approverRule);
  const notice = h("textarea", { class: "form-control", rows: 4, maxlength: 4000 }, p.noticeText);
  const num = (v, min = 1) => h("input", { type: "number", min: String(min), value: String(v), class: "form-control form-control-sm" });
  const maxPages = num(p.maxPagesPerApproval), perPage = num(p.maxImagesPerPage), lifetime = num(p.approvalLifetimeMinutes), standingPages = num(p.standingMaxPagesPerJob, 0);
  const reason = h("input", { type: "checkbox", class: "form-check-input", id: "dw-vision-reason", checked: p.requireReason });
  const standing = h("input", { class: "form-control form-control-sm", value: (p.standingTaskTypes || []).join(", "), placeholder: "none" });
  const chosen = new Set(p.allowedProviderIds);

  const providers = vision.providers.length ? vision.providers.map((m) => h("label", { class: `form-check${m.eligible ? "" : " text-muted"}` },
    h("input", { class: "form-check-input", type: "checkbox", checked: chosen.has(m.providerId), disabled: !m.eligible, onchange: (e) => { e.target.checked ? chosen.add(m.providerId) : chosen.delete(m.providerId); } }),
    ` ${m.name} (${m.model}${m.governanceRoute ? ", " + m.governanceRoute : ""})`, m.eligible ? null : h("span", { class: "small ms-2" }, `not eligible: ${m.whyNot}`)))
    : h("p", { class: "text-muted small" }, "No AI providers are configured.");

  const save = async () => {
    const body = {
      enabled: enabled.checked, allowedProviderIds: [...chosen], approverRule: approver.value, noticeText: notice.value, maxPagesPerApproval: Number(maxPages.value), maxImagesPerPage: Number(perPage.value),
      requireReason: reason.checked, approvalLifetimeMinutes: Number(lifetime.value), standingTaskTypes: standing.value.split(",").map((t) => t.trim()).filter(Boolean), standingMaxPagesPerJob: Number(standingPages.value)
    };
    if (p.enabled && !body.enabled && !(await confirmDialog("Switching hosted vision off takes effect at once: unused approvals are revoked, queued vision work is cancelled, and running work is stopped.", { ok: "Switch off", kind: "danger" }))) return;
    if (await run(() => api.put("/admin/vision-policy", body), "Saved.")) repaint();
  };

  return h("div", { class: "dw-card" },
    h("h2", {}, "Hosted vision"),
    h("p", { class: "text-muted small" }, "Sending drawing content to a hosted model is a data-governance event. It is off by default. When on, each use needs a person’s approval for named sheets and one model, which expires. Changing the notice makes everyone read it again."),
    h("label", { class: "form-check mb-2" }, enabled, h("strong", {}, " Allow hosted vision as a fallback")),
    field("Models that may receive drawings", h("div", {}, providers), "Only models that are enabled, allowed for Ductwork, vision-capable and on a governance route can be chosen."),
    h("div", { class: "dw-formgrid" }, field("Who may approve", approver), field("Most pages per approval", maxPages), field("Most images per page (pages and crops)", perPage), field("An approval lasts (minutes)", lifetime)),
    h("label", { class: "form-check mb-2" }, reason, " A written reason is required for every approval"),
    field("Notice shown before approving", notice, `Version ${p.noticeVersion}. Last changed by ${p.changedBy || "no one yet"}${p.changedUtc ? ", " + fmtDate(p.changedUtc) : ""}.`),
    h("div", { class: "dw-formgrid" }, field("Pre-approved task types (comma separated)", standing, "Leave empty unless your organization has agreed to it."), field("Pages per job those may use", standingPages)),
    h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: save }, "Save"));
}

if (typeof document !== "undefined" && document.getElementById("dw-app")?.dataset.page === "admin") {
  startAdmin(document.getElementById("dw-app"));
}
