// Drawings: upload PDFs, record the pages found in each file as sheets, and say what each sheet is.

import { h, clear, fmtBytes, fmtDate, toast, select, badge, titleCase } from "./util.js";
import { Upload, UploadCancelled } from "./uploader.js";
import { openPdf, readPages } from "./pdfpages.js";

const KINDS = ["Plan", "Schedule", "Legend", "Notes", "Detail", "Riser", "Other"];

export async function render(container, ctx) {
  const uploads = h("div", { class: "dw-uploads" });
  const lists = h("div", {});
  const disposers = [];

  const refresh = async () => {
    const [files, sheets] = await Promise.all([ctx.files(true), ctx.sheets(true)]);
    clear(lists);
    lists.append(filesTable(ctx, files, sheets, refresh), sheetsTable(ctx, files, sheets, refresh));
  };

  container.append(dropZone(ctx, uploads, refresh), uploads, lists);
  await refresh();
  return { dispose() { disposers.forEach((d) => d()); }, refresh };
}

function dropZone(ctx, uploads, refresh) {
  const input = h("input", { type: "file", accept: "application/pdf,.pdf", multiple: true, class: "d-none" });
  const zone = h("div", { class: "dw-drop", tabindex: 0, role: "button", "aria-label": "Choose PDF drawings to upload" },
    h("strong", {}, "Drop PDF drawings here, or click to choose"),
    h("div", { class: "text-muted small" }, "Large files are sent in pieces and checked as they arrive. Sheets are recorded automatically when a file finishes."),
    input);
  const choose = () => input.click();
  zone.addEventListener("click", choose);
  zone.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); choose(); } });
  zone.addEventListener("dragover", (e) => { e.preventDefault(); zone.classList.add("dw-drop-over"); });
  zone.addEventListener("dragleave", () => zone.classList.remove("dw-drop-over"));
  const take = (files) => { for (const f of files) startUpload(ctx, f, uploads, refresh); };
  zone.addEventListener("drop", (e) => { e.preventDefault(); zone.classList.remove("dw-drop-over"); take([...e.dataTransfer.files]); });
  input.addEventListener("change", () => { take([...input.files]); input.value = ""; });
  return zone;
}

async function startUpload(ctx, file, host, refresh) {
  if (!/\.pdf$/i.test(file.name)) { toast(`${file.name} is not a PDF.`, "error"); return; }
  if (!(await ctx.ensureLock())) return;

  const bar = h("div", { class: "progress-bar", role: "progressbar", style: { width: "0%" } });
  const status = h("span", { class: "text-muted small" }, "Starting…");
  const cancel = h("button", { class: "btn btn-outline-secondary btn-sm", type: "button" }, "Cancel");
  const row = h("div", { class: "dw-uploadrow" }, h("div", { class: "dw-uploadname" }, h("strong", {}, file.name), h("span", { class: "text-muted small" }, fmtBytes(file.size))),
    h("div", { class: "progress flex-grow-1" }, bar), status, cancel);
  host.append(row);

  const upload = new Upload({
    api: ctx.api, jobId: ctx.jobId, file,
    onProgress: ({ sent, total, state }) => {
      bar.style.width = `${total ? Math.round((sent / total) * 100) : 0}%`;
      status.textContent = { starting: "Starting…", checking: "Checking the file…", sending: `${fmtBytes(sent)} of ${fmtBytes(total)}`, retrying: "Connection problem, retrying…", finishing: "Verifying…", done: "Stored" }[state] || state;
    }
  });
  cancel.addEventListener("click", () => upload.cancel());

  try {
    const stored = await upload.run();
    cancel.remove();
    status.textContent = stored.duplicateOf ? "Stored (identical to a file already on this job)" : "Stored. Recording pages…";
    if (!stored.duplicateOf) await recordPages(ctx, stored, status);
    ctx.invalidate("files", "sheets");
    await refresh();
    setTimeout(() => row.remove(), 4000);
  } catch (e) {
    cancel.remove();
    bar.classList.add("bg-danger");
    status.textContent = e instanceof UploadCancelled ? "Cancelled" : e.message;
    if (!(e instanceof UploadCancelled)) ctx.report(e);
  }
}

/** Opens the stored file in the browser, reads its pages, and tells the server what it found. */
export async function recordPages(ctx, file, statusEl) {
  try {
    const doc = await openPdf(ctx.boot, { url: ctx.api.url(`/jobs/${ctx.jobId}/files/${file.fileId}/content?inline=true`) });
    const pages = await readPages(doc, { onProgress: (n, total) => { if (statusEl) statusEl.textContent = `Reading page ${n} of ${total}…`; } });
    const sheets = pages.map((p) => ({
      pageNo: p.pageNo, widthPts: p.widthPts, heightPts: p.heightPts, rotation: p.rotation, sheetNumber: p.sheetNumber, title: p.title, discipline: null,
      kind: "Plan", contentHash: null, hasTextLayer: p.hasTextLayer
    }));
    await ctx.api.post(`/jobs/${ctx.jobId}/files/${file.fileId}/sheets`, { sheets });
    if (statusEl) statusEl.textContent = `Stored. ${pages.length} page(s) recorded.`;
    const blind = pages.filter((p) => !p.hasTextLayer).length;
    if (blind) toast(`${blind} page(s) in ${file.fileName} have no live text. They will need OCR or hosted vision to be read.`, "info", 8000);
    return pages.length;
  } catch (e) {
    ctx.report(e);
    if (statusEl) statusEl.textContent = "Stored, but its pages could not be recorded. Use Record pages.";
    return 0;
  }
}

