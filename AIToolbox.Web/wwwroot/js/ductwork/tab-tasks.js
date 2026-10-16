// Detection: run the kinds of work an administrator has enabled, and watch them. A task goes to a worker, which reads the
// drawings and hands back proposals; nothing it finds counts until a person has checked it on the Review tab.

import { h, clear, badge, fmtDate, confirmDialog, select, field, dialog } from "./util.js";

const ACTIVE = new Set(["Queued", "Running"]);

export async function render(container, ctx) {
  let timer = null;
  const paint = async () => {
    const [types, tasks, approvals, workers] = await Promise.all([
      ctx.run(() => ctx.api.get("/task-types")), ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/tasks`)), ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/vision-approvals`)),
      ctx.run(() => ctx.api.get("/workers/summary"))
    ]);
    clear(container);
    container.append(...[workerNotice(workers, tasks || []), runCard(ctx, types || [], approvals || [], paint), tasksCard(ctx, tasks || [], paint)].filter(Boolean));
    clearTimeout(timer);
    if ((tasks || []).some((t) => ACTIVE.has(t.status))) timer = setTimeout(() => { if (container.isConnected) paint(); }, 3000);
  };

  await paint();
  return { dispose() { clearTimeout(timer); }, refresh: paint };
}

/** Queued work with nobody to do it looks exactly like slow work, so say so. Counts only: who the workers are is for administrators. */
function workerNotice(workers, tasks) {
  if (!workers || workers.online > 0) return null;
  const waiting = tasks.some((t) => t.status === "Queued");
  const why = workers.total === 0 ? "No worker has ever connected to this site." : workers.draining > 0 ? "The workers are finishing up and taking no new work." : "No worker has been heard from in the last minute.";
  return h("div", { class: "alert alert-warning", role: "status" }, h("strong", {}, "No worker is connected. "), why, waiting ? " Work you have queued will wait until one is." : " Work you queue will wait until one is.", " Ask an administrator to check the workers under Ductwork settings.");
}

function runCard(ctx, types, approvals, repaint) {
  if (!types.length) {
    return h("div", { class: "dw-card" }, h("h2", {}, "Run detection"), h("p", { class: "text-muted" }, "No kind of work has been enabled yet. An administrator turns a detector on after it has been measured against checked drawings."));
  }

  return h("div", { class: "dw-card" },
    h("h2", {}, "Run detection"),
    h("p", { class: "text-muted small" }, "Each result is a proposal for you to check. Running again adds proposals; it never changes what you have already decided."),
    h("div", { class: "d-flex gap-2 flex-wrap" }, types.map((t) => h("button", {
      class: `btn btn-sm ${t.sendsDrawingContent ? "btn-outline-danger" : "btn-primary"}`, type: "button", disabled: !ctx.canChange,
      title: t.sendsDrawingContent ? "Sends drawing content to a hosted model, so it needs an approval" : "",
      onclick: () => start(ctx, t, approvals, repaint)
    }, t.sendsDrawingContent ? `${t.taskType} (hosted vision)` : t.taskType))));
}

async function start(ctx, type, approvals, repaint) {
  let approvalId = null;
  if (type.sendsDrawingContent) {
    const usable = approvals.filter((a) => a.status === "Active" && !a.isExpired);
    const sel = select(usable.map((a) => [a.approvalId, `${a.pageCount} page(s), expires ${fmtDate(a.expiresUtc)}`]), usable[0]?.approvalId);
    const body = usable.length
      ? h("div", {}, h("p", {}, "This kind of work sends drawing content to a hosted model, so it needs an approval you have given."), field("Approval to use", sel))
      : h("div", {}, h("p", {}, "This kind of work sends drawing content to a hosted model, so it needs an approval, and there is none you can use."), h("a", { href: "#/vision" }, "Go to Hosted vision"));
    const result = await dialog({ title: "Approval needed", body, actions: usable.length ? [{ label: "Cancel" }, { label: "Run", kind: "primary" }] : [{ label: "Close", kind: "primary" }] });
    if (result !== "Run") return;
    approvalId = sel.value;
  } else if (!(await confirmDialog(`Run ${type.taskType} on this job's in-scope sheets?`, { ok: "Run" }))) {
    return;
  }

  const task = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/tasks`, { taskType: type.taskType, inputManifestRef: null, visionApprovalId: approvalId }), { ok: "Queued. A worker will pick it up." });
  if (task) repaint();
}

function tasksCard(ctx, tasks, repaint) {
  const newestFirst = [...tasks].sort((a, b) => b.processingTaskId - a.processingTaskId);
  return h("div", { class: "dw-card" },
    h("div", { class: "d-flex justify-content-between align-items-center" }, h("h2", {}, "Work on this job"), h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: repaint }, "Refresh")),
    newestFirst.length ? h("table", { class: "table table-sm align-middle" },
      h("thead", {}, h("tr", {}, ["Task", "Status", "Progress", "Started", "Result", ""].map((t) => h("th", {}, t)))),
      h("tbody", {}, newestFirst.map((t) => row(ctx, t, repaint)))) : h("p", { class: "text-muted" }, "Nothing has been run yet."));
}

function row(ctx, t, repaint) {
  const kind = { Queued: "secondary", Running: "primary", Succeeded: "success", Failed: "danger", Cancelled: "secondary" }[t.status] || "light";
  const pct = t.progressTotal ? Math.round((t.progressDone / t.progressTotal) * 100) : null;
  return h("tr", {},
    h("td", {}, h("strong", {}, t.taskType), t.sendsDrawingContentToHostedModel ? h("span", { class: "ms-1" }, badge("hosted vision", "danger")) : null, h("div", { class: "small text-muted" }, `by ${t.createdBy}`)),
    h("td", {}, badge(t.cancelRequested && ACTIVE.has(t.status) ? "Cancelling" : t.status, kind), t.attempt > 1 ? h("span", { class: "small text-muted ms-1" }, `attempt ${t.attempt} of ${t.maxAttempts}`) : null),
    h("td", { style: { minWidth: "160px" } }, pct !== null ? h("div", { class: "progress" }, h("div", { class: "progress-bar", style: { width: `${pct}%` } }, `${pct}%`)) : "", t.progressMessage ? h("div", { class: "small text-muted" }, t.progressMessage) : null),
    h("td", { class: "small" }, fmtDate(t.startedUtc || t.createdUtc)),
    h("td", { class: "small" }, t.status === "Failed" ? h("span", { class: "text-danger" }, t.errorMessage || "Failed") : t.finishedUtc ? `Finished ${fmtDate(t.finishedUtc)}` : ""),
    h("td", { class: "text-end" }, ACTIVE.has(t.status) && !t.cancelRequested ? h("button", {
      class: "btn btn-outline-danger btn-sm", type: "button", disabled: !ctx.mayEdit,
      onclick: async () => { if (await confirmDialog("Cancel this task?", { ok: "Cancel task", kind: "danger" }) && await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/tasks/${t.processingTaskId}/cancel`), { ok: "Cancelling." })) repaint(); }
    }, "Cancel") : (t.status === "Succeeded" ? h("a", { class: "btn btn-outline-primary btn-sm", href: "#/review" }, "Review") : null)));
}

