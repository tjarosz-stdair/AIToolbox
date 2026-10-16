// Questions and rules. Where a drawing is ambiguous, or a detector disagrees with something, it asks rather than guesses:
// the question stays open and on the count sheet until a person answers it. You answer by applying a rule (after seeing
// exactly what it would change), by writing a new one, by correcting the item yourself, or by waiving the question with a
// reason. A rule changes a count only once a person has approved it, and every change can be taken back.

import { h, clear, field, select, badge, titleCase, toast, dialog, confirmDialog, promptDialog, fmtDate } from "./util.js";

const SCOPES = [["Job", "This job"], ["BuildingType", "One building type"], ["Sheet", "One sheet"], ["Instance", "Every job (needs a Job Manager)"]];

export async function render(container, ctx) {
  let showAll = false;
  const state = { findings: [], rules: [], sheets: [], types: [] };

  const load = async () => {
    const [findings, rules, sheets, types] = await Promise.all([
      ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/findings`)), ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/rules`)), ctx.sheets(true), ctx.buildingTypes(true)
    ]);
    Object.assign(state, { findings: findings || [], rules: rules || [], sheets, types });
  };

  const paint = async () => {
    await load();
    clear(container);
    container.append(h("div", { class: "dw-grid2" }, findingsCard(ctx, state, showAll, (v) => { showAll = v; paint(); }, paint), rulesCard(ctx, state, paint)));
  };

  await paint();
  return { refresh: paint };
}

// ------------------------------------------------------------------ findings

function findingsCard(ctx, state, showAll, setShowAll, repaint) {
  const list = state.findings.filter((f) => showAll || f.state === "Open");
  const open = state.findings.filter((f) => f.state === "Open").length;
  return h("div", { class: "dw-card" },
    h("div", { class: "d-flex justify-content-between align-items-center" }, h("h2", {}, `Questions (${open} open)`),
      h("label", { class: "form-check small" }, h("input", { class: "form-check-input", type: "checkbox", checked: showAll, onchange: (e) => setShowAll(e.target.checked) }), " Show answered")),
    list.length ? list.map((f) => finding(ctx, state, f, repaint)) : h("p", { class: "text-muted" }, open ? "" : "No open questions."));
}

