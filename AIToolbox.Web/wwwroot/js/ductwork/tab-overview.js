// Overview: who and what the job is, how far along it is, who is on it, and its lifecycle (complete, close, reopen).

import { h, clear, field, select, badge, titleCase, fmtDate, toast, promptDialog, confirmDialog } from "./util.js";
import { settle } from "./progress.js";
import { archiveBadge, archiveFacts, isActive, operationLine, progressBar, requestArchive, requestRestore } from "./archive-ui.js";

const REVIEW_LEVELS = [
  ["Sensitive", "Sensitive: ask me about every proposal"],
  ["Normal", "Normal: only ask when the detector is not confident"],
  ["Insensitive", "Insensitive: only ask about what the detector flags"]
];
const STEPS = [["Setup", "Drawings, sheets, building types and multipliers are set up"], ["Process", "Detection has run"], ["Review", "Every sheet has been reviewed"], ["Report", "The count sheet has been produced"]];
const STEP_STATES = [["NotStarted", "Not started"], ["Active", "In progress"], ["Done", "Done"]];

export async function render(container, ctx) {
  let timer = null;
  const paint = async () => {
    clear(container);
    await ctx.reload();
    const view = ctx.job;
    const j = view.job;
    const canChange = ctx.canChange;
    const archive = await ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/archive`));
    clearTimeout(timer);
    if (archive?.operations?.[0] && isActive(archive.operations[0])) timer = setTimeout(() => { if (container.isConnected) paint(); }, ctx.progress?.connected ? 30000 : 2500);

    container.append(
      h("div", { class: "dw-grid2" },
        detailsCard(ctx, j, canChange, paint),
        h("div", {}, workflowCard(ctx, view, canChange, paint), lifecycleCard(ctx, j, paint), archiveCard(ctx, j, archive, paint))),
      h("div", { class: "dw-grid2" }, membersCard(ctx, view, paint), h("div", {}, tagsCard(ctx, view, paint), activityCard(ctx))));
  };

  // Archive and restore report as they go; the overview repaints when its job's operation changes, and falls back to a timer without the connection.
  const repaint = settle(() => { if (container.isConnected) paint(); }, 400);
  const stopWatching = ctx.progress?.watch(ctx.jobId, (events, { resync }) => { if (resync || events.some((e) => e.kind === "archive")) repaint(); });
  const stopState = ctx.progress?.onState(() => repaint());

  await paint();
  return { dispose() { clearTimeout(timer); repaint.cancel(); stopWatching?.(); stopState?.(); }, refresh: paint };
}

/** Archive and restore. A job being archived or restored is held still, so this card says what is happening and when it will be done. */
function archiveCard(ctx, j, archive, repaint) {
  if (!archive) return null;
  const op = archive.operations?.[0];
  const archives = archive.archives || [];
  const job = { jobId: ctx.jobId, name: j.name, stateBeforeArchive: j.stateBeforeArchive };
  const state = j.state;
  const parts = [];

  if (isActive(op)) {
    parts.push(h("p", { class: "mb-1" }, operationLine(op)), progressBar(op), h("p", { class: "small text-muted mt-2 mb-0" }, "The job is held still while this runs. It carries on if the server restarts."));
  } else if (state === "Archived") {
    parts.push(h("p", {}, "This job is archived and its drawings, items and history are not in the database. The archive is the only full copy."));
    if (op?.status === "Failed") parts.push(h("p", { class: "text-danger small" }, operationLine(op)));
    if (archive.canRestore) parts.push(h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: async () => { if (await requestRestore(ctx.api, ctx.run, job)) repaint(); } }, "Restore job"));
    else parts.push(h("p", { class: "text-muted small mb-0" }, "Only the job’s owner or a Job Manager can restore it."));
  } else if (state === "Complete" || state === "Closed") {
    if (op?.status === "Failed") parts.push(h("p", { class: "text-danger small" }, operationLine(op)));
    parts.push(archive.canArchive
      ? h("div", {}, h("p", { class: "small text-muted" }, "Make a checked copy of this job, and optionally remove its detail from the database to free space. Restore brings it back."),
        h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: async () => { if (await requestArchive(ctx.api, ctx.run, job)) repaint(); } }, "Archive…"))
      : h("p", { class: "text-muted small mb-0" }, "Only the job’s owner or a Job Manager can archive it."));
  } else if (!archives.length) {
    return null;
  }

  if (!isActive(op) && op?.status === "Failed" && state === "Archiving") parts.push(h("p", { class: "text-danger small" }, operationLine(op)));

  if (archives.length) {
    parts.push(h("h3", { class: "dw-h3" }, "Archives"), h("ul", { class: "list-unstyled mb-0" }, archives.map((a) => h("li", { class: "mb-2" },
      archiveBadge(a), " ", h("span", { class: "small" }, archiveFacts(a)), a.lastProblem ? h("div", { class: "small text-danger" }, a.lastProblem) : null))));
  }

  return h("div", { class: "dw-card" }, h("h2", {}, "Archive"), parts);
}

function detailsCard(ctx, j, canChange, repaint) {
  const f = {
    name: h("input", { class: "form-control form-control-sm", value: j.name, disabled: !canChange, maxlength: 200 }),
    customerName: h("input", { class: "form-control form-control-sm", value: j.customerName || "", disabled: !canChange, maxlength: 200 }),
    projectName: h("input", { class: "form-control form-control-sm", value: j.projectName || "", disabled: !canChange, maxlength: 200 }),
    siteAddress: h("input", { class: "form-control form-control-sm", value: j.siteAddress || "", disabled: !canChange, maxlength: 400 })
  };
  const level = select(REVIEW_LEVELS, j.reviewLevel, { disabled: !canChange });
  const baseline = h("input", { type: "number", min: "0", step: "0.25", class: "form-control form-control-sm", value: j.baselineManualHours ?? "", disabled: !ctx.mayEdit, placeholder: "hours" });

  const save = async () => {
    const clearFields = ["customerName", "projectName", "siteAddress"].filter((k) => !f[k].value.trim() && j[k]);
    const body = {
      name: f.name.value.trim() || null,
      customerName: f.customerName.value.trim() || null, projectName: f.projectName.value.trim() || null, siteAddress: f.siteAddress.value.trim() || null,
      clear: clearFields, expectedStateVersion: ctx.job.job.stateVersion
    };
    if (await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/details`, body), { ok: "Saved." })) repaint();
  };

  return h("div", { class: "dw-card" },
    h("h2", {}, "Job details"),
    field("Name", f.name), field("Customer", f.customerName), field("Project", f.projectName), field("Site address", f.siteAddress),
    h("button", { class: "btn btn-primary btn-sm", type: "button", disabled: !canChange, onclick: save }, "Save details"),
    h("hr"),
    field("Review level", level, "How much you want to be asked about. This is how much you trust the detector, not how good it is."),
    h("button", {
      class: "btn btn-outline-primary btn-sm", type: "button", disabled: !canChange,
      onclick: async () => { if (await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/review-level`, { reviewLevel: level.value }), { ok: "Review level changed." })) repaint(); }
    }, "Change review level"),
    h("hr"),
    field("Hours to do this by hand", baseline, "Your own estimate, used only to show the time this tool saves."),
    h("button", {
      class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !ctx.mayEdit,
      onclick: async () => { if (await ctx.run(() => ctx.api.put(`/jobs/${ctx.jobId}/baseline`, { hours: baseline.value === "" ? null : Number(baseline.value) }), { ok: "Saved." })) repaint(); }
    }, "Save estimate"),
    h("div", { class: "text-muted small mt-3" }, `Created ${fmtDate(j.createdUtc)} by ${j.createdBy}. Owner: ${j.ownerEmail}.`));
}

function workflowCard(ctx, view, canChange, repaint) {
  return h("div", { class: "dw-card" },
    h("h2", {}, "Progress"),
    h("p", { class: "text-muted small" }, "A job can be completed once all four steps are done and every building type has a confirmed multiplier."),
    STEPS.map(([step, text]) => {
      const sel = select(STEP_STATES, view.steps[step] || "NotStarted", { disabled: !canChange, "aria-label": step });
      sel.addEventListener("change", async () => { if (!(await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/workflow/${step}`, { status: sel.value })))) repaint(); });
      return h("div", { class: "dw-steprow" }, h("div", {}, h("strong", {}, step), h("div", { class: "text-muted small" }, text)), sel);
    }));
}

