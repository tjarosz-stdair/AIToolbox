// Settings for administrators: which kinds of work are switched on (and for which measured detector), and the controls on
// sending drawings to a hosted model. Both are off until an administrator turns them on, and every change is recorded.

import { createApi } from "./api.js";
import { antiforgeryFrom, readBoot } from "./boot.js";
import { h, clear, field, select, badge, titleCase, fmtDate, toast, confirmDialog } from "./util.js";

export async function startAdmin(root, boot = readBoot(), api = createApi({ base: boot.api, antiforgery: antiforgeryFrom() })) {
  const run = async (fn, ok) => {
    try { const r = await fn(); if (ok) toast(ok, "success", 3000); return r === undefined ? true : r; } catch (e) { toast(e.message || "That did not work.", "error"); return undefined; }
  };

  const paint = async () => {
    const [bindings, producers, vision] = await Promise.all([run(() => api.get("/admin/task-bindings")), run(() => api.get("/admin/task-bindings/producers")), run(() => api.get("/admin/vision-policy"))]);
    clear(root);
    root.append(
      h("div", { class: "page-heading" }, h("div", {}, h("h1", {}, "Ductwork settings"), h("p", { class: "text-muted mb-0" }, "Switch on the kinds of work that have been measured, and control hosted vision.")),
        h("a", { class: "btn btn-outline-secondary btn-sm", href: boot.base }, "← Jobs")),
      bindingsCard(api, run, bindings || [], producers || [], paint),
      visionCard(api, run, vision, paint));
  };

  await paint();
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
