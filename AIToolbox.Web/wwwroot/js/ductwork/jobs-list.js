// The list of jobs the signed-in person is on, and the way to start a new one.

import { createApi, ApiError, qs } from "./api.js";
import { antiforgeryFrom, readBoot } from "./boot.js";
import { h, clear, field, select, badge, titleCase, fmtDate, dialog, toast, debounce } from "./util.js";

const STATES = [["active", "Open jobs"], ["Complete", "Complete"], ["Closed", "Closed"], ["Archived", "Archived"], ["all", "All"]];

export async function startJobsList(root, boot = readBoot(), api = createApi({ base: boot.api, antiforgery: antiforgeryFrom() })) {
  const state = { filter: "active", text: "", tag: "", jobs: [] };
  const table = h("div", {});

  const load = async () => {
    const states = state.filter === "active" ? ["Draft", "InReview", "Completing"] : state.filter === "all" ? null : [state.filter];
    try {
      state.jobs = await api.get(`/jobs${qs({ state: states, tag: state.tag ? [state.tag] : null })}`);
    } catch (e) {
      clear(table);
      table.append(h("div", { class: "alert alert-danger" }, e instanceof ApiError ? e.message : "The jobs could not be loaded."));
      return;
    }

    paint();
  };

  const paint = () => {
    clear(table);
    const needle = state.text.trim().toLowerCase();
    const jobs = state.jobs.filter((j) => !needle || [j.name, j.customerName, j.projectName, j.siteAddress, j.ownerEmail].some((v) => (v || "").toLowerCase().includes(needle)));
    if (!jobs.length) {
      table.append(h("div", { class: "dw-card" }, h("p", { class: "text-muted mb-0" }, state.jobs.length ? "No job matches your search." : "You are not on any jobs here yet. Start one with “New job”.")));
      return;
    }

    table.append(h("div", { class: "dw-card" }, h("table", { class: "table table-hover align-middle mb-0" },
      h("thead", {}, h("tr", {}, ["Job", "Customer / project", "State", "Owner", "Updated"].map((t) => h("th", {}, t)))),
      h("tbody", {}, jobs.map((j) => h("tr", { class: "dw-jobrow", onclick: () => { location.href = `${boot.base}/Jobs/${j.jobId}`; } },
        h("td", {}, h("a", { href: `${boot.base}/Jobs/${j.jobId}`, class: "fw-semibold", onclick: (e) => e.stopPropagation() }, j.name)),
        h("td", {}, [j.customerName, j.projectName].filter(Boolean).join(" / ")),
        h("td", {}, badge(titleCase(j.state), j.state === "Complete" ? "success" : j.state === "Closed" || j.state === "Archived" ? "secondary" : "primary")),
        h("td", { class: "small" }, j.ownerEmail), h("td", { class: "small text-muted" }, fmtDate(j.updatedUtc))))))));
  };

  const filter = select(STATES, state.filter, { "aria-label": "Which jobs" });
  filter.addEventListener("change", () => { state.filter = filter.value; load(); });
  const search = h("input", { class: "form-control form-control-sm", placeholder: "Search jobs…", "aria-label": "Search jobs" });
  search.addEventListener("input", debounce(() => { state.text = search.value; paint(); }, 150));
  const tag = h("input", { class: "form-control form-control-sm", placeholder: "Tag", "aria-label": "Tag" });
  tag.addEventListener("keydown", (e) => { if (e.key === "Enter") { state.tag = tag.value.trim(); load(); } });

  clear(root);
  root.append(
    h("div", { class: "page-heading" }, h("div", {}, h("h1", {}, "Ductwork Takeoff"), h("p", { class: "text-muted mb-0" }, "Count ductwork items from mechanical drawings, check what was found, and produce a count sheet.")),
      h("div", { class: "d-flex gap-2" }, boot.isAdmin || boot.isJobManager ? h("a", { class: "btn btn-outline-secondary btn-sm", href: `${boot.base}/Archives` }, "Archives") : null,
        boot.isAdmin ? h("a", { class: "btn btn-outline-secondary btn-sm", href: `${boot.base}/Admin` }, "Settings") : null,
        h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: () => newJobDialog(api, boot) }, "New job"))),
    h("div", { class: "d-flex gap-2 mb-3 flex-wrap" }, filter, search, tag), table);
  await load();
}

async function newJobDialog(api, boot) {
  const f = {
    name: h("input", { class: "form-control", maxlength: 200, placeholder: "For example Whiteland Apartments" }), customer: h("input", { class: "form-control", maxlength: 200 }),
    project: h("input", { class: "form-control", maxlength: 200 }), address: h("input", { class: "form-control", maxlength: 400 }),
    level: select([["Sensitive", "Sensitive: ask me about every proposal (recommended while the tool is new)"], ["Normal", "Normal"], ["Insensitive", "Insensitive"]], "Sensitive")
  };
  const result = await dialog({
    title: "New job", body: h("div", {}, field("Name", f.name), field("Customer", f.customer), field("Project", f.project), field("Site address", f.address), field("How much should the tool ask you?", f.level)),
    actions: [{ label: "Cancel" }, { label: "Create", kind: "primary", validate: () => (f.name.value.trim() ? null : "Give the job a name.") }]
  });
  if (result !== "Create") return;
  try {
    const job = await api.post("/jobs", { name: f.name.value.trim(), customerName: f.customer.value.trim() || null, projectName: f.project.value.trim() || null, siteAddress: f.address.value.trim() || null, reviewLevel: f.level.value });
    location.href = `${boot.base}/Jobs/${job.jobId}`;
  } catch (e) {
    toast(e.message, "error");
  }
}

if (typeof document !== "undefined" && document.getElementById("dw-app")?.dataset.page === "jobs") {
  startJobsList(document.getElementById("dw-app"));
}