function lifecycleCard(ctx, j, repaint) {
  const buttons = [];
  const state = j.state;
  if (ctx.mayEdit && (state === "Draft" || state === "InReview")) {
    buttons.push(h("button", {
      class: "btn btn-success btn-sm", type: "button",
      onclick: async () => {
        if (!(await confirmDialog("Mark this job complete? Editing stops and the issued reports are produced. You can reopen it later.", { ok: "Complete the job", kind: "success" }))) return;
        const begun = await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/complete`));
        if (begun && await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/complete/finish`), { ok: "The job is complete." })) repaint();
        else repaint();
      }
    }, "Complete job"));
    buttons.push(h("button", {
      class: "btn btn-outline-secondary btn-sm", type: "button",
      onclick: async () => {
        const reason = await promptDialog("Close a job you are not going to finish (for example a lost bid). It cannot be completed afterwards, only reopened.", { title: "Close job", placeholder: "Why is this job being closed?", ok: "Close job" });
        if (reason && await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/close`, { reason }), { ok: "The job is closed." })) repaint();
      }
    }, "Close job"));
  }

  // A report run that failed leaves the job Completing. It is not done until its reports exist, so say so and offer to try again.
  if (ctx.mayEdit && state === "Completing") {
    buttons.push(h("button", {
      class: "btn btn-success btn-sm", type: "button",
      onclick: async () => { if (await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/complete/finish`), { ok: "The job is complete." })) repaint(); else repaint(); }
    }, "Finish completing"));
  }

  if (ctx.mayEdit && (state === "Complete" || state === "Closed")) {
    buttons.push(h("button", {
      class: "btn btn-primary btn-sm", type: "button",
      onclick: async () => {
        const reason = await promptDialog("Reopening makes the job editable again and starts a new completion version. The reason is recorded.", { title: "Reopen job", placeholder: "Why is it being reopened?", ok: "Reopen" });
        if (reason && await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/reopen`, { reason }), { ok: "The job is open again." })) repaint();
      }
    }, "Reopen job"));
  }

  const facts = [];
  if (j.completedUtc) facts.push(`Completed ${fmtDate(j.completedUtc)} by ${j.completedBy} (version ${j.completionVersion}).`);
  if (j.closedUtc) facts.push(`Closed ${fmtDate(j.closedUtc)} by ${j.closedBy}: ${j.closeReason}`);
  if (j.reopenedUtc) facts.push(`Reopened ${fmtDate(j.reopenedUtc)} by ${j.reopenedBy}: ${j.reopenReason}`);

  return h("div", { class: "dw-card" },
    h("h2", {}, "Lifecycle"),
    h("p", {}, "State: ", badge(titleCase(state), state === "Complete" ? "success" : "primary")),
    facts.map((t) => h("p", { class: "small text-muted mb-1" }, t)),
    h("div", { class: "d-flex gap-2 flex-wrap mt-2" }, buttons.length ? buttons : h("span", { class: "text-muted small" }, ctx.mayEdit ? "Nothing to do here in this state." : "Only people with edit permission can change the lifecycle.")));
}

function membersCard(ctx, view, repaint) {
  const owner = ctx.isOwner;
  const email = h("input", { class: "form-control form-control-sm", type: "email", placeholder: "name@company.com", "aria-label": "Email" });
  const role = select(["Editor", "Viewer", "Owner"], "Editor", { "aria-label": "Role" });

  const rows = view.members.map((m) => {
    const roleSel = select(["Owner", "Editor", "Viewer"], m.role, { disabled: !owner, "aria-label": `Role of ${m.userEmail}` });
    roleSel.addEventListener("change", async () => { await ctx.run(() => ctx.api.put(`/jobs/${ctx.jobId}/members/${encodeURIComponent(m.userEmail)}`, { role: roleSel.value }), { ok: "Role changed." }); repaint(); });
    return h("tr", {}, h("td", {}, m.userEmail), h("td", {}, roleSel),
      h("td", { class: "text-end" }, h("button", {
        class: "btn btn-outline-danger btn-sm", type: "button", disabled: !owner,
        onclick: async () => { if (await confirmDialog(`Remove ${m.userEmail} from this job?`, { ok: "Remove", kind: "danger" }) && await ctx.run(() => ctx.api.del(`/jobs/${ctx.jobId}/members/${encodeURIComponent(m.userEmail)}`), { ok: "Removed." })) repaint(); }
      }, "Remove")));
  });

  return h("div", { class: "dw-card" },
    h("h2", {}, "People on this job"),
    h("p", { class: "text-muted small" }, "Owners and editors can change the job. Viewers can look. Only owners manage this list."),
    h("table", { class: "table table-sm align-middle" }, h("tbody", {}, rows)),
    owner ? h("div", { class: "d-flex gap-2" }, email, role,
      h("button", {
        class: "btn btn-primary btn-sm", type: "button",
        onclick: async () => { if (!email.value.trim()) { toast("Enter an email address.", "error"); return; } if (await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/members`, { email: email.value.trim(), role: role.value }), { ok: "Added." })) repaint(); }
      }, "Add")) : null);
}

