// The one place the browser talks to the Ductwork API. It adds the tab id (the server derives the editing session
// from the signed-in person plus this id), the antiforgery token, and, once the tab holds the editing lock, the
// fencing token every content edit must carry. Failures become ApiError with the server's own code and words.

export class ApiError extends Error {
  constructor(status, code, message, body) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.body = body;
  }

  get isLockLost() { return this.status === 409 && this.code === "lock-lost"; }
  get isVersionConflict() { return this.status === 409 && this.code === "version-conflict"; }
  get isLocked() { return this.status === 409 && this.code === "locked"; }
  get isNotFound() { return this.status === 404; }
  get isForbidden() { return this.status === 403; }
}

/** A fresh id for this page load. Reloading starts a new editing session, as a new browser tab would. */
export function newTabId() {
  const bytes = new Uint8Array(12);
  (globalThis.crypto || window.crypto).getRandomValues(bytes);
  return "t" + [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export function createApi({ base, tabId = newTabId(), antiforgery = () => "", fetchImpl = (...a) => fetch(...a) }) {
  let fencing = null;
  const root = base.replace(/\/$/, "");

  async function request(method, path, { body, fencing: fencingOverride, headers = {}, rawBody, signal, keepalive } = {}) {
    const h = { Accept: "application/json", "X-Ductwork-Tab": tabId, ...headers };
    const token = antiforgery();
    if (token) h.RequestVerificationToken = token;
    const useFencing = fencingOverride === undefined ? fencing : fencingOverride;
    if (method !== "GET" && useFencing) h["X-Ductwork-Fencing-Token"] = String(useFencing);

    const init = { method, headers: h, credentials: "same-origin", signal, keepalive };
    if (rawBody !== undefined) init.body = rawBody;
    else if (body !== undefined) { h["Content-Type"] = "application/json"; init.body = JSON.stringify(body); }

    let response;
    try {
      response = await fetchImpl(root + path, init);
    } catch (e) {
      if (e?.name === "AbortError") throw e;
      throw new ApiError(0, "network", "The server could not be reached. Check your connection and try again.", null);
    }

    if (response.status === 204) return null;
    const text = await response.text();
    let data = null;
    if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    if (!response.ok) {
      const message = data?.error || data?.title || (text && text.length < 300 && !text.startsWith("<") ? text : `The server answered ${response.status}.`);
      throw new ApiError(response.status, data?.code || "", message, data);
    }
    return data;
  }

  return {
    tabId,
    base: root,
    request,
    get: (path, opts) => request("GET", path, opts),
    post: (path, body, opts) => request("POST", path, { body, ...opts }),
    put: (path, body, opts) => request("PUT", path, { body, ...opts }),
    patch: (path, body, opts) => request("PATCH", path, { body, ...opts }),
    del: (path, opts) => request("DELETE", path, opts),
    setFencing(value) { fencing = value || null; },
    get fencing() { return fencing; },
    /** A URL the browser can fetch directly (a PDF for pdf.js). It carries the session cookie, so it needs no header. */
    url: (path) => root + path
  };
}

/** Builds a query string, leaving out anything empty. */
export function qs(params) {
  const parts = [];
  for (const [k, v] of Object.entries(params || {})) {
    if (v === undefined || v === null || v === "") continue;
    for (const item of Array.isArray(v) ? v : [v]) parts.push(`${encodeURIComponent(k)}=${encodeURIComponent(item)}`);
  }
  return parts.length ? "?" + parts.join("&") : "";
}
