// Audit: everything that happened to this job, who did it and when. The log is append-only and hash-chained, so it can be
// checked for tampering at any time. Reading it is open to everyone on the job.

import { h, clear, fmtDate, titleCase, toast } from "./util.js";
import { qs } from "./api.js";

const CATEGORIES = ["", "job", "members", "tags", "workflow", "lock", "processing", "setup", "drawings", "sheets", "review", "rules", "vision"];

export async function render(container, ctx) {
  const filter = { category: "", actor: "", text: "" };
  const table = h("div", {});
  const verify = h("div", {});

  const load = async () => {
    const events = await ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/audit${qs({ category: filter.category, actor: filter.actor, text: filter.text, limit: 500 })}`));
    clear(table);
    if (!events) return;
    const rows = [...events].reverse();
    table.append(rows.length ? h("table", { class: "table table-sm dw-audit" },
      h("thead", {}, h("tr", {}, ["#", "When", "Who", "What", ""].map((t) => h("th", {}, t)))),
      h("tbody", {}, rows.map((e) => h("tr", {},
        h("td", { class: "text-muted small" }, e.eventId), h("td", { class: "small text-nowrap" }, fmtDate(e.occurredUtc)),
        h("td", { class: "small" }, e.actorType === "User" ? (e.actorDisplayName || e.actorEmail) : titleCase(e.actorType) + (e.actorDisplayName ? ` (${e.actorDisplayName})` : "")),
        h("td", {}, e.summary, h("div", { class: "small text-muted" }, `${e.category} · ${e.action}${e.reason ? ` · ${e.reason}` : ""}`)),
        h("td", {}, e.beforeJson || e.afterJson ? h("details", {}, h("summary", { class: "small" }, "Details"), h("pre", { class: "dw-json" }, pretty(e.beforeJson, e.afterJson))) : null)))))
      : h("p", { class: "text-muted" }, "Nothing matches."));
    if (events.length >= 500) table.append(h("p", { class: "text-muted small" }, "Showing the 500 most recent matches. Narrow the filter to see others."));
  };

  const category = h("select", { class: "form-select form-select-sm", "aria-label": "Category" }, CATEGORIES.map((c) => h("option", { value: c }, c ? titleCase(c) : "All kinds of change")));
  const actor = h("input", { class: "form-control form-control-sm", placeholder: "Person (email)", "aria-label": "Person" });
  const text = h("input", { class: "form-control form-control-sm", placeholder: "Search words", "aria-label": "Search" });
  const apply = () => { filter.category = category.value; filter.actor = actor.value.trim(); filter.text = text.value.trim(); load(); };
  category.addEventListener("change", apply);
  for (const el of [actor, text]) el.addEventListener("keydown", (e) => { if (e.key === "Enter") apply(); });

  container.append(h("div", { class: "dw-card" },
    h("div", { class: "d-flex justify-content-between align-items-center flex-wrap gap-2" }, h("h2", { class: "mb-0" }, "Audit log"),
      h("button", {
        class: "btn btn-outline-secondary btn-sm", type: "button",
        onclick: async () => {
          const r = await ctx.run(() => ctx.api.get(`/jobs/${ctx.jobId}/audit/verify`));
          if (!r) return;
          clear(verify);
          verify.append(r.isValid ? h("div", { class: "alert alert-success mt-2" }, `The log is intact: ${r.eventCount} events, every one linked to the one before it.`)
            : h("div", { class: "alert alert-danger mt-2" }, `The log does not verify${r.firstBadEventId ? ` from event ${r.firstBadEventId}` : ""}: ${r.problem || "a link is broken."}`));
          toast(r.isValid ? "The audit log verifies." : "The audit log does not verify.", r.isValid ? "success" : "error");
        }
      }, "Verify the log")),
    verify,
    h("div", { class: "d-flex gap-2 my-2 flex-wrap" }, category, actor, text, h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: apply }, "Filter")),
    table));
  await load();
  return {};
}

function pretty(before, after) {
  const fmt = (j) => { try { return JSON.stringify(JSON.parse(j), null, 2); } catch { return j; } };
  return [before ? `Before:\n${fmt(before)}` : null, after ? `After:\n${fmt(after)}` : null].filter(Boolean).join("\n\n");
}