function finding(ctx, state, f, repaint) {
  const open = f.state === "Open";
  const can = ctx.canChange;
  const rule = f.resolvedRuleId ? state.rules.find((r) => r.ruleId === f.resolvedRuleId) : null;
  return h("div", { class: `dw-finding${open ? "" : " dw-finding-done"}` },
    h("div", { class: "d-flex gap-2 align-items-center flex-wrap" }, badge(titleCase(f.kind), "light"), f.item ? h("strong", {}, f.item) : null, open ? null : badge(f.state === "Waived" ? "Waived" : "Answered", f.state === "Waived" ? "secondary" : "success")),
    h("p", { class: "mb-1 mt-1" }, f.question),
    f.candidates?.length ? h("div", { class: "small" }, "Could be: ", f.candidates.map((c) => h("span", { class: "dw-tag" }, c))) : null,
    f.evidence ? h("div", { class: "small text-muted" }, `Evidence: ${f.evidence}`) : null,
    open ? h("div", { class: "d-flex gap-2 flex-wrap mt-2" },
      f.entityId ? h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: () => { ctx.nav = { focusEntity: f.entityId }; ctx.go("review"); } }, "Show on the sheet") : null,
      h("button", { class: "btn btn-outline-primary btn-sm", type: "button", disabled: !can, onclick: () => applyRuleDialog(ctx, state, f, repaint) }, "Apply a rule…"),
      h("button", { class: "btn btn-outline-primary btn-sm", type: "button", disabled: !can, onclick: () => ruleDialog(ctx, state, { finding: f }, repaint) }, "Write a rule…"),
      f.entityId ? h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !can, onclick: () => resolveByOverride(ctx, f, repaint) }, "I corrected it myself") : null,
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !can, onclick: () => waive(ctx, f, repaint) }, "Waive…"))
      : h("div", { class: "small text-muted mt-1" }, `${titleCase(f.resolvedByKind)}${rule ? `: ${rule.name}` : ""} by ${f.resolvedBy}, ${fmtDate(f.resolvedUtc)}${f.resolutionNote ? `. ${f.resolutionNote}` : ""}`,
        can ? h("button", { class: "btn btn-link btn-sm py-0", type: "button", onclick: async () => { if (await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/findings/${f.findingId}/reopen`))) repaint(); } }, "Reopen") : null));
}

async function resolveByOverride(ctx, f, repaint) {
  const note = await promptDialog("Correct or reject the item first, then close the question. Add a note if you like.", { title: "Close the question", required: false, placeholder: "Optional note", ok: "Close question" });
  if (note === null) return;
  if (await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/findings/${f.findingId}/resolve`, { kind: "Override", ruleId: null, note: note || null }), { ok: "Question closed." })) repaint();
}

async function waive(ctx, f, repaint) {
  const note = await promptDialog("Leave this question unanswered on purpose? The reason is kept on record.", { title: "Waive the question", placeholder: "Why is it not needed?", ok: "Waive" });
  if (note && await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/findings/${f.findingId}/resolve`, { kind: "Waiver", ruleId: null, note }), { ok: "Waived." })) repaint();
}

async function applyRuleDialog(ctx, state, f, repaint) {
  const approved = state.rules.filter((r) => r.status === "Approved");
  if (!approved.length) { toast("There is no approved rule yet. Write one with “Write a rule”.", "info"); return; }
  const sel = select(approved.map((r) => [r.ruleId, `${r.name} (${titleCase(r.scope)})`]), approved[0].ruleId);
  const preview = h("div", { class: "dw-preview" });
  const show = async () => { await renderPreview(ctx, preview, () => ctx.api.get(`/jobs/${ctx.jobId}/rules/${sel.value}/preview`)); };
  sel.addEventListener("change", show);
  show();
  const result = await dialog({ title: "Apply a rule", wide: true, body: h("div", {}, field("Rule", sel), h("p", { class: "small text-muted" }, "This is exactly what it will change on this job:"), preview),
    actions: [{ label: "Cancel" }, { label: "Apply and answer", kind: "primary" }] });
  if (result !== "Apply and answer") return;
  const applied = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${sel.value}/apply`));
  if (!applied) return;
  toast(`Rule applied: ${applied.changed} changed, ${applied.skipped} left alone${applied.conflicts ? `, ${applied.conflicts} in conflict` : ""}.`, "success", 5000);
  ctx.invalidate("entities");
  const closed = f.entityId ? await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/findings/${f.findingId}/resolve`, { kind: "Rule", ruleId: sel.value, note: null })) : undefined;
  if (f.entityId && !closed) toast("The rule did not decide this particular item, so the question is still open.", "info", 7000);
  repaint();
}

// ------------------------------------------------------------------ rules

function rulesCard(ctx, state, repaint) {
  const can = ctx.canChange;
  const byKey = new Map();
  for (const r of state.rules) { if (!byKey.has(r.ruleKey)) byKey.set(r.ruleKey, []); byKey.get(r.ruleKey).push(r); }
  const groups = [...byKey.values()].map((versions) => versions.sort((a, b) => b.version - a.version));
  return h("div", { class: "dw-card" },
    h("div", { class: "d-flex justify-content-between align-items-center" }, h("h2", {}, "Rules"),
      h("button", { class: "btn btn-primary btn-sm", type: "button", disabled: !can, onclick: () => ruleDialog(ctx, state, {}, repaint) }, "Write a rule")),
    h("p", { class: "text-muted small" }, "A rule is your decision about how to read something ambiguous, for example “a boot labelled 4x12 means 4x12x7”. It is written down, previewed, approved by a person, and only then changes a count. Changing one makes a new version."),
    groups.length ? groups.map((g) => ruleRow(ctx, state, g, repaint)) : h("p", { class: "text-muted" }, "No rules yet."));
}

function ruleRow(ctx, state, versions, repaint) {
  const latest = versions[0];
  const inForce = versions.find((v) => v.status === "Approved");
  const shown = inForce || latest;
  const can = ctx.canChange;
  const act = (label, kind, fn, disabled = false) => h("button", { class: `btn btn-${kind} btn-sm`, type: "button", disabled: !can || disabled, onclick: fn }, label);
  const buttons = [];
  if (shown.status === "Proposed") buttons.push(act("Preview", "outline-secondary", () => previewDialog(ctx, shown)), act("Approve", "outline-success", async () => { if (await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${shown.ruleId}/approve`), { ok: "Approved." })) repaint(); }));
  if (shown.status === "Approved") buttons.push(
    act("Apply…", "outline-primary", () => applyDialog(ctx, shown, repaint)),
    act("Undo its changes", "outline-secondary", async () => { if (await confirmDialog(`Take back what “${shown.name}” changed, where nothing has changed since?`, { ok: "Undo" })) { const r = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${shown.ruleId}/undo-application`)); if (r) { toast(`${r.undone} item(s) restored, ${r.skipped} had changed since and were left.`, "success", 5000); ctx.invalidate("entities"); repaint(); } } }),
    act("Change…", "outline-secondary", () => ruleDialog(ctx, state, { revise: shown }, repaint)));
  if (shown.status !== "Retired") buttons.push(act("Retire", "outline-danger", async () => { const reason = await promptDialog("Retire this rule? Items it already changed keep their values.", { title: "Retire rule", placeholder: "Why?", ok: "Retire" }); if (reason && await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${shown.ruleId}/retire`, { reason }), { ok: "Retired." })) repaint(); }));

  return h("div", { class: `dw-rule dw-rule-${shown.status.toLowerCase()}` },
    h("div", { class: "d-flex gap-2 align-items-center flex-wrap" }, h("strong", {}, shown.name), badge(titleCase(shown.scope), "light"), badge(`v${shown.version}`, "light"), badge(shown.status, { Approved: "success", Proposed: "warning", Retired: "secondary" }[shown.status])),
    h("div", { class: "small" }, describeRule(shown)),
    latest.version > shown.version && latest.status === "Proposed" ? h("div", { class: "small text-warning" }, `A newer version (v${latest.version}) is waiting for approval.`) : null,
    shown.description ? h("div", { class: "small text-muted" }, shown.description) : null,
    shown.status === "Retired" ? h("div", { class: "small text-muted" }, `Retired by ${shown.retiredBy}: ${shown.retireReason}`) : null,
    h("div", { class: "d-flex gap-1 flex-wrap mt-1" }, buttons));
}

