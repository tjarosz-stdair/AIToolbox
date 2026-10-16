// Setup: the building types with their multipliers (the number of identical buildings or units each count stands for),
// and the views on each sheet with their scales. A multiplier is something a person states; a detector can only suggest one.

import { h, clear, field, select, badge, dialog, confirmDialog, titleCase } from "./util.js";
import { STANDARD_SCALES, scaleLabel } from "./geometry.js";

const KINDS = ["UnitType", "Building", "CommonArea", "Other"];
const VIEW_TYPES = ["Plan", "Section", "Elevation", "Detail", "Schedule", "Riser", "Other"];

export async function render(container, ctx) {
  const paint = async () => {
    const [types, sheets] = await Promise.all([ctx.buildingTypes(true), ctx.sheets(true)]);
    clear(container);
    container.append(typesCard(ctx, types, paint), viewsCard(ctx, sheets, types, paint));
  };

  await paint();
  return { refresh: paint };
}

// ------------------------------------------------------------------ building types

function typesCard(ctx, types, repaint) {
  const can = ctx.canChange;
  const rows = types.map((t) => {
    const state = t.confirmed
      ? badge(`${t.multiplier} confirmed`, "success")
      : t.multiplier != null ? badge(`${t.multiplier} suggested`, "warning") : badge("not set", "danger");
    return h("tr", {},
      h("td", {}, h("strong", {}, t.name), t.notes ? h("div", { class: "small text-muted" }, t.notes) : null),
      h("td", {}, titleCase(t.kind)),
      h("td", {}, state, t.confirmed ? h("div", { class: "small text-muted" }, `${titleCase(t.multiplierSource)}${t.multiplierEvidence ? `: ${t.multiplierEvidence}` : ""}`) : null),
      h("td", { class: "text-end" }, h("div", { class: "d-flex gap-1 justify-content-end flex-wrap" },
        h("button", { class: "btn btn-outline-primary btn-sm", type: "button", disabled: !can, onclick: () => multiplierDialog(ctx, t, repaint) }, t.confirmed ? "Change count" : "Set count"),
        h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !can, onclick: () => typeDialog(ctx, t, repaint) }, "Edit"),
        h("button", {
          class: "btn btn-outline-danger btn-sm", type: "button", disabled: !can,
          onclick: async () => { if (await confirmDialog(`Delete the building type "${t.name}"? This only works if no counted items use it.`, { ok: "Delete", kind: "danger" }) && await ctx.edit(() => ctx.api.del(`/jobs/${ctx.jobId}/building-types/${t.buildingTypeId}`), { ok: "Deleted." })) { ctx.invalidate("types"); repaint(); } }
        }, "Delete"))));
  });

  return h("div", { class: "dw-card" },
    h("div", { class: "d-flex justify-content-between align-items-center" }, h("h2", {}, "Building types and multipliers"),
      h("button", { class: "btn btn-primary btn-sm", type: "button", disabled: !can, onclick: () => typeDialog(ctx, null, repaint) }, "Add building type")),
    h("p", { class: "text-muted small" }, "Identical buildings or units are counted once and multiplied. The count of how many there are comes from you (or the architectural building list), never from a detector, and a job cannot be completed with an unconfirmed count."),
    types.length ? h("table", { class: "table table-sm align-middle" }, h("thead", {}, h("tr", {}, ["Name", "Kind", "How many", ""].map((x) => h("th", {}, x)))), h("tbody", {}, rows))
      : h("p", { class: "text-muted" }, "No building types yet. They are created automatically from a detector's results, or add one here."));
}

async function typeDialog(ctx, existing, repaint) {
  const name = h("input", { class: "form-control", value: existing?.name || "", maxlength: 100 });
  const kind = select(KINDS.map((k) => [k, titleCase(k)]), existing?.kind || "UnitType");
  const notes = h("input", { class: "form-control", value: existing?.notes || "", maxlength: 1000 });
  const result = await dialog({
    title: existing ? "Edit building type" : "Add building type",
    body: h("div", {}, field("Name", name, "For example Type 4, Unit B, or Leasing Office."), field("Kind", kind), field("Notes", notes)),
    actions: [{ label: "Cancel" }, { label: "Save", kind: "primary", validate: () => (name.value.trim() ? null : "Enter a name.") }]
  });
  if (result !== "Save") return;
  const body = { name: name.value.trim(), kind: kind.value, notes: notes.value.trim() || null };
  const done = await ctx.edit(() => (existing ? ctx.api.put(`/jobs/${ctx.jobId}/building-types/${existing.buildingTypeId}`, body) : ctx.api.post(`/jobs/${ctx.jobId}/building-types`, body)), { ok: "Saved." });
  if (done) { ctx.invalidate("types"); repaint(); }
}

