// Hosted vision. Local methods come first; only when they cannot read a sheet may a person approve sending specific pages to a
// hosted model. The approval names the sheets, the model and where it runs, expires, and is used by one run. Nothing is
// ever sent without it, and everything that was sent is listed here as counts, never the images themselves.

import { h, clear, field, select, badge, titleCase, fmtDate, promptDialog, toast } from "./util.js";

export async function render(container, ctx) {
  const paint = async () => {
    const [policy, approvals, log, sheets] = await Promise.all([
      ctx.run(() => ctx.api.get("/vision/policy")), ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/vision-approvals`)),
      ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/vision-transmissions`)), ctx.sheets(true)
    ]);
    clear(container);
    if (!policy) { container.append(h("div", { class: "alert alert-danger" }, "Hosted vision settings could not be loaded.")); return; }
    container.append(
      intro(policy),
      policy.enabled ? requestCard(ctx, policy, sheets, paint) : null,
      approvalsCard(ctx, approvals || [], paint),
      logCard(log || []));
  };

  await paint();
  return { refresh: paint };
}

function intro(policy) {
  return h("div", { class: `alert ${policy.enabled ? "alert-info" : "alert-secondary"}` },
    policy.enabled
      ? h("div", {}, h("strong", {}, "Hosted vision is available. "), "It is a fallback for sheets the local methods cannot read. Each use needs your approval, covers only the pages you choose, and expires.")
      : h("div", {}, h("strong", {}, "Hosted vision is switched off. "), "An administrator controls whether drawing content may be sent to a hosted model. Until it is switched on, sheets that cannot be read locally have to be handled by hand."));
}

function requestCard(ctx, policy, sheets, repaint) {
  const can = ctx.canChange;
  const candidates = sheets.filter((s) => s.inScope && !s.duplicateOf);
  const chosen = new Set(candidates.filter((s) => !s.hasTextLayer).map((s) => s.sheetId));
  const provider = select(policy.providers.map((p) => [p.providerId, `${p.name} (${p.model}${p.governanceRoute ? ", " + p.governanceRoute : ""})`]), policy.providers[0]?.providerId, { "aria-label": "Model" });
  const out = h("div", {});

  const list = h("div", { class: "dw-checklist" }, candidates.map((s) => h("label", { class: "form-check" },
    h("input", { class: "form-check-input", type: "checkbox", checked: chosen.has(s.sheetId), onchange: (e) => { e.target.checked ? chosen.add(s.sheetId) : chosen.delete(s.sheetId); } }),
    ` ${s.sheetNumber || `Page ${s.pageNo}`} ${s.title || ""} `, s.hasTextLayer ? null : badge("no live text", "warning"))));

  const review = async () => {
    if (!chosen.size) { toast("Choose at least one sheet.", "error"); return; }
    const d = await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/vision/disclosure`, { sheetIds: [...chosen], providerId: Number(provider.value) }));
    if (!d) return;
    clear(out);
    out.append(disclosure(ctx, d, [...chosen], Number(provider.value), repaint));
  };

  return h("div", { class: "dw-card" },
    h("h2", {}, "Approve sending pages"),
    policy.providers.length ? h("div", {},
      h("p", { class: "text-muted small" }, "Sheets with no live text are chosen for you, because they cannot be read any other way."),
      list, field("Model", provider),
      h("button", { class: "btn btn-primary btn-sm", type: "button", disabled: !can, onclick: review }, "Review what would be sent…"), out)
      : h("p", { class: "text-muted" }, "No model has been allowed for drawings. Ask an administrator."));
}

function disclosure(ctx, d, sheetIds, providerId, repaint) {
  const reason = h("input", { class: "form-control", maxlength: 400, placeholder: "Why local methods are not enough" });
  const agree = h("input", { class: "form-check-input", type: "checkbox", id: "dw-vision-agree" });
  const submit = h("button", { class: "btn btn-danger btn-sm", type: "button", disabled: d.problems.length > 0 }, "Approve sending these pages");
  submit.addEventListener("click", async () => {
    if (!agree.checked) { toast("Tick the box to confirm you have read the notice.", "error"); return; }
    if (d.requireReason && !reason.value.trim()) { toast("Give a reason.", "error"); return; }
    const a = await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/vision-approvals`, { sheetIds, providerId, reason: reason.value.trim() || null, acknowledgedNoticeVersion: d.noticeVersion }), { ok: "Approved. It expires soon, so use it from the Detection tab." });
    if (a) repaint();
  });

  return h("div", { class: "dw-disclosure" },
    h("h3", {}, "You are about to approve:"),
    h("ul", {},
      h("li", {}, `Sending ${d.pageCount} page(s), as up to ${d.imageBudget} images (pages and crops):`, h("div", { class: "small text-muted" }, d.sheets.map((s) => `${s.sheetNumber || `page ${s.pageNo}`} (${s.fileName})`).join("; "))),
      h("li", {}, `To the model ${d.provider.model}, run by ${d.provider.name} at ${d.provider.host}${d.provider.governanceRoute ? `, via ${d.provider.governanceRoute}` : ""}.`),
      h("li", {}, `For one run only. The approval expires in ${d.lifetimeMinutes} minutes and covers only these pages.`)),
    h("div", { class: "dw-notice" }, d.noticeText),
    d.problems.length ? h("div", { class: "alert alert-danger" }, h("ul", { class: "mb-0" }, d.problems.map((p) => h("li", {}, p)))) : null,
    d.requireReason ? field("Reason", reason) : null,
    h("label", { class: "form-check" }, agree, " I have read the notice above and approve sending these pages."),
    h("div", { class: "mt-2" }, submit));
}

