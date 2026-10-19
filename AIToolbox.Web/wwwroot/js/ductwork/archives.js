// Archives, across every job: for administrators and Job Managers. It shows each job's name, owner and state and what has been
// done to it, never the job's contents. A Job Manager can archive and restore any job from here.

import { createApi } from "./api.js";
import { antiforgeryFrom, readBoot } from "./boot.js";
import { h, clear, badge, titleCase, fmtDate, promptDialog, toast } from "./util.js";
import { archiveBadge, archiveFacts, isActive, operationLine, progressBar, requestArchive, requestRestore } from "./archive-ui.js";

export async function startArchives(root, boot = readBoot(), api = createApi({ base: boot.api, antiforgery: antiforgeryFrom() })) {
  let timer = null;
  const run = async (fn, ok) => {
    try { const r = await fn(); if (ok) toast(ok, "success", 3000); return r === undefined ? true : r; } catch (e) { toast(e.message || "That did not work.", "error"); return undefined; }
  };

  const paint = async () => {
    const data = await run(() => api.get("/archives"));
    clearTimeout(timer);
    clear(root);
    root.append(h("div", { class: "page-heading" },
      h("div", {}, h("h1", {}, "Archives"), h("p", { class: "text-muted mb-0" }, "Make a checked copy of a finished job, free the database space it uses, and bring it back when it is needed. Job contents are not shown here.")),
      h("a", { class: "btn btn-outline-secondary btn-sm", href: boot.base }, "← Jobs")));
    if (!data) { root.append(h("div", { class: "alert alert-danger" }, "The archives could not be loaded. Only administrators and Job Managers can see this page.")); return; }

    if (data.problems.length) {
      root.append(h("div", { class: "alert alert-danger" }, h("strong", {}, "Needs attention: "),
        h("ul", { class: "mb-0" }, data.problems.map((p) => h("li", {}, `${p.jobName}: ${p.problem}`)))));
    }

    if ((data.jobs || []).some((j) => isActive(j.operation))) timer = setTimeout(() => { if (root.isConnected) paint(); }, 3000);
    root.append(h("div", { class: "dw-card" }, data.jobs.length ? h("table", { class: "table table-sm align-middle" },
      h("thead", {}, h("tr", {}, ["Job", "State", "Archive", "What is happening", ""].map((t) => h("th", {}, t)))),
      h("tbody", {}, data.jobs.map((j) => row(j, data.admin)))) : h("p", { class: "text-muted mb-0" }, "No job is Complete, Closed or archived yet.")));
  };

  const row = (j, admin) => {
    const a = j.latestArchive;
    const op = j.operation;
    const job = { jobId: j.jobId, name: j.name, stateBeforeArchive: a?.stateAtArchive };
    const buttons = [];
    if ((j.state === "Complete" || j.state === "Closed") && !isActive(op)) buttons.push(h("button", { class: "btn btn-outline-secondary btn-sm", type: "button", onclick: async () => { if (await requestArchive(api, run, job)) paint(); } }, "Archive…"));
    if (j.state === "Archived" && !isActive(op)) buttons.push(h("button", { class: "btn btn-primary btn-sm", type: "button", onclick: async () => { if (await requestRestore(api, run, job)) paint(); } }, "Restore"));
    if (a && a.status !== "Writing" && a.status !== "Failed" && a.sizeBytes != null) buttons.push(h("button", {
      class: "btn btn-outline-secondary btn-sm", type: "button", title: "Read the archive back and check it",
      onclick: async () => { const r = await run(() => api.post(`/archives/${a.archiveId}/check`)); if (r && r !== true) { toast(r.ok ? "The archive is intact." : `Problem: ${r.problems[0]}`, r.ok ? "success" : "error", 6000); paint(); } }
    }, "Check"));
    if (op?.status === "Failed" && op.kind === "ArchivePurge" && j.state === "Archiving") buttons.push(h("button", {
      class: "btn btn-outline-primary btn-sm", type: "button", onclick: async () => { if (await run(() => api.post(`/archives/operations/${op.operationId}/retry`), "Retrying.")) paint(); }
    }, "Retry"));
    if (admin && a?.lastProblem) buttons.push(h("button", {
      class: "btn btn-outline-danger btn-sm", type: "button", title: "Point the archive at the file if it was moved",
      onclick: async () => {
        const key = await promptDialog(`Where is the archive now? Enter its location relative to the archive store (it is checked against the recorded hash). It was last at: ${a.storageKey}`, { title: "Relink archive", placeholder: "for example moved/ductwork-12-v1-20260101-ab12cd34.zip", ok: "Relink" });
        if (key && await run(() => api.post(`/archives/${a.archiveId}/relink`, { storageKey: key.trim() }), "Relinked.")) paint();
      }
    }, "Relink"));

    return h("tr", {},
      h("td", {}, h("strong", {}, j.name), h("div", { class: "small text-muted" }, [j.customerName, j.projectName].filter(Boolean).join(" / ") || j.ownerEmail)),
      h("td", {}, badge(titleCase(j.state), j.state === "Archived" ? "secondary" : j.state === "Archiving" || j.state === "Restoring" ? "warning" : "success")),
      h("td", { class: "small" }, a ? [archiveBadge(a), h("div", { class: "text-muted" }, archiveFacts(a))] : h("span", { class: "text-muted" }, "none")),
      h("td", { class: "small" }, op ? [operationLine(op), progressBar(op)] : ""),
      h("td", { class: "text-end text-nowrap" }, buttons));
  };

  await paint();
}

if (typeof document !== "undefined" && document.getElementById("dw-app")?.dataset.page === "archives") {
  startArchives(document.getElementById("dw-app"));
}
