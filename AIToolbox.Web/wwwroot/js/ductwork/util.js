// Small helpers shared by every Ductwork screen. No framework: elements are built with h() and nothing is ever
// assembled from strings, so text from the server (names, labels, evidence) can never become markup.

/** Builds an element. Attributes: class, text, on<Event> handlers, data-* via dataset, booleans, everything else as an attribute. */
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    // ARIA state is meaningful when false (aria-expanded="false"), so it is written as text and never dropped.
    if (key.startsWith("aria-") && typeof value === "boolean") { el.setAttribute(key, String(value)); continue; }
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") el.className = value;
    else if (key === "text") el.textContent = value;
    else if (key === "dataset") Object.assign(el.dataset, value);
    else if (key === "style" && typeof value === "object") Object.assign(el.style, value);
    else if (key.startsWith("on") && typeof value === "function") el.addEventListener(key.slice(2).toLowerCase(), value);
    else if (value === true) el.setAttribute(key, "");
    else el.setAttribute(key, String(value));
  }
  append(el, children);
  return el;
}

export function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child === undefined || child === null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

export const $ = (selector, root = document) => root.querySelector(selector);
export const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

export function fmtDate(iso) {
  if (!iso) return "";
  const d = new Date(iso.endsWith("Z") || /[+-]\d\d:\d\d$/.test(iso) ? iso : iso + "Z");
  return Number.isNaN(d.getTime()) ? String(iso) : d.toLocaleString();
}

export function fmtBytes(n) {
  if (n == null) return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

/** A quantity as it should read on a count sheet: whole numbers without decimals, otherwise up to two places. */
export function fmtQty(n) {
  if (n === null || n === undefined) return "TBD";
  const rounded = Math.round(n * 100) / 100;
  return Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(2).replace(/0+$/, "").replace(/\.$/, "");
}

export function pct(n) {
  return n == null ? "" : `${Math.round(n * 100)}%`;
}

export function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

export function titleCase(text) {
  return String(text ?? "").replace(/([a-z])([A-Z])/g, "$1 $2");
}

export function badge(text, kind = "secondary") {
  return h("span", { class: `badge text-bg-${kind}` }, text);
}

// ---- toasts

let toastHost;

export function toast(message, kind = "info", ms = 5000) {
  if (!toastHost) {
    toastHost = h("div", { class: "dw-toasts", "aria-live": "polite" });
    document.body.append(toastHost);
  }
  const el = h("div", { class: `dw-toast dw-toast-${kind}`, role: kind === "error" ? "alert" : "status" }, message);
  toastHost.append(el);
  setTimeout(() => el.remove(), kind === "error" ? Math.max(ms, 9000) : ms);
  return el;
}

// ---- dialogs

/**
 * Shows a dialog and resolves with the label of the button pressed (or null if dismissed). `body` is a Node.
 * An action can have `validate: () => string|null`; a message keeps the dialog open and shows it.
 */
export function dialog({ title, body, actions = [{ label: "OK", kind: "primary" }], wide = false }) {
  return new Promise((resolve) => {
    const error = h("div", { class: "dw-dialog-error", hidden: true });
    const close = (value) => {
      overlay.remove();
      document.removeEventListener("keydown", onKey);
      resolve(value);
    };
    const buttons = actions.map((a) =>
      h("button", {
        type: "button", class: `btn btn-${a.kind || "secondary"} btn-sm`,
        onclick: () => {
          const problem = a.validate ? a.validate() : null;
          if (problem) { error.textContent = problem; error.hidden = false; return; }
          close(a.label);
        }
      }, a.label));
    const box = h("div", { class: `dw-dialog${wide ? " dw-dialog-wide" : ""}`, role: "dialog", "aria-modal": "true", "aria-label": title },
      h("div", { class: "dw-dialog-head" }, h("strong", {}, title), h("button", { type: "button", class: "btn-close", "aria-label": "Close", onclick: () => close(null) })),
      h("div", { class: "dw-dialog-body" }, body, error),
      h("div", { class: "dw-dialog-foot" }, buttons));
    const overlay = h("div", { class: "dw-overlay" }, box);
    const onKey = (e) => { if (e.key === "Escape") close(null); };
    document.addEventListener("keydown", onKey);
    document.body.append(overlay);
    (box.querySelector("input,select,textarea") || buttons[buttons.length - 1])?.focus();
  });
}

export async function confirmDialog(message, { title = "Please confirm", ok = "Confirm", kind = "primary" } = {}) {
  return (await dialog({ title, body: h("p", {}, message), actions: [{ label: "Cancel" }, { label: ok, kind }] })) === ok;
}

/** Asks for one line of text (a reason). Resolves with the text, or null if cancelled. */
export async function promptDialog(message, { title = "Reason", placeholder = "", required = true, ok = "OK" } = {}) {
  const input = h("input", { type: "text", class: "form-control", placeholder, maxlength: 400 });
  const result = await dialog({
    title, body: h("div", {}, h("p", {}, message), input),
    actions: [{ label: "Cancel" }, { label: ok, kind: "primary", validate: () => (required && !input.value.trim() ? "Please enter a reason." : null) }]
  });
  return result === ok ? input.value.trim() : null;
}

/** A labelled form row. */
export function field(label, control, hint) {
  return h("div", { class: "mb-2" }, h("label", { class: "form-label mb-1" }, label), control, hint ? h("div", { class: "form-text" }, hint) : null);
}

export function select(options, value, attrs = {}) {
  const el = h("select", { class: "form-select form-select-sm", ...attrs });
  for (const o of options) {
    const [val, text] = Array.isArray(o) ? o : [o, o];
    el.append(h("option", { value: val, selected: String(val) === String(value) }, text));
  }
  return el;
}

export function download(filename, text, type = "text/csv") {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = h("a", { href: url, download: filename });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}

export function csvEscape(value) {
  const s = value === null || value === undefined ? "" : String(value);
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function toCsv(rows) {
  return rows.map((r) => r.map(csvEscape).join(",")).join("\r\n");
}