function approvalsCard(ctx, approvals, repaint) {
  return h("div", { class: "dw-card" },
    h("h2", {}, "Approvals on this job"),
    approvals.length ? h("table", { class: "table table-sm align-middle" },
      h("thead", {}, h("tr", {}, ["Status", "Pages", "Images used", "Approved by", "Expires", ""].map((t) => h("th", {}, t)))),
      h("tbody", {}, approvals.map((a) => h("tr", {},
        h("td", {}, badge(a.isExpired && a.status === "Active" ? "Expired" : a.status, { Active: "primary", Used: "success", Revoked: "secondary", Expired: "secondary" }[a.status] || "light"), a.kind === "Standing" ? h("span", { class: "ms-1" }, badge("standing", "light")) : null,
          a.reason ? h("div", { class: "small text-muted" }, a.reason) : null),
        h("td", {}, a.pageCount), h("td", {}, `${a.imagesSent} of ${a.imageBudget}`), h("td", {}, a.approvedBy), h("td", {}, fmtDate(a.expiresUtc)),
        h("td", { class: "text-end" }, a.status === "Active" || a.status === "Used" ? h("button", {
          class: "btn btn-outline-danger btn-sm", type: "button", disabled: !ctx.canChange,
          onclick: async () => { const reason = await promptDialog("Revoke this approval? Nothing further will be sent under it.", { title: "Revoke approval", placeholder: "Why?", ok: "Revoke" }); if (reason && await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/vision-approvals/${a.approvalId}/revoke`, { reason }), { ok: "Revoked." })) repaint(); }
        }, "Revoke") : null)))))
      : h("p", { class: "text-muted" }, "No approvals have been given on this job."));
}

function logCard(log) {
  return h("div", { class: "dw-card" },
    h("h2", {}, "What has been sent"),
    h("p", { class: "text-muted small" }, "Counts only. The images and the instructions sent with them are not kept."),
    log.length ? h("table", { class: "table table-sm" }, h("thead", {}, h("tr", {}, ["When", "Images", "Size", "Outcome"].map((t) => h("th", {}, t)))),
      h("tbody", {}, log.map((t) => h("tr", {}, h("td", {}, fmtDate(t.requestedUtc)), h("td", {}, t.images), h("td", {}, `${(t.bytes / 1024).toFixed(0)} KB`), h("td", {}, titleCase(t.outcome), t.error ? h("div", { class: "small text-muted" }, t.error) : null)))))
      : h("p", { class: "text-muted" }, "Nothing has been sent to a hosted model on this job."));
}