function tagsCard(ctx, view, repaint) {
  const input = h("input", { class: "form-control form-control-sm", placeholder: "Add a tag", maxlength: 60, "aria-label": "New tag" });
  const add = async () => { const tag = input.value.trim(); if (tag && await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/tags`, { tag }))) repaint(); };
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") add(); });
  return h("div", { class: "dw-card" },
    h("h2", {}, "Tags"),
    h("div", { class: "dw-tags" }, view.tags.length ? view.tags.map((t) => h("span", { class: "dw-tag" }, t,
      ctx.mayEdit ? h("button", { class: "dw-tag-x", type: "button", "aria-label": `Remove tag ${t}`, onclick: async () => { if (await ctx.run(() => ctx.api.del(`/jobs/${ctx.jobId}/tags/${encodeURIComponent(t)}`))) repaint(); } }, "×") : null)) : h("span", { class: "text-muted small" }, "No tags yet.")),
    ctx.mayEdit ? h("div", { class: "d-flex gap-2 mt-2" }, input, h("button", { class: "btn btn-outline-primary btn-sm", type: "button", onclick: add }, "Add")) : null);
}

function activityCard(ctx) {
  const body = h("div", { class: "text-muted small" }, "Loading…");
  ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/activity`)).then((a) => {
    if (!a) { body.textContent = "Not available."; return; }
    clear(body);
    const hours = (s) => (s / 3600).toFixed(2);
    body.append(h("div", {}, `Active time: ${hours(a.activeSeconds)} h over ${a.sessions} session(s). Idle: ${hours(a.idleSeconds)} h.`));
    if (a.byUser?.length) body.append(h("table", { class: "table table-sm mt-2" }, h("tbody", {}, a.byUser.map((u) => h("tr", {}, h("td", {}, u.userEmail ?? u.email), h("td", {}, `${hours(u.activeSeconds)} h`))))));
  });
  return h("div", { class: "dw-card" }, h("h2", {}, "Time on this job"), body);
}
