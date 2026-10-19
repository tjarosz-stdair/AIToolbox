// What every Ductwork page starts from: the facts the server put in the page, and the antiforgery token.

export function readBoot(doc = document) {
  const el = doc.getElementById("dw-boot");
  const d = el?.dataset || {};
  return {
    api: d.api || "/ductwork/api", base: d.base || "/Ductwork", jobId: Number(d.jobId || 0), email: d.email || "", isAdmin: d.admin === "true", isJobManager: d.manager === "true",
    pdfjs: d.pdfjs, pdfjsWorker: d.pdfjsWorker, signalr: d.signalr, progressHub: d.progressHub
  };
}

export function antiforgeryFrom(doc = document) {
  return () => doc.querySelector("#dw-token-form input[name='__RequestVerificationToken']")?.value || "";
}