function filesTable(ctx, files, sheets, refresh) {
  const pagesByFile = new Map();
  for (const s of sheets) pagesByFile.set(s.fileId, (pagesByFile.get(s.fileId) || 0) + 1);

  const rows = files.map((f) => {
    const recorded = pagesByFile.get(f.fileId) || 0;
    const actions = [h("a", { class: "btn btn-outline-secondary btn-sm", href: ctx.api.url(`/jobs/${ctx.jobId}/files/${f.fileId}/content`) }, "Download")];
    if (!f.duplicateOf && recorded === 0 && ctx.canChange) {
      actions.push(h("button", {
        class: "btn btn-outline-primary btn-sm", type: "button",
        onclick: async (ev) => { ev.target.disabled = true; if (await ctx.ensureLock()) { await recordPages(ctx, f, null); ctx.invalidate("sheets"); await refresh(); } ev.target.disabled = false; }
      }, "Record pages"));
    }

    return h("tr", {}, h("td", {}, f.fileName, f.duplicateOf ? h("span", { class: "ms-2" }, badge("copy", "warning")) : null),
      h("td", {}, fmtBytes(f.bytes)), h("td", {}, f.duplicateOf ? "—" : recorded), h("td", {}, `${f.uploadedBy}, ${fmtDate(f.uploadedUtc)}`), h("td", { class: "text-end" }, h("div", { class: "d-flex gap-1 justify-content-end" }, actions)));
  });

  return h("div", { class: "dw-card" }, h("h2", {}, `Files (${files.length})`),
    files.length ? h("table", { class: "table table-sm align-middle" }, h("thead", {}, h("tr", {}, ["File", "Size", "Pages", "Uploaded", ""].map((t) => h("th", {}, t)))), h("tbody", {}, rows))
      : h("p", { class: "text-muted" }, "No drawings yet."));
}

function sheetsTable(ctx, files, sheets, refresh) {
  const fileNames = new Map(files.map((f) => [f.fileId, f.fileName]));
  const can = ctx.canChange;

  const patch = async (sheet, body) => {
    const ok = await ctx.edit(() => ctx.api.patch(`/jobs/${ctx.jobId}/sheets/${sheet.sheetId}`, body));
    if (ok) ctx.invalidate("sheets");
    else await refresh();
    return ok;
  };

  const rows = sheets.map((s) => {
    const number = h("input", { class: "form-control form-control-sm", value: s.sheetNumber || "", disabled: !can, maxlength: 40, "aria-label": `Sheet number of page ${s.pageNo}` });
    const title = h("input", { class: "form-control form-control-sm", value: s.title || "", disabled: !can, maxlength: 200, "aria-label": `Title of page ${s.pageNo}` });
    const kind = select(KINDS, s.kind, { disabled: !can, "aria-label": `Kind of page ${s.pageNo}` });
    const scope = h("input", { type: "checkbox", class: "form-check-input", checked: s.inScope, disabled: !can, "aria-label": `Page ${s.pageNo} is in scope` });
    number.addEventListener("change", () => patch(s, { sheetNumber: number.value }));
    title.addEventListener("change", () => patch(s, { title: title.value }));
    kind.addEventListener("change", () => patch(s, { kind: kind.value }));
    scope.addEventListener("change", () => patch(s, { inScope: scope.checked }));
    return h("tr", { class: s.inScope ? "" : "text-muted" },
      h("td", {}, h("div", { class: "small text-muted" }, fileNames.get(s.fileId) || ""), `p. ${s.pageNo}`),
      h("td", {}, number), h("td", {}, title), h("td", {}, kind), h("td", { class: "text-center" }, scope),
      h("td", {}, [s.hasTextLayer ? null : badge("no text", "warning"), s.duplicateOf ? badge("duplicate", "secondary") : null, s.views.length ? badge(`${s.views.length} view(s)`, "light") : null]),
      h("td", { class: "text-end" }, h("a", { class: "btn btn-outline-primary btn-sm", href: `#/review/${s.sheetId}` }, "Open")));
  });

  return h("div", { class: "dw-card" }, h("h2", {}, `Sheets (${sheets.length})`),
    h("p", { class: "text-muted small" }, "A page that is identical to one already on the job is left out of scope so it is read once. Detectors refer to a sheet by its number, so keep the numbers right."),
    sheets.length ? h("div", { class: "table-responsive" }, h("table", { class: "table table-sm align-middle" },
      h("thead", {}, h("tr", {}, ["Page", "Sheet number", "Title", "Kind", "In scope", "", ""].map((t) => h("th", {}, t)))), h("tbody", {}, rows)))
      : h("p", { class: "text-muted" }, "Upload a drawing and its pages are recorded here."));
}