async function multiplierDialog(ctx, type, repaint) {
  const count = h("input", { class: "form-control", type: "number", min: "0", step: "1", value: type.multiplier ?? "" });
  const source = select([["Manual", "My own count"], ["ArchitecturalList", "From the architectural building list"]], type.multiplierSource === "ArchitecturalList" ? "ArchitecturalList" : "Manual");
  const evidence = h("input", { class: "form-control", value: type.multiplierEvidence || "", maxlength: 1000, placeholder: "For example: sheet T1.1, rows A to Q" });
  const hint = h("div", { class: "form-text" }, "");
  const sync = () => { hint.textContent = source.value === "ArchitecturalList" ? "Say where the list is (sheet and rows) so the number can be checked." : "Optional: a note on how you counted."; };
  source.addEventListener("change", sync);
  sync();

  const result = await dialog({
    title: `How many ${type.name}?`,
    body: h("div", {}, type.multiplier != null && !type.confirmed ? h("p", { class: "alert alert-warning py-2" }, `A detector suggested ${type.multiplier}. That is only a hint until you confirm a number.`) : null,
      field("Number of identical buildings or units (zero is a valid answer)", count), field("Where it comes from", source), field("Evidence", evidence, null), hint),
    actions: [{ label: "Cancel" }, ...(type.confirmed ? [{ label: "Clear", kind: "outline-danger" }] : []),
      { label: "Confirm", kind: "primary", validate: () => (count.value === "" || Number(count.value) < 0 || !Number.isInteger(Number(count.value)) ? "Enter a whole number, zero or more." : source.value === "ArchitecturalList" && !evidence.value.trim() ? "Say where the list is." : null) }]
  });

  if (result === "Clear") {
    if (await ctx.edit(() => ctx.api.del(`/jobs/${ctx.jobId}/building-types/${type.buildingTypeId}/multiplier`), { ok: "Cleared." })) { ctx.invalidate("types"); repaint(); }
  } else if (result === "Confirm") {
    const body = { multiplier: Number(count.value), source: source.value, evidence: evidence.value.trim() || null };
    if (await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/building-types/${type.buildingTypeId}/multiplier`, body), { ok: "Confirmed." })) { ctx.invalidate("types"); repaint(); }
  }
}

// ------------------------------------------------------------------ views and scales

function viewsCard(ctx, sheets, types, repaint) {
  const plans = sheets.filter((s) => s.inScope);
  const typeNames = new Map(types.map((t) => [t.buildingTypeId, t.name]));
  const items = plans.map((s) => sheetBlock(ctx, s, types, typeNames, repaint));
  return h("div", { class: "dw-card" },
    h("h2", {}, "Views and scales"),
    h("p", { class: "text-muted small" }, "A sheet can hold several drawings at different scales (a plan and an enlarged plan). A scale is never assumed: until you state it or calibrate it against a known length, lengths are not measured from that view."),
    items.length ? items : h("p", { class: "text-muted" }, "No sheets in scope yet. Upload drawings on the Drawings tab."));
}

function sheetBlock(ctx, sheet, types, typeNames, repaint) {
  const can = ctx.canChange;
  const views = sheet.views.map((v) => viewRow(ctx, sheet, v, types, typeNames, repaint));
  return h("details", { class: "dw-sheetblock", open: sheet.views.length > 0 || null },
    h("summary", {}, h("strong", {}, sheet.sheetNumber || `Page ${sheet.pageNo}`), " ", sheet.title || "", " ", badge(`${sheet.views.length} view(s)`, "light")),
    h("div", { class: "dw-sheetbody" },
      views.length ? h("table", { class: "table table-sm align-middle" }, h("tbody", {}, views)) : h("p", { class: "text-muted small mb-1" }, "No views. Items on this sheet are shown against the whole page."),
      h("button", { class: "btn btn-outline-primary btn-sm", type: "button", disabled: !can, onclick: () => viewDialog(ctx, sheet, null, types, repaint) }, "Add view")));
}

function viewRow(ctx, sheet, view, types, typeNames, repaint) {
  const can = ctx.canChange;
  return h("tr", {},
    h("td", {}, h("strong", {}, view.title), h("div", { class: "small text-muted" }, [view.floor ? `Floor ${view.floor}` : null, titleCase(view.type), view.buildingTypeId ? typeNames.get(view.buildingTypeId) : null].filter(Boolean).join(" · "))),
    h("td", {}, view.scaleRatio != null ? [badge(scaleLabel(view.scaleRatio), "success"), h("div", { class: "small text-muted" }, `${titleCase(view.scaleMethod)} by ${view.scaleSetBy}`)] : badge("scale not set", "warning")),
    h("td", { class: "text-end" }, h("div", { class: "d-flex gap-1 justify-content-end flex-wrap" },
      h("button", { class: "btn btn-outline-primary btn-sm", type: "button", disabled: !can, onclick: () => scaleDialog(ctx, view, repaint) }, "Scale"),
      h("a", { class: `btn btn-outline-secondary btn-sm${can ? "" : " disabled"}`, href: `#/review/${sheet.sheetId}/calibrate/${view.viewId}` }, "Calibrate"),
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !can, onclick: () => viewDialog(ctx, sheet, view, types, repaint) }, "Edit"),
      h("button", {
        class: "btn btn-outline-danger btn-sm", type: "button", disabled: !can,
        onclick: async () => { if (await confirmDialog(`Remove the view "${view.title}"?`, { ok: "Remove", kind: "danger" }) && await ctx.edit(() => ctx.api.del(`/jobs/${ctx.jobId}/views/${view.viewId}`), { ok: "Removed." })) { ctx.invalidate("sheets"); repaint(); } }
      }, "Remove"))));
}

