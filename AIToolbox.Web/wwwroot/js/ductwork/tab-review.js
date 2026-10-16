// Review: the sheet viewer with the items the detector found, a tree of everything by building type, and a panel for the
// item you are looking at. A person verifies or rejects what was found, corrects it, adds what was missed, decides what to
// do about it, and takes any of it back. Nothing a detector proposes counts as checked until a person says so.

import { h, clear, field, select, badge, titleCase, toast, dialog, promptDialog, confirmDialog, pct, fmtDate, fmtQty, debounce } from "./util.js";
import { SheetViewer } from "./viewer.js";
import { openPdf } from "./pdfpages.js";
import * as model from "./model.js";
import * as geo from "./geometry.js";

const UNITS = ["EA", "LF", "SF"];

export async function render(container, ctx) {
  const [sheetsAll, entities, types] = await Promise.all([ctx.sheets(true), ctx.entities(true), ctx.buildingTypes(true)]);
  const sheets = sheetsAll.filter((s) => s.inScope).sort((a, b) => (a.fileId === b.fileId ? a.pageNo - b.pageNo : String(a.fileId).localeCompare(b.fileId)));

  const st = {
    ctx, sheets, allSheets: sheetsAll, entities, types,
    sheetId: null, selectedId: null, selection: new Set(), expanded: new Set(),
    filter: { statuses: new Set(), dispositions: new Set(), minConfidence: null, text: "", showRejected: false, thisSheetOnly: true },
    advance: true, tool: "select", calibrateViewId: null, viewer: null
  };

  const route = (location.hash.match(/^#\/review\/([^/]+)(?:\/calibrate\/([^/]+))?/) || []);
  st.sheetId = sheets.find((s) => s.sheetId === route[1])?.sheetId || sheets.find((s) => model.entitiesForSheet(entities, s).length)?.sheetId || sheets[0]?.sheetId || null;
  st.calibrateViewId = route[2] || null;

  if (!sheets.length) {
    container.append(h("div", { class: "dw-card" }, h("h2", {}, "Nothing to review yet"), h("p", { class: "text-muted" }, "Upload drawings and run detection first. Sheets that are in scope appear here.")));
    return {};
  }

  const els = {
    top: h("div", { class: "dw-review-top" }),
    tree: h("div", { class: "dw-tree" }),
    filters: h("div", { class: "dw-filters" }),
    stage: h("div", { class: "dw-stage" }),
    panel: h("div", { class: "dw-itempanel" })
  };
  const left = h("div", { class: "dw-left" }, els.filters, els.tree);
  const center = h("div", { class: "dw-center" }, els.top, els.stage);
  container.append(h("div", { class: "dw-review" }, left, center, els.panel));

  st.viewer = new SheetViewer(els.stage, {
    onSelect: (id, { additive } = {}) => selectItem(id, { additive }),
    onTool: (ev) => handleTool(ev)
  });

  // ---- derived views of the data

  const filtered = () => model.filterEntities(st.entities, { ...st.filter, statuses: st.filter.statuses, dispositions: st.filter.dispositions });
  const currentSheet = () => st.sheets.find((s) => s.sheetId === st.sheetId) || null;
  const visibleEntities = () => {
    const all = filtered();
    const sheet = currentSheet();
    return st.filter.thisSheetOnly && sheet ? model.entitiesForSheet(all, sheet) : all;
  };
  const selected = () => st.entities.find((e) => e.publicId === st.selectedId) || null;

  const setEntities = (list) => {
    st.entities = list;
    ctx.cache.entities = Promise.resolve(list);
  };

  const applyUpdated = (updated) => {
    setEntities(model.replaceEntity(st.entities, updated));
    paintAll();
  };

  // ---- the viewer

  const docs = (ctx.cache.docs ||= new Map());
  const docFor = (fileId) => {
    if (!docs.has(fileId)) docs.set(fileId, openPdf(ctx.boot, { url: ctx.api.url(`/jobs/${ctx.jobId}/files/${fileId}/content?inline=true`) }));
    return docs.get(fileId);
  };

  let shown = null;
  const showSheet = async (sheetId, { keepSelection = true } = {}) => {
    st.sheetId = sheetId;
    const sheet = currentSheet();
    if (!sheet) return;
    history.replaceState(null, "", `#/review/${sheet.sheetId}`);
    paintTop();
    if (!keepSelection) st.selectedId = null;
    try {
      const doc = await docFor(sheet.fileId);
      if (st.sheetId !== sheetId) return;
      const page = await doc.getPage(sheet.pageNo);
      if (st.sheetId !== sheetId) return;
      await st.viewer.showPage(page);
      shown = sheetId;
    } catch (e) {
      st.viewer.clearPage();
      toast(`The drawing could not be shown: ${e.message || e}`, "error");
    }

    paintViewerItems();
    paintTree();
    paintPanel();
    if (st.selectedId) st.viewer.select(st.selectedId, { focus: true });
  };

  const viewerItems = () => {
    const sheet = currentSheet();
    if (!sheet) return [];
    return model.entitiesForSheet(filtered(), sheet).filter((e) => e.box).map((e) => ({
      id: e.publicId, box: e.box, status: model.statusKey(e), dashed: e.disposition === "TBD" || e.disposition === "Excluded" || e.disposition === "FactoryFurnished",
      label: `${e.type}${e.size ? " " + e.size : ""}`,
      tip: `${model.itemLabel(e)} · ${e.reviewStatus}${e.disposition !== "Included" ? " · " + titleCase(e.disposition) : ""} · ${pct(e.confidence)}`
    }));
  };

  const paintViewerItems = () => {
    st.viewer.setItems(viewerItems());
    const sheet = currentSheet();
    st.viewer.setRegions((sheet?.regions || []).map((r) => ({ kind: r.kind, polygon: r.polygon })));
    st.viewer.select(st.selectedId);
  };

  // ---- selecting

  const selectItem = async (id, { additive = false, focus = false } = {}) => {
    if (additive && id) {
      st.selection.has(id) ? st.selection.delete(id) : st.selection.add(id);
      paintTree();
      paintToolbarCount();
      return;
    }

    st.selectedId = id;
    if (id) {
      const entity = selected();
      const sheet = entity ? model.sheetOf(entity, st.sheets) : null;
      if (sheet && sheet.sheetId !== st.sheetId) { await showSheet(sheet.sheetId); }
      st.viewer.select(id, { focus: focus || !geoVisible(entity) });
      expandTo(entity);
    } else {
      st.viewer.select(null);
    }

    paintTree();
    paintPanel();
  };

  const geoVisible = (e) => {
    if (!e?.box) return true;
    const v = st.viewer.view;
    const r = geo.boxToScreen(v, e.box);
    return r.x >= 0 && r.y >= 0 && r.x + r.w <= st.viewer.width && r.y + r.h <= st.viewer.height;
  };

  const expandTo = (e) => {
    if (!e) return;
    st.expanded.add(`bt:${e.buildingTypeId ?? "none"}`);
    st.expanded.add(`g:${e.buildingTypeId ?? "none"}:${[e.type, e.size ?? "", e.tag ?? ""].join("|")}`);
  };

  const queue = () => visibleEntities();
  const step = (dir) => { const next = model.stepItem(queue(), st.selectedId, dir); if (next) selectItem(next.publicId, { focus: true }); };
  const nextProposal = () => {
    const q = queue();
    const i = q.findIndex((e) => e.publicId === st.selectedId);
    const after = [...q.slice(i + 1), ...q.slice(0, Math.max(i, 0))].find((e) => e.reviewStatus === "Proposed" && e.publicId !== st.selectedId);
    return after || null;
  };

  // ---- actions on items

  const review = async (entity, status, reason = null) => {
    const updated = await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/entities/${entity.publicId}/review`, { status, expectedRevision: entity.revision, reason }));
    if (!updated) return;
    const nxt = st.advance && status !== "Proposed" ? nextProposal() : null;
    applyUpdated(updated);
    if (nxt) selectItem(nxt.publicId, { focus: true });
  };

  const bulk = async (status, ids) => {
    if (!ids.length) { toast("Choose some items first.", "error"); return; }
    let reason = null;
    if (status === "Rejected") { reason = await promptDialog(`Reject ${ids.length} item(s)? Say why, so it can be explained later.`, { title: "Reject items", required: false, placeholder: "Optional reason", ok: "Reject" }); if (reason === null) return; }
    const result = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/entities/review`, { entityIds: ids, status, reason }));
    if (!result) return;
    toast(`${result.changed} item(s) ${status === "Verified" ? "verified" : status === "Rejected" ? "rejected" : "reopened"}.`, "success", 3000);
    st.selection.clear();
    setEntities(await ctx.entities(true));
    paintAll();
  };

  const undo = async (entity) => {
    const updated = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/entities/${entity.publicId}/undo`));
    if (updated) applyUpdated(updated);
  };

  const undoMine = async () => {
    const updated = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/undo`));
    if (!updated) return;
    applyUpdated(updated);
    toast("Took back your last change.", "success", 2500);
    selectItem(updated.publicId, { focus: true });
  };

  const setDisposition = async (entity) => {
    const sel = select([["Included", "Count it"], ["Excluded", "Leave it out"], ["FactoryFurnished", "Furnished by the factory"], ["TBD", "Not decided yet"]], entity.disposition);
    const reason = h("input", { class: "form-control", value: entity.dispositionReason || "", maxlength: 400, placeholder: "Why" });
    const row = field("Reason", reason, "Needed for leaving an item out or marking it factory-furnished.");
    const toggle = () => { row.hidden = !["Excluded", "FactoryFurnished"].includes(sel.value); };
    sel.addEventListener("change", toggle);
    toggle();
    const result = await dialog({
      title: "What should happen to this item?", body: h("div", {}, field("Decision", sel), row),
      actions: [{ label: "Cancel" }, { label: "Save", kind: "primary", validate: () => (["Excluded", "FactoryFurnished"].includes(sel.value) && !reason.value.trim() ? "A reason is needed." : null) }]
    });
    if (result !== "Save") return;
    const updated = await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/entities/${entity.publicId}/disposition`, { disposition: sel.value, expectedRevision: entity.revision, reason: reason.value.trim() || null }));
    if (updated) applyUpdated(updated);
  };

  const editItem = async (entity) => {
    const f = {
      type: h("input", { class: "form-control", value: entity.type, maxlength: 100 }),
      size: h("input", { class: "form-control", value: entity.size || "", maxlength: 100 }),
      tag: h("input", { class: "form-control", value: entity.tag || "", maxlength: 100 }),
      quantity: h("input", { class: "form-control", type: "number", min: "0", step: "any", value: entity.quantity }),
      unit: select(UNITS, entity.unit),
      category: h("input", { class: "form-control", value: entity.category || "", maxlength: 100 }),
      manufacturer: h("input", { class: "form-control", value: entity.manufacturer || "", maxlength: 100 }),
      model: h("input", { class: "form-control", value: entity.model || "", maxlength: 100 }),
      partCode: h("input", { class: "form-control", value: entity.customerPartCode || "", maxlength: 100 }),
      building: select([["", "Not assigned"], ...st.types.map((t) => [t.buildingTypeId, t.name])], entity.buildingTypeId ?? ""),
      reason: h("input", { class: "form-control", maxlength: 400, placeholder: "Optional note on why" })
    };
    const result = await dialog({
      title: "Correct this item", wide: true,
      body: h("div", { class: "dw-formgrid" }, field("Type", f.type), field("Size", f.size), field("Tag", f.tag), field("Quantity", f.quantity), field("Unit", f.unit), field("Building type", f.building),
        field("Category", f.category), field("Manufacturer", f.manufacturer), field("Model", f.model), field("Part code", f.partCode), field("Note", f.reason)),
      actions: [{ label: "Cancel" }, { label: "Save", kind: "primary", validate: () => (!f.type.value.trim() ? "An item needs a type." : Number(f.quantity.value) < 0 ? "A quantity cannot be negative." : null) }]
    });
    if (result !== "Save") return;
    const bt = f.building.value === "" ? null : Number(f.building.value);
    const body = {
      expectedRevision: entity.revision, reason: f.reason.value.trim() || null, type: f.type.value, size: f.size.value, tag: f.tag.value, category: f.category.value, manufacturer: f.manufacturer.value,
      model: f.model.value, customerPartCode: f.partCode.value, quantity: Number(f.quantity.value), unit: f.unit.value, buildingTypeId: bt, clearBuildingType: bt === null && entity.buildingTypeId != null,
      viewId: null, clearSheetView: false
    };
    const updated = await ctx.edit(() => ctx.api.patch(`/jobs/${ctx.jobId}/entities/${entity.publicId}`, body));
    if (updated) applyUpdated(updated);
  };

  // ---- tools

  const ensureView = async (sheet) => {
    if (sheet.views.length) return sheet.views[0];
    const created = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/sheets/${sheet.sheetId}/views`, { title: "Whole page", floor: null, type: "Plan", buildingTypeId: null, polygon: null }));
    if (!created) return null;
    ctx.invalidate("sheets");
    const fresh = await ctx.sheets(true);
    st.allSheets = fresh;
    st.sheets = fresh.filter((s) => s.inScope).sort((a, b) => (a.fileId === b.fileId ? a.pageNo - b.pageNo : String(a.fileId).localeCompare(b.fileId)));
    return st.sheets.find((s) => s.sheetId === sheet.sheetId)?.views[0] || null;
  };

  const handleTool = async (ev) => {
    const sheet = currentSheet();
    if (!sheet) return;
    if (ev.tool === "add") await addItemDialog(sheet, ev.point);
    else if (ev.tool === "calibrate") await calibrate(sheet, ev.points);
    else if (ev.tool === "region") await addRegion(sheet, ev.polygon);
  };

  const addItemDialog = async (sheet, point) => {
    const f = {
      type: h("input", { class: "form-control", maxlength: 100, placeholder: "For example Boot, Damper, Grille" }), size: h("input", { class: "form-control", maxlength: 100 }),
      tag: h("input", { class: "form-control", maxlength: 100 }), quantity: h("input", { class: "form-control", type: "number", min: "0", step: "any", value: 1 }), unit: select(UNITS, "EA"),
      building: select([["", "Not assigned"], ...st.types.map((t) => [t.buildingTypeId, t.name])], ""), evidence: h("input", { class: "form-control", maxlength: 1000, placeholder: "What you saw" })
    };
    const result = await dialog({
      title: "Add an item the detector missed", body: h("div", { class: "dw-formgrid" }, field("Type", f.type), field("Size", f.size), field("Tag", f.tag), field("Quantity", f.quantity), field("Unit", f.unit), field("Building type", f.building), field("Note", f.evidence)),
      actions: [{ label: "Cancel" }, { label: "Add", kind: "primary", validate: () => (f.type.value.trim() ? null : "An item needs a type.") }]
    });
    if (result !== "Add") return;
    const view = await ensureView(sheet);
    if (!view) return;
    const box = geo.boxAround(point, 24, st.viewer.pageW, st.viewer.pageH);
    const created = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/entities`, {
      type: f.type.value.trim(), size: f.size.value.trim() || null, tag: f.tag.value.trim() || null, quantity: Number(f.quantity.value), unit: f.unit.value,
      buildingTypeId: f.building.value === "" ? null : Number(f.building.value), viewId: view.viewId, box: [box.x0, box.y0, box.x1, box.y1], evidence: f.evidence.value.trim() || null
    }), { ok: "Added." });
    if (!created) return;
    setEntities([...st.entities, created]);
    setTool("select");
    selectItem(created.publicId);
  };

  const calibrate = async (sheet, points) => {
    const distance = geo.distance(points[0], points[1]);
    const length = h("input", { class: "form-control", type: "number", min: "0", step: "any", placeholder: "feet" });
    const views = sheet.views;
    const viewSel = select(views.length ? views.map((v) => [v.viewId, v.title]) : [["", "Whole page (a view will be created)"]], st.calibrateViewId || views[0]?.viewId || "");
    const result = await dialog({
      title: "Calibrate the scale",
      body: h("div", {}, h("p", {}, `You marked two points ${(distance / 72).toFixed(2)} inches apart on the page. How long is that in real life?`), field("Real length in feet", length), field("Applies to view", viewSel)),
      actions: [{ label: "Cancel" }, { label: "Set scale", kind: "primary", validate: () => (Number(length.value) > 0 ? null : "Enter the real length in feet.") }]
    });
    setTool("select");
    if (result !== "Set scale") return;
    const ratio = geo.ratioFromCalibration(points[0], points[1], Number(length.value));
    if (!ratio) { toast("Those two points are the same spot.", "error"); return; }
    const view = viewSel.value ? { viewId: viewSel.value } : await ensureView(sheet);
    if (!view) return;
    const done = await ctx.edit(() => ctx.api.put(`/jobs/${ctx.jobId}/views/${view.viewId}/scale`, {
      ratio, method: "Calibrated", calibration: { pointsPts: points, knownLengthFeet: Number(length.value), distancePts: distance }
    }), { ok: `Scale set to ${geo.scaleLabel(ratio)}.` });
    if (done) { ctx.invalidate("sheets"); await refreshSheets(); }
  };

  const addRegion = async (sheet, polygon) => {
    const kind = select([["Exclude", "Leave this area out of the count"], ["Include", "Count only this area"]], "Exclude");
    const reason = h("input", { class: "form-control", maxlength: 300, placeholder: "For example: legend, not part of the plan" });
    const result = await dialog({
      title: "Mark an area", body: h("div", {}, field("What to do", kind), field("Reason", reason, "Required when leaving an area out.")),
      actions: [{ label: "Cancel" }, { label: "Save", kind: "primary", validate: () => (kind.value === "Exclude" && !reason.value.trim() ? "Say why this area is left out." : null) }]
    });
    setTool("select");
    if (result !== "Save") return;
    const done = await ctx.edit(() => ctx.api.post(`/jobs/${ctx.jobId}/sheets/${sheet.sheetId}/regions`, { kind: kind.value, reason: reason.value.trim() || null, polygon }), { ok: "Area marked." });
    if (done) await refreshSheets();
  };

  const refreshSheets = async () => {
    const fresh = await ctx.sheets(true);
    st.allSheets = fresh;
    st.sheets = fresh.filter((s) => s.inScope).sort((a, b) => (a.fileId === b.fileId ? a.pageNo - b.pageNo : String(a.fileId).localeCompare(b.fileId)));
    paintTop();
    paintViewerItems();
    paintPanel();
  };

  const setTool = (tool) => { st.tool = tool; st.viewer.setTool(tool); paintTop(); };

  // ---- painting

  function paintAll() {
    paintTop();
    paintFilters();
    paintViewerItems();
    paintTree();
    paintPanel();
  }

  function paintTop() {
    clear(els.top);
    const sheet = currentSheet();
    const idx = st.sheets.findIndex((s) => s.sheetId === st.sheetId);
    const count = (s) => model.entitiesForSheet(st.entities, s).filter((e) => e.reviewStatus !== "Rejected").length;
    const todo = (s) => model.entitiesForSheet(st.entities, s).filter((e) => e.reviewStatus === "Proposed").length;
    const sheetSel = select(st.sheets.map((s) => [s.sheetId, `${s.sheetNumber || `Page ${s.pageNo}`}${s.title ? " – " + s.title : ""}  (${count(s)} items, ${todo(s)} to review)`]), st.sheetId, { "aria-label": "Sheet" });
    sheetSel.addEventListener("change", () => showSheet(sheetSel.value, { keepSelection: false }));
    const tool = (id, text, title) => h("button", { class: `btn btn-sm ${st.tool === id ? "btn-primary" : "btn-outline-secondary"}`, type: "button", title, disabled: id !== "select" && !ctx.canChange, onclick: () => setTool(st.tool === id ? "select" : id) }, text);
    els.top.append(
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: idx <= 0, title: "Previous sheet", onclick: () => showSheet(st.sheets[idx - 1].sheetId, { keepSelection: false }) }, "‹"),
      sheetSel,
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: idx < 0 || idx >= st.sheets.length - 1, title: "Next sheet", onclick: () => showSheet(st.sheets[idx + 1].sheetId, { keepSelection: false }) }, "›"),
      h("span", { class: "dw-sep" }),
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", title: "Fit page (F)", onclick: () => st.viewer.fit() }, "Fit"),
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: () => st.viewer.zoomBy(1.25), "aria-label": "Zoom in" }, "+"),
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: () => st.viewer.zoomBy(0.8), "aria-label": "Zoom out" }, "−"),
      h("span", { class: "dw-sep" }),
      tool("add", "Add item", "Click on the page to add an item the detector missed"),
      tool("calibrate", "Calibrate", "Click two points a known distance apart"),
      tool("region", "Mark area", "Drag a rectangle to leave an area out of the count"),
      h("span", { class: "dw-sep" }),
      h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !ctx.canChange, title: "Undo my last change (U)", onclick: undoMine }, "Undo"),
      sheet ? h("span", { class: "small text-muted ms-2" }, `${sheet.views.length ? sheet.views.map((v) => `${v.title}: ${geo.scaleLabel(v.scaleRatio)}`).join(" · ") : "No views set up"}`) : null);
    if (st.tool !== "select") els.top.append(h("span", { class: "dw-toolhint" }, { add: "Click where the item is.", calibrate: "Click two points a known distance apart.", region: "Drag a rectangle." }[st.tool]));
  }

  function paintToolbarCount() {
    const b = els.filters.querySelector("[data-bulk-count]");
    if (b) b.textContent = String(st.selection.size);
  }

  function paintFilters() {
    clear(els.filters);
    const c = model.counts(st.entities);
    const statusChip = (name, text, n) => {
      const on = st.filter.statuses.has(name);
      return h("button", { class: `dw-chip${on ? " dw-chip-on" : ""} dw-chip-${name.toLowerCase()}`, type: "button", "aria-pressed": on, onclick: () => { on ? st.filter.statuses.delete(name) : st.filter.statuses.add(name); paintAll(); } }, `${text} ${n}`);
    };
    const search = h("input", { class: "form-control form-control-sm", placeholder: "Search items…", value: st.filter.text, "aria-label": "Search items" });
    search.addEventListener("input", debounce(() => { st.filter.text = search.value; paintViewerItems(); paintTree(); }, 150));
    const conf = h("input", { type: "range", min: "0", max: "100", value: Math.round((st.filter.minConfidence ?? 0) * 100), class: "form-range", "aria-label": "Minimum confidence" });
    const confLabel = h("span", { class: "small text-muted" }, st.filter.minConfidence ? `≥ ${pct(st.filter.minConfidence)}` : "any");
    conf.addEventListener("input", () => { st.filter.minConfidence = Number(conf.value) / 100 || null; confLabel.textContent = st.filter.minConfidence ? `≥ ${pct(st.filter.minConfidence)}` : "any"; });
    conf.addEventListener("change", () => { paintViewerItems(); paintTree(); });

    const verifiable = model.bulkVerifiable(visibleEntities(), st.filter.minConfidence ?? 0.95);
    els.filters.append(
      h("div", { class: "dw-chips" }, statusChip("Proposed", "To review", c.Proposed), statusChip("Verified", "Verified", c.Verified), statusChip("Manual", "Added", c.Manual), statusChip("Rejected", "Rejected", c.Rejected),
        h("button", {
          class: `dw-chip${st.filter.dispositions.has("TBD") ? " dw-chip-on" : ""}`, type: "button", "aria-pressed": st.filter.dispositions.has("TBD"),
          onclick: () => { st.filter.dispositions.has("TBD") ? st.filter.dispositions.delete("TBD") : st.filter.dispositions.add("TBD"); paintAll(); }
        }, `Undecided ${c.tbd}`)),
      search,
      h("div", { class: "d-flex align-items-center gap-2" }, h("span", { class: "small" }, "Confidence"), conf, confLabel),
      h("label", { class: "form-check small" }, h("input", { class: "form-check-input", type: "checkbox", checked: st.filter.thisSheetOnly, onchange: (e) => { st.filter.thisSheetOnly = e.target.checked; paintTree(); } }), " This sheet only"),
      h("label", { class: "form-check small" }, h("input", { class: "form-check-input", type: "checkbox", checked: st.advance, onchange: (e) => { st.advance = e.target.checked; } }), " Move to the next item after verifying or rejecting"),
      h("div", { class: "dw-bulkbar" },
        h("button", { class: "btn btn-success btn-sm", type: "button", disabled: !ctx.canChange || !st.selection.size, onclick: () => bulk("Verified", [...st.selection]) }, "Verify chosen (", h("span", { dataset: { bulkCount: "" } }, String(st.selection.size)), ")"),
        h("button", { class: "btn btn-outline-danger btn-sm", type: "button", disabled: !ctx.canChange || !st.selection.size, onclick: () => bulk("Rejected", [...st.selection]) }, "Reject chosen"),
        h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !st.selection.size, onclick: () => { st.selection.clear(); paintTree(); paintFilters(); } }, "Clear")),
      h("div", { class: "dw-bulkbar" },
        h("button", {
          class: "btn btn-outline-success btn-sm", type: "button", disabled: !ctx.canChange || !verifiable.length,
          title: "Verify every visible proposal at or above this confidence that is not undecided",
          onclick: async () => { if (await confirmDialog(`Verify ${verifiable.length} proposal(s) at ${pct(st.filter.minConfidence ?? 0.95)} confidence or higher? You can undo them one by one.`, { ok: "Verify all", kind: "success" })) bulk("Verified", verifiable.map((e) => e.publicId)); }
        }, `Verify all ≥ ${pct(st.filter.minConfidence ?? 0.95)} (${verifiable.length})`)));
  }

  function paintTree() {
    clear(els.tree);
    const list = visibleEntities();
    const tree = model.buildTree(list, st.types);
    if (!tree.length) { els.tree.append(h("p", { class: "text-muted small p-2" }, "Nothing matches the filters.")); return; }

    for (const bt of tree) {
      const open = st.expanded.has(bt.key);
      els.tree.append(h("div", { class: "dw-node dw-node-bt" },
        h("button", { class: "dw-twisty", type: "button", "aria-expanded": open, onclick: () => { open ? st.expanded.delete(bt.key) : st.expanded.add(bt.key); paintTree(); } }, open ? "▾" : "▸"),
        h("strong", {}, bt.label), h("span", { class: "dw-count" }, `${bt.count} · ${fmtQty(bt.quantity)}`), bt.proposed ? badge(`${bt.proposed} to review`, "warning") : null));
      if (!open) continue;
      for (const g of bt.groups) {
        const gopen = st.expanded.has(g.key);
        const allChosen = g.items.every((e) => st.selection.has(e.publicId));
        els.tree.append(h("div", { class: "dw-node dw-node-g" },
          h("input", { type: "checkbox", class: "form-check-input", checked: allChosen && g.items.length > 0, "aria-label": `Choose all ${g.label}`, onchange: (ev) => { for (const e of g.items) ev.target.checked ? st.selection.add(e.publicId) : st.selection.delete(e.publicId); paintTree(); paintFilters(); } }),
          h("button", { class: "dw-twisty", type: "button", "aria-expanded": gopen, onclick: () => { gopen ? st.expanded.delete(g.key) : st.expanded.add(g.key); paintTree(); } }, gopen ? "▾" : "▸"),
          h("span", {}, g.label), h("span", { class: "dw-count" }, `${g.count}`), g.proposed ? h("span", { class: "dw-dot dw-dot-proposed", title: `${g.proposed} to review` }) : null));
        if (!gopen) continue;
        for (const e of g.items.slice(0, 400)) {
          els.tree.append(h("div", { class: `dw-node dw-node-i${e.publicId === st.selectedId ? " dw-selected" : ""}`, role: "button", tabindex: 0, dataset: { id: e.publicId },
            onclick: () => selectItem(e.publicId, { focus: true }), onkeydown: (ev) => { if (ev.key === "Enter") selectItem(e.publicId, { focus: true }); } },
          h("input", { type: "checkbox", class: "form-check-input", checked: st.selection.has(e.publicId), "aria-label": "Choose item", onclick: (ev) => ev.stopPropagation(), onchange: (ev) => { ev.target.checked ? st.selection.add(e.publicId) : st.selection.delete(e.publicId); paintToolbarCount(); } }),
          h("span", { class: `dw-dot dw-dot-${model.statusKey(e)}` }), h("span", { class: "dw-itemtext" }, `${fmtQty(e.quantity)} × ${e.sheetViewRef || "?"}`), h("span", { class: "dw-count" }, pct(e.confidence)),
          e.disposition !== "Included" ? badge(titleCase(e.disposition), "light") : null));
        }

        if (g.items.length > 400) els.tree.append(h("div", { class: "text-muted small ps-4" }, `…and ${g.items.length - 400} more. Narrow the filters to see them.`));
      }
    }

    els.tree.querySelector(".dw-selected")?.scrollIntoView({ block: "nearest" });
  }

  function paintPanel() {
    clear(els.panel);
    const e = selected();
    if (!e) { els.panel.append(sheetPanel()); return; }
    const can = ctx.canChange;
    const sheet = model.sheetOf(e, st.sheets);
    const typeName = st.types.find((t) => t.buildingTypeId === e.buildingTypeId)?.name;

    const confidence = h("div", { class: "dw-confbar", title: `Detector confidence ${pct(e.confidence)}` }, h("div", { style: { width: pct(e.confidence) } }));
    const history = h("div", { class: "dw-history" });
    const loadHistory = async () => {
      const list = await ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/entities/${e.publicId}/history`));
      clear(history);
      if (!list) return;
      history.append(list.length ? list.slice().reverse().map((r) => h("div", { class: "dw-hist" }, h("strong", {}, r.action), ` by ${r.changedBy}, ${fmtDate(r.changedUtc)}`, r.reason ? h("div", { class: "text-muted" }, r.reason) : null)) : h("div", { class: "text-muted small" }, "No changes yet."));
    };

    els.panel.append(
      h("div", { class: "dw-card dw-card-tight" },
        h("div", { class: "d-flex justify-content-between align-items-start" }, h("h2", { class: "mb-1" }, model.itemLabel(e)), badge(e.reviewStatus, { Verified: "success", Rejected: "secondary", Manual: "primary" }[e.reviewStatus] || "warning")),
        h("div", { class: "text-muted small" }, `${fmtQty(e.quantity)} ${e.unit} · ${typeName || "no building type"} · ${sheet?.sheetNumber || e.sheetViewRef || "no sheet"}`),
        confidence,
        h("dl", { class: "dw-facts" },
          h("dt", {}, "Found by"), h("dd", {}, `${e.method}${e.family ? ` (${titleCase(e.family)})` : ""}${e.resolutionSource !== "Detector" ? `, then ${titleCase(e.resolutionSource)}${e.resolvedByRuleId ? " (rule)" : ""}` : ""}`),
          h("dt", {}, "Decision"), h("dd", {}, `${titleCase(e.disposition)}${e.dispositionReason ? `: ${e.dispositionReason}` : ""}`),
          e.evidence ? [h("dt", {}, "Evidence"), h("dd", {}, e.evidence)] : null,
          e.isStale ? [h("dt", {}, "Note"), h("dd", { class: "text-danger" }, "Something it depends on changed, so check it again.")] : null),
        h("div", { class: "d-flex gap-2 flex-wrap mt-2" },
          e.reviewStatus !== "Verified" && e.reviewStatus !== "Manual" ? h("button", { class: "btn btn-success btn-sm", type: "button", disabled: !can || e.resolutionSource === "UserAdded", onclick: () => review(e, "Verified") }, "Verify (V)") : null,
          e.reviewStatus !== "Rejected" ? h("button", {
            class: "btn btn-outline-danger btn-sm", type: "button", disabled: !can,
            onclick: async () => { const reason = await promptDialog("Reject this item? It will not be counted. Say why if you can.", { title: "Reject item", required: false, placeholder: "Optional reason", ok: "Reject" }); if (reason !== null) review(e, "Rejected", reason || null); }
          }, "Reject (X)") : null,
          e.reviewStatus === "Verified" || e.reviewStatus === "Rejected" ? h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !can, onclick: () => review(e, "Proposed") }, "Reopen") : null),
        h("div", { class: "d-flex gap-2 flex-wrap mt-2" },
          h("button", { class: "btn btn-outline-primary btn-sm", type: "button", disabled: !can, onclick: () => editItem(e) }, "Correct…"),
          h("button", { class: "btn btn-outline-primary btn-sm", type: "button", disabled: !can, onclick: () => setDisposition(e) }, "Decision…"),
          h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", disabled: !can || e.revision <= 1, title: "Take back the last change to this item (U)", onclick: () => undo(e) }, "Undo")),
        h("div", { class: "d-flex gap-2 mt-3" },
          h("button", { class: "btn btn-link btn-sm p-0", type: "button", onclick: () => selectItem(model.stepItem(queue(), e.publicId, -1)?.publicId, { focus: true }) }, "‹ Previous (K)"),
          h("button", { class: "btn btn-link btn-sm p-0", type: "button", onclick: () => selectItem(model.stepItem(queue(), e.publicId, 1)?.publicId, { focus: true }) }, "Next (J) ›"),
          h("button", { class: "btn btn-link btn-sm p-0 ms-auto", type: "button", onclick: () => selectItem(null) }, "Close"))),
      h("details", { class: "dw-card dw-card-tight", ontoggle: (ev) => { if (ev.target.open) loadHistory(); } }, h("summary", {}, `History (revision ${e.revision})`), history));
  }

  function sheetPanel() {
    const sheet = currentSheet();
    const wrap = h("div", { class: "dw-card dw-card-tight" });
    if (!sheet) return wrap;
    const items = model.entitiesForSheet(st.entities, sheet);
    const c = model.counts(items);
    wrap.append(h("h2", {}, sheet.sheetNumber || `Page ${sheet.pageNo}`), h("div", { class: "text-muted small" }, sheet.title || ""),
      h("p", { class: "mt-2 mb-1" }, `${c.total} items: ${c.Proposed} to review, ${c.Verified} verified, ${c.Manual} added, ${c.Rejected} rejected.`),
      !sheet.hasTextLayer ? h("p", { class: "text-warning small" }, "This page has no live text, so a detector cannot read its labels directly.") : null,
      h("h3", { class: "dw-h3" }, "Views"),
      sheet.views.length ? sheet.views.map((v) => h("div", { class: "small" }, `${v.title}: scale ${geo.scaleLabel(v.scaleRatio)}`)) : h("div", { class: "small text-muted" }, "None. Add views on the Setup tab."),
      h("h3", { class: "dw-h3" }, "Marked areas"),
      sheet.regions.length ? sheet.regions.map((r) => h("div", { class: "d-flex align-items-center gap-2 small" }, badge(r.kind, r.kind === "Exclude" ? "danger" : "success"), r.reason || "",
        h("button", {
          class: "btn btn-link btn-sm text-danger p-0 ms-auto", type: "button", disabled: !ctx.canChange,
          onclick: async () => { if (await ctx.edit(() => ctx.api.del(`/jobs/${ctx.jobId}/regions/${r.regionId}`), { ok: "Removed." })) await refreshSheets(); }
        }, "Remove"))) : h("div", { class: "small text-muted" }, "None."),
      h("div", { class: "small text-muted mt-3" }, "Keys: J next, K previous, V verify, X reject, U undo, F fit, Esc stop a tool."));
    return wrap;
  }

  // ---- keyboard

  const onKey = (ev) => {
    if (ev.ctrlKey || ev.metaKey || ev.altKey) return;
    const tag = (ev.target?.tagName || "").toLowerCase();
    if (["input", "textarea", "select"].includes(tag) || document.querySelector(".dw-overlay")) return;
    const e = selected();
    switch (ev.key.toLowerCase()) {
      case "j": step(1); break;
      case "k": step(-1); break;
      case "f": st.viewer.fit(); break;
      case "escape": if (st.tool !== "select") setTool("select"); else selectItem(null); break;
      case "v": if (e && ctx.canChange && e.resolutionSource !== "UserAdded" && e.reviewStatus !== "Verified") review(e, "Verified"); break;
      case "x": if (e && ctx.canChange && e.reviewStatus !== "Rejected") review(e, "Rejected"); break;
      case "u": if (e && ctx.canChange) undo(e); else if (ctx.canChange) undoMine(); break;
      default: return;
    }

    ev.preventDefault();
  };
  document.addEventListener("keydown", onKey);

  const offInvalid = ctx.on("invalidated", async (names) => {
    if (names.includes("entities")) { setEntities(await ctx.entities(true)); paintAll(); }
  });

  paintAll();
  await showSheet(st.sheetId);
  if (st.calibrateViewId && ctx.canChange) setTool("calibrate");
  const wanted = ctx.nav?.focusEntity;
  if (wanted) {
    ctx.nav = null;
    const entity = st.entities.find((e) => e.publicId === wanted);
    if (entity) await selectItem(entity.publicId, { focus: true });
  }

  return {
    dispose() { document.removeEventListener("keydown", onKey); offInvalid(); st.viewer.dispose(); },
    refresh: async () => { setEntities(await ctx.entities(true)); paintAll(); }
  };
}