export function describeRule(r) {
  const m = r.match || {};
  const a = r.action || {};
  const when = [m.type && `type is ${m.type}`, m.size && `size is ${m.size}`, m.sizePattern && `size matches ${m.sizePattern}`, m.tag && `tag is ${m.tag}`, m.buildingType && `building type is ${m.buildingType}`, m.method && `found by ${m.method}`].filter(Boolean).join(" and ");
  const then = [a.setType && `set the type to ${a.setType}`, a.setSize && `set the size to ${a.setSize}`, a.sizeSuffix && `add “${a.sizeSuffix}” to the size`, a.multiplyQuantity != null && `multiply the quantity by ${a.multiplyQuantity}`].filter(Boolean).join(", ");
  return `When ${when || "anything"}: ${then || "do nothing"}.`;
}

async function renderPreview(ctx, host, fetcher) {
  clear(host);
  host.append(h("div", { class: "text-muted small" }, "Working out what it would change…"));
  const p = await ctx.run(fetcher);
  clear(host);
  if (!p) return;
  host.append(
    h("div", { class: "mb-1" }, h("strong", {}, `${p.changes.length} item(s) would change`), p.skipped.length ? `, ${p.skipped.length} left alone` : "", p.conflicts.length ? `, ${p.conflicts.length} in conflict` : ""),
    p.changes.length ? h("table", { class: "table table-sm" }, h("tbody", {}, p.changes.slice(0, 25).map((c) => h("tr", {}, h("td", {}, c.item), h("td", { class: "text-muted small" }, c.change))))) : null,
    p.changes.length > 25 ? h("div", { class: "small text-muted" }, `…and ${p.changes.length - 25} more.`) : null,
    p.skipped.length ? h("details", { class: "small mt-1" }, h("summary", {}, "Left alone"), p.skipped.slice(0, 20).map((s) => h("div", {}, `${s.item}: ${s.reason}`))) : null,
    p.conflicts.length ? h("div", { class: "small text-danger mt-1" }, `${p.conflicts.length} item(s) are decided differently by another rule of the same strength. Applying will raise a question about each.`) : null);
}