async function viewDialog(ctx, sheet, view, types, repaint) {
  const title = h("input", { class: "form-control", value: view?.title || "", maxlength: 200, placeholder: "For example Unit plan, or Enlarged kitchen" });
  const floor = h("input", { class: "form-control", value: view?.floor || "", maxlength: 50 });
  const type = select(VIEW_TYPES, view?.type || "Plan");
  const building = select([["", "None"], ...types.map((t) => [t.buildingTypeId, t.name])], view?.buildingTypeId ?? "");
  const result = await dialog({
    title: view ? "Edit view" : `Add a view to ${sheet.sheetNumber || `page ${sheet.pageNo}`}`,
    body: h("div", {}, field("Title", title), field("Floor", floor), field("Type of drawing", type), field("Building type", building, "Items counted in this view are assigned to it.")),
    actions: [{ label: "Cancel" }, { label: "Save", kind: "primary", validate: () => (title.value.trim() ? null : "Enter a title.") }]
  });
  if (result !== "Save") return;
  const buildingTypeId = building.value === "" ? null : Number(building.value);
  const done = await ctx.edit(() => (view
    ? ctx.api.patch(`/jobs/${ctx.jobId}/views/${view.viewId}`, { title: title.value.trim(), floor: floor.value.trim(), type: type.value, buildingTypeId, clearBuildingType: buildingTypeId === null && view.buildingTypeId != null })
    : ctx.api.post(`/jobs/${ctx.jobId}/sheets/${sheet.sheetId}/views`, { title: title.value.trim(), floor: floor.value.trim() || null, type: type.value, buildingTypeId, polygon: null })), { ok: "Saved." });
  if (done) { ctx.invalidate("sheets"); repaint(); }
}

async function scaleDialog(ctx, view, repaint) {
  const presets = select([["", "Choose a standard scale…"], ...STANDARD_SCALES.map(([label, ratio]) => [ratio, label]), ["custom", "Other (type a ratio)"]], view.scaleRatio != null && STANDARD_SCALES.some(([, r]) => r === view.scaleRatio) ? view.scaleRatio : view.scaleRatio != null ? "custom" : "");
  const custom = h("input", { class: "form-control", type: "number", step: "any", min: "0", value: view.scaleRatio ?? "", placeholder: "real length per drawn length, e.g. 48" });
  presets.addEventListener("change", () => { if (presets.value && presets.value !== "custom") custom.value = presets.value; });
  const result = await dialog({
    title: `Scale of ${view.title}`,
    body: h("div", {}, h("p", { class: "text-muted small" }, "State the scale printed on the drawing. To measure it against a known length instead, use Calibrate."), field("Scale", presets), field("Ratio", custom, "1/4 inch = 1 foot is 48.")),
    actions: [{ label: "Cancel" }, ...(view.scaleRatio != null ? [{ label: "Clear", kind: "outline-danger" }] : []),
      { label: "Set scale", kind: "primary", validate: () => (Number(custom.value) > 0 ? null : "Choose a scale or enter a positive ratio.") }]
  });
  if (result === "Clear") {
    if (await ctx.edit(() => ctx.api.del(`/jobs/${ctx.jobId}/views/${view.viewId}/scale`), { ok: "Scale cleared." })) { ctx.invalidate("sheets"); repaint(); }
  } else if (result === "Set scale") {
    if (await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/views/${view.viewId}/scale`, { ratio: Number(custom.value), method: "Stated", calibration: null }), { ok: "Scale set." })) { ctx.invalidate("sheets"); repaint(); }
  }
}

