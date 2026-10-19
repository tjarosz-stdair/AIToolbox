// What the job page and the Archives page share: asking for an archive or a restore, and saying plainly what an archive is doing.

import { h, badge, fmtBytes, fmtDate, dialog, confirmDialog, toast } from "./util.js";

const ACTIVE = new Set(["Queued", "Running"]);

export const isActive = (op) => !!op && ACTIVE.has(op.status);

const ARCHIVE_KIND = { Ready: "success", Purged: "primary", PurgePending: "warning", Writing: "warning", Failed: "danger", Superseded: "secondary" };
const ARCHIVE_TEXT = {
  Ready: "Archived (copy kept, data still in the database)", Purged: "Archived and removed from the database", PurgePending: "Removing from the database",
  Writing: "Being written", Failed: "Did not complete", Superseded: "Out of date (the job was reopened)"
};

export function archiveBadge(archive) {
  return badge(ARCHIVE_TEXT[archive.status] || archive.status, ARCHIVE_KIND[archive.status] || "light");
}

/** One line on what an operation is doing or did. */
export function operationLine(op) {
  if (!op) return "";
  const what = { ArchiveZip: "Archiving", ArchivePurge: "Archiving and removing from the database", Restore: "Restoring" }[op.kind] || op.kind;
  if (op.status === "Failed") return `${what} stopped: ${op.error || "it did not complete."}`;
  if (op.status === "Succeeded") return `${what} finished ${fmtDate(op.finishedUtc)}.`;
  const pct = op.progressTotal ? ` (${Math.min(100, Math.round((op.progressDone / op.progressTotal) * 100))}%)` : "";
  return `${what}: ${op.message || "waiting for the server to start"}${pct}`;
}

export function progressBar(op) {
  const pct = op?.progressTotal ? Math.min(100, Math.round((op.progressDone / op.progressTotal) * 100)) : null;
  return pct === null ? null : h("div", { class: "progress mt-1", style: { maxWidth: "320px" } }, h("div", { class: "progress-bar", style: { width: `${pct}%` } }, `${pct}%`));
}

/** What an archive holds, for the person deciding whether to rely on it. */
export function archiveFacts(a) {
  const parts = [`${fmtBytes(a.sizeBytes)}`, `${a.sourceFileCount ?? 0} drawing file(s)`, `version ${a.completionVersion}`, `made ${fmtDate(a.createdUtc)} by ${a.createdBy}`];
  if (a.purgedUtc) parts.push(`detail removed ${fmtDate(a.purgedUtc)}`);
  if (a.restoredUtc) parts.push(`restored ${fmtDate(a.restoredUtc)}`);
  return parts.join(" · ");
}

/**
 * Asks how to archive a job. A copy keeps the data where it is. Archive-and-remove frees the database but leaves the archive as the
 * only full copy, so it asks for the job's name to be typed.
 */
export async function askArchive(job) {
  const copy = h("input", { type: "radio", name: "dw-archive-kind", value: "ZipOnly", checked: true, class: "form-check-input" });
  const purge = h("input", { type: "radio", name: "dw-archive-kind", value: "Purge", class: "form-check-input" });
  const typed = h("input", { class: "form-control form-control-sm", placeholder: job.name, "aria-label": "Type the job name to confirm", disabled: true, autocomplete: "off" });
  const sync = () => { typed.disabled = !purge.checked; if (!purge.checked) typed.value = ""; };
  copy.addEventListener("change", sync);
  purge.addEventListener("change", sync);

  const body = h("div", {},
    h("label", { class: "form-check d-block mb-2" }, copy, h("strong", {}, " Keep a copy"), h("div", { class: "small text-muted ms-4" }, "Writes an archive and checks it. The job stays exactly as it is.")),
    h("label", { class: "form-check d-block mb-2" }, purge, h("strong", {}, " Archive and remove from the database"),
      h("div", { class: "small text-muted ms-4" }, "Writes the archive, proves it holds everything, then removes the job’s drawings, items and history from the database. The archive becomes the only full copy. Restore brings it all back.")),
    h("div", { class: "ms-4" }, h("label", { class: "small d-block mb-1" }, "To remove it, type the job’s name:"), typed),
    h("p", { class: "small text-muted mt-3 mb-0" }, "The job is held still while this runs and nobody can edit it. It continues if the server restarts."));

  const result = await dialog({
    title: `Archive “${job.name}”`, body,
    actions: [{ label: "Cancel" }, {
      label: "Archive", kind: "primary",
      validate: () => (purge.checked && typed.value.trim().toLowerCase() !== job.name.trim().toLowerCase() ? "Type the job’s name exactly to confirm." : null)
    }]
  });
  return result === "Archive" ? { kind: purge.checked ? "Purge" : "ZipOnly", confirmName: purge.checked ? typed.value.trim() : null } : null;
}

export async function askRestore(job) {
  return confirmDialog(`Restore “${job.name}”? Its drawings, items and history are put back from the archive and it returns to ${job.stateBeforeArchive || "its earlier state"}, read-only. You can reopen it afterwards.`, { ok: "Restore" });
}

/** Asks for an archive and queues it. Returns true when the server accepted it. */
export async function requestArchive(api, run, job) {
  const choice = await askArchive(job);
  if (!choice) return false;
  const op = await run(() => api.post(`/jobs/${job.jobId}/archive`, choice));
  if (op) toast("Archiving has started. You can leave this page; it carries on.", "success", 4000);
  return !!op;
}

export async function requestRestore(api, run, job) {
  if (!(await askRestore(job))) return false;
  const op = await run(() => api.post(`/jobs/${job.jobId}/restore`));
  if (op) toast("Restoring has started.", "success", 3000);
  return !!op;
}
