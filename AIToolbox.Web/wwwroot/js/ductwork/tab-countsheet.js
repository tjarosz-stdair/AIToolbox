// The count sheet: what was found, by building type, multiplied out, and the job totals. It is computed by the server and
// never typed in, so what is shown here, in the CSV and on paper cannot disagree. A quantity that cannot be known yet is
// shown as "to be determined", never as zero, and the page says in plain words what is holding it up.

import { h, clear, badge, titleCase, fmtQty, toCsv, download, toast } from "./util.js";
import { countSheetBlockers } from "./model.js";

const STATE_TEXT = {
  Known: null,
  MultiplierUnconfirmed: "Waiting for a confirmed multiplier",
  ItemUndetermined: "Item not decided yet",
  NotCounted: "Not counted"
};

export async function render(container, ctx) {
  const paint = async () => {
    const sheet = await ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/count-sheet`));
    clear(container);
    if (!sheet) { container.append(h("div", { class: "alert alert-danger" }, "The count sheet could not be built.")); return; }
    container.append(view(ctx, sheet, paint));
  };

  await paint();
  return { refresh: paint };
}

function view(ctx, sheet, repaint) {
  const blockers = countSheetBlockers(sheet);
  const status = sheet.isFinal
    ? h("div", { class: "alert alert-success" }, h("strong", {}, "Final. "), "Every count is verified, every multiplier is confirmed and every question is answered.")
    : h("div", { class: "alert alert-warning" }, h("strong", {}, "Not final yet. "), blockers.length ? h("ul", { class: "mb-0" }, blockers.map((b) => h("li", {}, b))) : "Something is still open.");

  return h("div", {},
    h("div", { class: "d-flex justify-content-between align-items-center flex-wrap gap-2" },
      h("h2", { class: "mb-0" }, "Count sheet"),
      h("div", { class: "d-flex gap-2 dw-noprint" },
        h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: repaint }, "Refresh"),
        h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: () => { download(`count-sheet-${ctx.jobId}.csv`, csv(ctx, sheet)); toast("Downloaded.", "success", 2000); } }, "Download CSV"),
        h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: () => window.print() }, "Print"))),
    h("p", { class: "text-muted small" }, "Extended quantity is the count in one building times how many buildings there are. Items are only added together when they are the same item in the same unit."),
    status,
    sheet.openFindings?.length ? openFindings(ctx, sheet) : null,
    sheet.blocks.map((b) => block(b)),
    totals(sheet),
    sheet.openItems?.length ? openItems(sheet) : null);
}

function block(b) {
  const mult = b.multiplierConfirmed ? badge(`× ${b.multiplier} confirmed`, "success") : b.multiplier != null ? badge(`× ${b.multiplier} suggested, not confirmed`, "warning") : badge("multiplier not set", "danger");
  return h("div", { class: "dw-card dw-countblock" },
    h("div", { class: "d-flex justify-content-between align-items-center" }, h("h3", {}, b.name), h("div", {}, b.isUnassigned ? badge("needs a building type", "warning") : mult)),
    b.multiplierEvidence && b.multiplierConfirmed ? h("div", { class: "small text-muted mb-1" }, `${titleCase(b.multiplierSource)}: ${b.multiplierEvidence}`) : null,
    b.lines.length ? h("table", { class: "table table-sm dw-counttable" },
      h("thead", {}, h("tr", {}, ["Item", "Size", "Tag", "Per building", "Multiplier", "Extended", ""].map((t, i) => h("th", { class: i >= 3 && i < 6 ? "text-end" : "" }, t)))),
      h("tbody", {}, b.lines.map((l) => line(l)))) : h("p", { class: "text-muted small" }, "Nothing counted for this building type yet."));
}

function line(l) {
  const note = STATE_TEXT[l.state];
  const counted = l.state === "Known";
  return h("tr", { class: l.state === "NotCounted" ? "text-muted" : "" },
    h("td", {}, l.type, l.category ? h("span", { class: "text-muted small ms-1" }, l.category) : null, l.manufacturer || l.model ? h("div", { class: "small text-muted" }, [l.manufacturer, l.model].filter(Boolean).join(" ")) : null),
    h("td", {}, l.size || ""), h("td", {}, l.tag || ""),
    h("td", { class: "text-end" }, `${fmtQty(l.quantityPerBuilding)} ${l.unit}`),
    h("td", { class: "text-end" }, l.multiplier != null ? `× ${l.multiplier}` : ""),
    h("td", { class: "text-end" }, counted ? h("strong", {}, `${fmtQty(l.extendedQuantity)} ${l.unit}`) : h("span", { class: "text-muted" }, "TBD")),
    h("td", {}, [note ? badge(note, l.state === "NotCounted" ? "secondary" : "warning") : null, l.proposedCount ? badge(`${l.proposedCount} unverified`, "warning") : null, l.staleCount ? badge("recheck", "danger") : null,
      l.disposition !== "Included" ? badge(titleCase(l.disposition), "light") : null]));
}

function totals(sheet) {
  if (!sheet.totals.length) return h("div", { class: "dw-card" }, h("h3", {}, "Job totals"), h("p", { class: "text-muted" }, "No totals yet."));
  return h("div", { class: "dw-card" },
    h("h3", {}, "Job totals"),
    h("table", { class: "table table-sm dw-counttable" },
      h("thead", {}, h("tr", {}, ["Item", "Size", "Tag", "Total", "Holding it up"].map((t, i) => h("th", { class: i === 3 ? "text-end" : "" }, t)))),
      h("tbody", {}, sheet.totals.map((t) => h("tr", {},
        h("td", {}, t.type), h("td", {}, t.size || ""), h("td", {}, t.tag || ""),
        h("td", { class: "text-end" }, t.isComplete ? h("strong", {}, `${fmtQty(t.total)} ${t.unit}`) : h("span", {}, h("span", { class: "text-muted" }, `TBD (at least ${fmtQty(t.knownPart)} ${t.unit})`))),
        h("td", { class: "small" }, t.pendingBuildingTypes?.length ? `Multiplier for ${t.pendingBuildingTypes.join(", ")}` : t.proposedCount ? `${t.proposedCount} unverified` : ""))))));
}

function openItems(sheet) {
  return h("div", { class: "dw-card" },
    h("h3", {}, "Still to be determined"),
    h("p", { class: "text-muted small" }, "These are listed but never added into a total until they are decided."),
    h("table", { class: "table table-sm" }, h("tbody", {}, sheet.openItems.map((o) => h("tr", {}, h("td", {}, o.buildingType), h("td", {}, [o.type, o.size, o.tag].filter(Boolean).join(" ")), h("td", {}, `${fmtQty(o.quantity)} ${o.unit}`), h("td", { class: "text-muted small" }, o.reason))))));
}

function openFindings(ctx, sheet) {
  return h("div", { class: "dw-card" },
    h("h3", {}, "Unanswered questions"),
    h("ul", {}, sheet.openFindings.map((f) => h("li", {}, f.item ? h("strong", {}, `${f.item}: `) : null, f.question))),
    h("a", { class: "btn btn-outline-primary btn-sm dw-noprint", href: "#/findings" }, "Answer them"));
}

function csv(ctx, sheet) {
  const rows = [["Building type", "Multiplier", "Multiplier confirmed", "Item", "Size", "Tag", "Unit", "Per building", "Extended", "Status", "Decision"]];
  for (const b of sheet.blocks) for (const l of b.lines) {
    rows.push([b.name, b.multiplier ?? "", b.multiplierConfirmed ? "yes" : "no", l.type, l.size ?? "", l.tag ?? "", l.unit, l.quantityPerBuilding, l.state === "Known" ? l.extendedQuantity : "TBD", titleCase(l.state), titleCase(l.disposition)]);
  }

  rows.push([]);
  rows.push(["Job totals"]);
  rows.push(["Item", "Size", "Tag", "Unit", "Total", "Complete"]);
  for (const t of sheet.totals) rows.push([t.type, t.size ?? "", t.tag ?? "", t.unit, t.isComplete ? t.total : "TBD", t.isComplete ? "yes" : "no"]);
  rows.push([]);
  rows.push([sheet.isFinal ? "FINAL" : "NOT FINAL: " + countSheetBlockers(sheet).join(" ")]);
  return toCsv(rows);
}