function previewDialog(ctx, rule) {
  const host = h("div", { class: "dw-preview" });
  renderPreview(ctx, host, () => ctx.api.get(`/jobs/${ctx.jobId}/rules/${rule.ruleId}/preview`));
  return dialog({ title: `What “${rule.name}” would change`, wide: true, body: host, actions: [{ label: "Close", kind: "primary" }] });
}

async function applyDialog(ctx, rule, repaint) {
  const host = h("div", { class: "dw-preview" });
  renderPreview(ctx, host, () => ctx.api.get(`/jobs/${ctx.jobId}/rules/${rule.ruleId}/preview`));
  const result = await dialog({ title: `Apply “${rule.name}”`, wide: true, body: host, actions: [{ label: "Cancel" }, { label: "Apply", kind: "primary" }] });
  if (result !== "Apply") return;
  const r = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${rule.ruleId}/apply`));
  if (!r) return;
  toast(`${r.changed} item(s) changed, ${r.skipped} left alone${r.conflicts ? `, ${r.conflicts} in conflict` : ""}.`, "success", 5000);
  ctx.invalidate("entities");
  repaint();
}

/**
 * The rule builder. `finding` pre-fills it from the item a question is about; `revise` starts from an existing rule.
 * It can preview before anything is saved, and can save a proposal or approve and apply in one step.
 */
async function ruleDialog(ctx, state, { finding, revise } = {}, repaint) {
  const item = finding?.item || "";
  const base = revise ? { ...revise } : null;
  const t = (v) => h("input", { class: "form-control form-control-sm", value: v ?? "", maxlength: 100 });
  const f = {
    name: t(base?.name || (finding ? `Answer to: ${item}` : "")), description: t(base?.description),
    scope: select(SCOPES, base?.scope || "Job", { disabled: !!revise }),
    building: select([["", "—"], ...state.types.map((b) => [b.buildingTypeId, b.name])], base?.buildingTypeId ?? "", { disabled: !!revise }),
    sheet: select([["", "—"], ...state.sheets.map((s) => [s.sheetId, s.sheetNumber || `Page ${s.pageNo}`])], base?.sheetId ?? "", { disabled: !!revise }),
    mType: t(base?.match?.type), mSize: t(base?.match?.size), mPattern: t(base?.match?.sizePattern), mTag: t(base?.match?.tag), mBuilding: t(base?.match?.buildingType),
    aSetType: t(base?.action?.setType), aSetSize: t(base?.action?.setSize), aSuffix: t(base?.action?.sizeSuffix),
    aMultiply: h("input", { class: "form-control form-control-sm", type: "number", min: "0", step: "any", value: base?.action?.multiplyQuantity ?? "" })
  };
  const preview = h("div", { class: "dw-preview" });

  const body = () => {
    const nz = (v) => (v.value.trim() === "" ? null : v.value.trim());
    return {
      name: f.name.value.trim(), description: nz(f.description), scope: f.scope.value, buildingTypeId: f.scope.value === "BuildingType" && f.building.value ? Number(f.building.value) : null,
      sheetId: f.scope.value === "Sheet" && f.sheet.value ? f.sheet.value : null,
      match: { type: nz(f.mType), size: nz(f.mSize), sizePattern: nz(f.mPattern), tag: nz(f.mTag), buildingType: nz(f.mBuilding) },
      action: { setType: nz(f.aSetType), setSize: nz(f.aSetSize), sizeSuffix: nz(f.aSuffix), multiplyQuantity: f.aMultiply.value === "" ? null : Number(f.aMultiply.value) },
      fromFindingId: finding?.findingId ?? null
    };
  };

  const previewBtn = h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: () => renderPreview(ctx, preview, () => ctx.api.post(`/jobs/${ctx.jobId}/rules/preview`, body())) }, "Show what it would change");
  const scopeHint = h("div", { class: "form-text" }, "");
  const syncScope = () => { f.building.parentElement.hidden = f.scope.value !== "BuildingType"; f.sheet.parentElement.hidden = f.scope.value !== "Sheet"; scopeHint.textContent = f.scope.value === "Instance" ? "A rule for every job needs a Job Manager or an administrator." : ""; };

  const form = h("div", { class: "dw-formgrid" },
    field("Name", f.name), field("Why", f.description), field("Applies to", f.scope, null), field("Building type", f.building), field("Sheet", f.sheet), scopeHint,
    h("h3", { class: "dw-h3 w-100" }, "When an item matches"),
    field("Type is", f.mType), field("Size is", f.mSize), field("Size matches pattern", f.mPattern, "A regular expression, for example ^\\d+x\\d+$ for sizes with two dimensions."), field("Tag is", f.mTag), field("Building type is", f.mBuilding),
    h("h3", { class: "dw-h3 w-100" }, "Then"),
    field("Set the type to", f.aSetType), field("Set the size to", f.aSetSize), field("Add to the size", f.aSuffix, "For example x7 turns 4x12 into 4x12x7."), field("Multiply the quantity by", f.aMultiply, "For example 2 for two grilles per flag."),
    h("div", { class: "w-100" }, previewBtn, preview));
  f.scope.addEventListener("change", syncScope);
  syncScope();

  const labels = revise ? ["Cancel", "Save new version"] : ["Cancel", "Save for approval", "Approve and apply"];
  const result = await dialog({
    title: revise ? `Change “${revise.name}”` : finding ? "Write a rule to answer this question" : "Write a rule", wide: true, body: form,
    actions: labels.map((label, i) => (i === 0 ? { label } : { label, kind: i === labels.length - 1 ? "primary" : "outline-primary", validate: () => (f.name.value.trim() ? null : "Give the rule a name.") }))
  });
  if (!result || result === "Cancel") return;

  const payload = body();
  if (revise) {
    if (await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${revise.ruleId}/revise`, payload), { ok: "Saved as a new version, waiting for approval." })) repaint();
    return;
  }

  const created = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/rules`, payload));
  if (!created) return;
  if (result === "Save for approval") { toast("Saved. Approve it when you are happy with the preview.", "success"); repaint(); return; }

  const approved = await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${created.ruleId}/approve`));
  if (!approved) { repaint(); return; }
  const applied = await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/rules/${created.ruleId}/apply`));
  if (applied) {
    toast(`${applied.changed} item(s) changed, ${applied.skipped} left alone${applied.conflicts ? `, ${applied.conflicts} in conflict` : ""}.`, "success", 5000);
    ctx.invalidate("entities");
    if (finding?.entityId) await ctx.run(() => ctx.api.post(`/jobs/${ctx.jobId}/findings/${finding.findingId}/resolve`, { kind: "Rule", ruleId: created.ruleId, note: null }));
  }

  repaint();
}
