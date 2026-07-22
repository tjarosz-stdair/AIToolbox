import { getSettings } from "./settings";
import type { JobsSnapshot, ParsedProduct, ProductParserWorkspaceState } from "./types";

type ProductSection = {
  key: string;
  title: string;
  fields: ProductField[];
};

type ProductField = {
  key: string;
  label: string;
  value: string;
  long: boolean;
};

const stateKey = "productParserWorkspaceState";
const zohoImportModeKey = "zohoImportModeRequested";
const defaultState: ProductParserWorkspaceState = {
  view: "tools",
  tab: "parse",
  search: "",
  expandedSectionKeys: []
};
let state: ProductParserWorkspaceState = { ...defaultState };
let signedIn = false;
let statusTimer: number | undefined;
let suppressNextLibraryLoad = false;
let suppressNextDetailSectionScroll = false;
let titleIconFailed = false;

const pageTitle = document.querySelector("#pageTitle") as HTMLElement;
const titleIcon = document.querySelector("#titleIcon") as HTMLImageElement;
const versionLabel = document.querySelector("#versionLabel") as HTMLElement;
const toolList = document.querySelector("#toolList") as HTMLElement;
const parserView = document.querySelector("#parserView") as HTMLElement;
const jobsView = document.querySelector("#jobsView") as HTMLElement;
const status = document.querySelector("#status") as HTMLElement;
const backButton = document.querySelector("#backButton") as HTMLButtonElement;
const openWebButton = document.querySelector("#openWebButton") as HTMLButtonElement;
const navSeparator = document.querySelector("#navSeparator") as HTMLElement;
const signInButton = document.querySelector("#signInButton") as HTMLButtonElement;
const signOutButton = document.querySelector("#signOutButton") as HTMLButtonElement;
const authStatus = document.querySelector("#authStatus") as HTMLElement;
const openParserButton = document.querySelector("#openParser") as HTMLButtonElement;
const openParserHint = document.querySelector("#openParserHint") as HTMLElement;
const openJobsButton = document.querySelector("#openJobs") as HTMLButtonElement;
const openJobsHint = document.querySelector("#openJobsHint") as HTMLElement;
const jobsCountBadge = document.querySelector("#jobsCountBadge") as HTMLElement;
const jobsUpdated = document.querySelector("#jobsUpdated") as HTMLElement;
const jobsList = document.querySelector("#jobsList") as HTMLElement;
const refreshJobsButton = document.querySelector("#refreshJobsButton") as HTMLButtonElement;
const fields = document.querySelector("#fields") as HTMLElement;
const fileInput = document.querySelector("#fileInput") as HTMLInputElement;
const dropZone = document.querySelector("#dropZone") as HTMLElement;
const publicToggle = document.querySelector("#publicToggle") as HTMLInputElement;
const parseMode = document.querySelector("#parseMode") as HTMLSelectElement;
const userNotes = document.querySelector("#userNotes") as HTMLTextAreaElement;
const parsePanel = document.querySelector("#parsePanel") as HTMLElement;
const libraryPanel = document.querySelector("#libraryPanel") as HTMLElement;
const detailPanel = document.querySelector("#detailPanel") as HTMLElement;
const productHeader = document.querySelector("#productHeader") as HTMLElement;
const importToZohoButton = document.querySelector("#importToZohoButton") as HTMLButtonElement;
const librarySearchForm = document.querySelector("#librarySearchForm") as HTMLFormElement;
const librarySearch = document.querySelector("#librarySearch") as HTMLInputElement;
const libraryResults = document.querySelector("#libraryResults") as HTMLElement;
const tabButtons = {
  parse: document.querySelector("#parseTabButton") as HTMLButtonElement,
  library: document.querySelector("#libraryTabButton") as HTMLButtonElement,
  detail: document.querySelector("#detailTabButton") as HTMLButtonElement
};

openParserButton.addEventListener("click", () => {
  if (!signedIn) {
    authStatus.textContent = "Sign in first.";
    showStatus("Sign in before opening Product Parser.");
    return;
  }

  showParser("parse");
});
titleIcon.addEventListener("error", () => {
  titleIconFailed = true;
  titleIcon.remove();
  if (state.view === "tools") {
    pageTitle.textContent = "AI Toolbox";
  }
}, { once: true });
openJobsButton.addEventListener("click", () => {
  if (!signedIn) {
    authStatus.textContent = "Sign in first.";
    showStatus("Sign in before viewing jobs.");
    return;
  }

  showJobs();
});
backButton.addEventListener("click", () => showTools());
openWebButton.addEventListener("click", () => openProductParserWeb());
refreshJobsButton.addEventListener("click", () => loadJobs(true));
document.addEventListener("click", event => {
  const target = event.target instanceof Element ? event.target.closest("#signInButton") : null;
  if (!target) {
    return;
  }

  event.preventDefault();
  void signIn();
});
signOutButton.addEventListener("click", () => signOut());
importToZohoButton.addEventListener("click", () => importSelectedProductToZoho());
tabButtons.parse.addEventListener("click", () => showParser("parse"));
tabButtons.library.addEventListener("click", () => showParser("library"));
tabButtons.detail.addEventListener("click", () => showParser("detail"));
librarySearchForm.addEventListener("submit", event => {
  event.preventDefault();
  state.search = librarySearch.value.trim();
  saveState();
  loadLibrary();
});
fileInput.addEventListener("change", () => fileInput.files && uploadPdfs(Array.from(fileInput.files)));

dropZone.addEventListener("dragover", event => {
  event.preventDefault();
  dropZone.classList.add("dragging");
});
dropZone.addEventListener("dragleave", () => dropZone.classList.remove("dragging"));
dropZone.addEventListener("drop", event => {
  event.preventDefault();
  dropZone.classList.remove("dragging");
  const files = Array.from(event.dataTransfer?.files || [])
    .filter(file => file.type === "application/pdf" || file.name.toLowerCase().endsWith(".pdf"));
  if (files.length > 0) {
    uploadPdfs(files);
  }
});

void restoreState();

async function restoreState(): Promise<void> {
  await refreshAuthState();
  const stored = await chrome.storage.local.get([stateKey, zohoImportModeKey]);
  state = { ...defaultState, ...(stored[stateKey] as Partial<ProductParserWorkspaceState> | undefined) };
  const importMode = new URLSearchParams(window.location.search).get("mode") === "zoho-import"
    || stored[zohoImportModeKey] === true;
  if (stored[zohoImportModeKey] === true) {
    await chrome.storage.local.remove(zohoImportModeKey);
  }
  if (importMode) {
    state.view = "parser";
    state.tab = "library";
    state.search = "";
    state.selectedProduct = undefined;
    state.selectedSectionKey = undefined;
    state.expandedSectionKeys = [];
    suppressNextLibraryLoad = true;
  }
  if (!signedIn) {
    state.view = "tools";
  }
  librarySearch.value = state.search;
  if (state.view === "parser") {
    showParser(state.tab, false);
    if (state.selectedProduct) {
      renderProduct(state.selectedProduct);
    }
    if (state.tab === "library" && !importMode) {
      void loadLibrary(false);
    }
  } else if (state.view === "jobs") {
    showJobs(false);
  } else {
    showTools(false);
  }
  void updateImportButton();
  void loadJobs(false);
}

function showParser(tab: ProductParserWorkspaceState["tab"] = state.tab, persist = true): void {
  state.view = "parser";
  state.tab = tab;
  pageTitle.textContent = "Product Parser";
  versionLabel.classList.add("hidden");
  backButton.classList.remove("hidden");
  openWebButton.classList.remove("hidden");
  navSeparator.classList.remove("hidden");
  syncAuthHeader();
  toolList.classList.add("hidden");
  jobsView.classList.add("hidden");
  parserView.classList.remove("hidden");
  parsePanel.classList.toggle("hidden", tab !== "parse");
  libraryPanel.classList.toggle("hidden", tab !== "library");
  detailPanel.classList.toggle("hidden", tab !== "detail");
  for (const [key, button] of Object.entries(tabButtons)) {
    button.classList.toggle("active", key === tab);
  }
  if (tab === "library" && suppressNextLibraryLoad) {
    libraryResults.innerHTML = `<div class="empty-state">Search for a product.</div>`;
    suppressNextLibraryLoad = false;
  } else if (tab === "library" && libraryResults.childElementCount === 0) {
    void loadLibrary(false);
  }
  if (tab === "detail" && !state.selectedProduct) {
    productHeader.innerHTML = `<span class="muted">No product selected.</span>`;
    fields.innerHTML = "";
  }
  if (persist) {
    void saveState();
  }
  void updateImportButton();
}

function showTools(persist = true): void {
  state.view = "tools";
  pageTitle.textContent = titleIconFailed ? "AI Toolbox" : "Toolbox";
  versionLabel.classList.remove("hidden");
  backButton.classList.add("hidden");
  openWebButton.classList.add("hidden");
  navSeparator.classList.add("hidden");
  syncAuthHeader();
  parserView.classList.add("hidden");
  jobsView.classList.add("hidden");
  toolList.classList.remove("hidden");
  if (persist) {
    void saveState();
  }
  importToZohoButton.classList.add("hidden");
}

function showJobs(persist = true): void {
  state.view = "jobs";
  pageTitle.textContent = "Jobs";
  versionLabel.classList.add("hidden");
  backButton.classList.remove("hidden");
  openWebButton.classList.remove("hidden");
  navSeparator.classList.remove("hidden");
  syncAuthHeader();
  parserView.classList.add("hidden");
  toolList.classList.add("hidden");
  jobsView.classList.remove("hidden");
  importToZohoButton.classList.add("hidden");
  if (persist) {
    void saveState();
  }
  void loadJobs(true);
}

async function signOut(): Promise<void> {
  signOutButton.disabled = true;
  try {
    const response = await chrome.runtime.sendMessage({ type: "signOut" });
    if (response?.error) {
      throw new Error(response.error);
    }

    signedIn = false;
    state.view = "tools";
    signInButton.classList.remove("hidden");
    signOutButton.classList.add("hidden");
    authStatus.textContent = "Signed out.";
    openParserButton.disabled = true;
    openParserHint.textContent = "Sign in to parse, look up, copy.";
    openJobsButton.disabled = true;
    openJobsHint.textContent = "Sign in to view active jobs.";
    renderJobs({ count: 0, jobs: [] });
    showTools();
  } catch (error) {
    authStatus.textContent = error instanceof Error ? error.message : String(error);
  } finally {
    signOutButton.disabled = false;
  }
}

async function signIn(): Promise<void> {
  if (signInButton.disabled) {
    return;
  }

  signInButton.disabled = true;
  authStatus.textContent = "Signing in...";
  showStatus("Signing in...", 0);
  try {
    await getToken();
    showStatus("Signed in.");
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    authStatus.textContent = message.includes("Configure the extension options")
      ? "Options needed."
      : `Sign in failed: ${message}`;
    showStatus(message, 6500);
  } finally {
    signInButton.disabled = false;
  }
}

async function getToken(): Promise<string> {
  const response = await chrome.runtime.sendMessage({ type: "getToken" });
  if (!response) {
    throw new Error("No response from the extension background worker.");
  }
  if (response.error) {
    throw new Error(response.error);
  }

  applySignedInIdentity(response.accountUsername || response.accountName);
  return response.token;
}

function applySignedInIdentity(identity: string | undefined): void {
  signedIn = true;
  authStatus.textContent = identity ? `Signed in: ${identity}` : "Signed in.";
  openParserButton.disabled = false;
  openParserHint.textContent = "Parse, look up, copy.";
  openJobsButton.disabled = false;
  openJobsHint.textContent = "View active parser jobs.";
  syncAuthHeader();
  void loadJobs(true);
}

async function refreshAuthState(): Promise<void> {
  try {
    const response = await chrome.runtime.sendMessage({ type: "getAuthState" });
    if (response?.signedIn) {
      applySignedInIdentity(response.accountUsername || response.accountName);
      return;
    }
  } catch {
    // Treat auth-state lookup as best effort.
  }

  signedIn = false;
  authStatus.textContent = "";
  openParserButton.disabled = true;
  openParserHint.textContent = "Sign in to parse, look up, copy.";
  openJobsButton.disabled = true;
  openJobsHint.textContent = "Sign in to view active jobs.";
  syncAuthHeader();
}

function syncAuthHeader(): void {
  const onMainPage = state.view === "tools";
  authStatus.classList.toggle("hidden", !onMainPage);
  signInButton.classList.toggle("hidden", !onMainPage || signedIn);
  signOutButton.classList.toggle("hidden", !onMainPage || !signedIn);
}

async function openProductParserWeb(): Promise<void> {
  const settings = await getSettings();
  const path = state.view === "jobs" ? "/ProductParser?viewMode=jobs" : "/ProductParser";
  await chrome.tabs.create({ url: `${settings.apiBaseUrl}${path}` });
}

async function uploadPdfs(files: File[]): Promise<void> {
  try {
    if (files.length === 0) {
      return;
    }

    showParser("parse");
    showStatus(files.length === 1 ? "Parsing PDF..." : `Parsing ${files.length} PDFs...`, 0);
    const settings = await getSettings();
    const token = await getToken();
    const body = new FormData();
    for (const file of files) {
      body.append("files", file);
    }
    body.append("parseMode", parseMode.value);
    body.append("userNotes", userNotes.value);
    body.append("isPublic", String(publicToggle.checked));
    const response = await fetch(`${settings.apiBaseUrl}/api/v1/product-parse/jobs?async=true`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body
    });
    if (!response.ok) {
      throw new Error(await response.text());
    }

    const payload = await response.json() as { toolJobId?: number; isDuplicate?: boolean; status?: string; message?: string };
    showStatus(payload.message || `Job #${payload.toolJobId || ""} submitted.`);
    await loadJobs(true);
    showJobs();
  } catch (error) {
    showStatus(error instanceof Error ? error.message : String(error), 6500);
  } finally {
    fileInput.value = "";
  }
}

async function loadJobs(forceRefresh: boolean): Promise<void> {
  if (!signedIn) {
    renderJobs({ count: 0, jobs: [] });
    return;
  }

  try {
    const response = await chrome.runtime.sendMessage({ type: forceRefresh ? "refreshJobs" : "getJobsSnapshot", notify: false });
    if (response?.error && !response.jobs?.length) {
      throw new Error(response.error);
    }

    renderJobs(response as JobsSnapshot);
  } catch (error) {
    renderJobs({
      count: 0,
      jobs: [],
      error: error instanceof Error ? error.message : String(error),
      lastUpdatedUtc: new Date().toISOString()
    });
  }
}

function renderJobs(snapshot: JobsSnapshot): void {
  const count = Number(snapshot.count || snapshot.jobs.length || 0);
  jobsCountBadge.textContent = String(count);
  jobsCountBadge.classList.toggle("hidden", count === 0);
  openJobsHint.textContent = signedIn
    ? count === 0 ? "No active jobs." : `${count} active job${count === 1 ? "" : "s"}.`
    : "Sign in to view active jobs.";
  jobsUpdated.textContent = snapshot.lastUpdatedUtc
    ? `Updated ${formatDateTime(snapshot.lastUpdatedUtc)}`
    : "Not refreshed yet.";

  jobsList.innerHTML = "";
  if (snapshot.error) {
    const error = document.createElement("div");
    error.className = "empty-state";
    error.textContent = `Could not refresh jobs: ${snapshot.error}`;
    jobsList.append(error);
    return;
  }

  if (count === 0) {
    jobsList.innerHTML = `<div class="empty-state">No jobs in progress.</div>`;
    return;
  }

  for (const job of snapshot.jobs) {
    const row = document.createElement("div");
    row.className = "history-row";
    row.innerHTML = `
      <strong>${escapeHtml(job.title || `${job.toolKey} #${job.id}`)}</strong>
      <div class="job-row-meta">
        <span class="job-pill">${escapeHtml(job.status || "Active")}</span>
        <span class="job-pill">${escapeHtml(formatDuration(job.elapsedSeconds || 0))}</span>
      </div>
    `;
    jobsList.append(row);
  }
}

async function loadLibrary(showLoading = true): Promise<void> {
  try {
    if (showLoading) {
      showStatus("Loading library...", 0);
    }
    const settings = await getSettings();
    const token = await getToken();
    const url = new URL(`${settings.apiBaseUrl}/api/v1/products`);
    url.searchParams.set("scope", "all");
    if (state.search) {
      url.searchParams.set("search", state.search);
    }
    const response = await fetch(url.toString(), {
      headers: { Authorization: `Bearer ${token}` }
    });
    if (!response.ok) {
      throw new Error(await response.text());
    }

    const products = await response.json() as ParsedProduct[];
    renderProductChoices(products);
    showStatus(`${products.length} product${products.length === 1 ? "" : "s"}.`);
  } catch (error) {
    showStatus(error instanceof Error ? error.message : String(error), 6500);
  }
}

function renderProductChoices(products: ParsedProduct[]): void {
  libraryResults.innerHTML = "";
  if (products.length === 0) {
    libraryResults.innerHTML = `<div class="empty-state">No products found.</div>`;
    return;
  }

  for (const product of products) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "history-row";
    const title = product.productCode || product.productName || `Product ${product.id}`;
    const parsedDate = formatProductRunDate(product.createdUtc || product.updatedUtc);
    const subtitle = [product.productName, product.supplier, product.manufacturer]
      .filter(Boolean)
      .join(" · ");
    button.innerHTML = `
      <span class="history-row-title">
        <strong>${escapeHtml(title)}</strong>
        ${parsedDate ? `<span class="date-pill">${escapeHtml(parsedDate)}</span>` : ""}
      </span>
      ${subtitle ? `<span>${escapeHtml(subtitle)}</span>` : ""}
    `;
    button.addEventListener("click", () => selectProduct(product));
    libraryResults.append(button);
  }
}

async function selectProduct(product: ParsedProduct): Promise<void> {
  try {
    const loaded = await loadProductDetail(product);
    state.selectedProduct = loaded;
    const sectionKeys = buildSections(loaded).map(section => section.key);
    if (!state.selectedSectionKey || !sectionKeys.includes(state.selectedSectionKey)) {
      state.selectedSectionKey = sectionKeys[0];
    }
    suppressNextDetailSectionScroll = true;
    renderProduct(loaded);
    showParser("detail");
    await saveState();
  } catch (error) {
    showStatus(error instanceof Error ? error.message : String(error), 6500);
  }
}

async function loadProductDetail(product: ParsedProduct): Promise<ParsedProduct> {
  const id = product.primaryParsedProductId || product.id;
  const settings = await getSettings();
  const token = await getToken();
  const response = await fetch(`${settings.apiBaseUrl}/api/v1/products/${id}`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  if (!response.ok) {
    return product;
  }

  return await response.json() as ParsedProduct;
}

function renderProduct(product: ParsedProduct): void {
  const sections = buildSections(product);
  const title = product.productCode || product.productName || `Product ${product.id}`;
  productHeader.innerHTML = `
    <strong>${escapeHtml(title)}</strong>
    ${product.productName && product.productName !== title ? `<span>${escapeHtml(product.productName)}</span>` : ""}
  `;
  fields.innerHTML = "";
  for (const section of sections) {
    const details = document.createElement("details");
    details.className = "product-section";
    details.dataset.sectionKey = section.key;
    details.open = state.expandedSectionKeys.includes(section.key) || state.selectedSectionKey === section.key;
    const summary = document.createElement("summary");
    summary.textContent = section.title;
    summary.addEventListener("click", () => {
      state.selectedSectionKey = section.key;
      persistExpandedSections();
    });
    details.addEventListener("toggle", () => persistExpandedSections());
    const sectionFields = document.createElement("div");
    sectionFields.className = "section-fields";
    for (const field of section.fields) {
      sectionFields.append(renderField(section, field));
    }
    details.append(summary, sectionFields);
    fields.append(details);
  }

  requestAnimationFrame(() => {
    if (suppressNextDetailSectionScroll) {
      suppressNextDetailSectionScroll = false;
      document.querySelector("main")?.scrollTo({ top: 0 });
      return;
    }

    const selected = state.selectedSectionKey
      ? fields.querySelector(`[data-section-key="${cssEscape(state.selectedSectionKey)}"]`)
      : null;
    selected?.scrollIntoView({ block: "start" });
  });
  void updateImportButton();
}

async function importSelectedProductToZoho(): Promise<void> {
  if (!state.selectedProduct) {
    showStatus("Select a product first.");
    return;
  }

  try {
    const target = await getZohoImportTarget();
    if (!target) {
      showStatus("Open a Zoho product create form first.");
      return;
    }

    const response = await chrome.tabs.sendMessage(target.tabId, {
      type: "AIT_IMPORT_PRODUCT",
      product: state.selectedProduct
    });
    if (response?.error) {
      throw new Error(response.error);
    }

    showStatus(response?.message || "Imported into Zoho.");
    resetProductParserForNextZohoImport();
  } catch (error) {
    showStatus(error instanceof Error ? error.message : String(error), 6500);
  }
}

async function updateImportButton(): Promise<void> {
  if (!state.selectedProduct || state.view !== "parser" || state.tab !== "detail") {
    importToZohoButton.classList.add("hidden");
    return;
  }

  const target = await getZohoImportTarget();
  importToZohoButton.classList.toggle("hidden", !target);
}

async function getZohoImportTarget(): Promise<{ tabId: number; title?: string; url?: string } | null> {
  const response = await chrome.runtime.sendMessage({ type: "getZohoImportTarget" });
  if (response?.error) {
    throw new Error(response.error);
  }

  return response?.target || null;
}

function renderField(section: ProductSection, field: ProductField): HTMLElement {
  const wrapper = document.createElement("label");
  wrapper.className = "field-label";
  wrapper.textContent = field.label;
  const row = document.createElement("div");
  row.className = "copy-row";
  const input = document.createElement(field.long ? "textarea" : "input") as HTMLInputElement | HTMLTextAreaElement;
  input.value = field.value;
  input.readOnly = true;
  input.addEventListener("focus", () => {
    state.selectedSectionKey = section.key;
    void saveState();
  });
  const copy = document.createElement("button");
  copy.type = "button";
  copy.textContent = "Copy";
  copy.addEventListener("click", async () => {
    await navigator.clipboard.writeText(input.value);
    state.selectedSectionKey = section.key;
    await saveState();
    showStatus(`Copied ${field.label}.`);
  });
  row.append(input, copy);
  wrapper.append(row);
  return wrapper;
}

function buildSections(product: ParsedProduct): ProductSection[] {
  const data = parseProductData(product.productDataJson);
  return Object.entries(data)
    .map(([key, value]) => buildSection(key, value))
    .filter((section): section is ProductSection => section !== null);
}

function buildSection(key: string, value: unknown): ProductSection | null {
  if (value === null || value === undefined || value === "") {
    return null;
  }

  if (isPlainObject(value)) {
    const productFields = Object.entries(value)
      .map(([childKey, childValue]) => buildField(childKey, childValue))
      .filter((field): field is ProductField => field !== null);
    return productFields.length === 0 ? null : { key, title: toLabel(key), fields: productFields };
  }

  return {
    key,
    title: toLabel(key),
    fields: [buildField(key, value)].filter((field): field is ProductField => field !== null)
  };
}

function buildField(key: string, value: unknown): ProductField | null {
  const text = valueToText(value);
  if (!text) {
    return null;
  }

  return {
    key,
    label: toLabel(key),
    value: text,
    long: text.length > 110 || Array.isArray(value) || isPlainObject(value)
  };
}

function firstSectionKey(product: ParsedProduct): string | undefined {
  return buildSections(product)[0]?.key;
}

function parseProductData(json: string | undefined): Record<string, unknown> {
  try {
    const parsed = JSON.parse(json || "{}");
    return isPlainObject(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function valueToText(value: unknown): string {
  if (value === null || value === undefined) {
    return "";
  }

  if (typeof value === "string") {
    return value;
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  if (Array.isArray(value)) {
    return value.map(valueToText).filter(Boolean).join("\n");
  }

  if (isPlainObject(value)) {
    return Object.entries(value)
      .map(([key, childValue]) => `${toLabel(key)}: ${valueToText(childValue)}`)
      .filter(line => !line.endsWith(": "))
      .join("\n");
  }

  return String(value);
}

function persistExpandedSections(): void {
  state.expandedSectionKeys = Array.from(fields.querySelectorAll<HTMLDetailsElement>("details.product-section"))
    .filter(details => details.open)
    .map(details => details.dataset.sectionKey || "")
    .filter(Boolean);
  void saveState();
}

async function saveState(): Promise<void> {
  await chrome.storage.local.set({ [stateKey]: state });
}

function resetProductParserForNextZohoImport(): void {
  state.view = "parser";
  state.tab = "library";
  state.search = "";
  state.selectedProduct = undefined;
  state.selectedSectionKey = undefined;
  state.expandedSectionKeys = [];
  librarySearch.value = "";
  void saveState();
}

function showStatus(message: string, durationMs = 3200): void {
  if (statusTimer) {
    window.clearTimeout(statusTimer);
    statusTimer = undefined;
  }

  status.textContent = message;
  status.classList.toggle("visible", Boolean(message));
  if (message && durationMs > 0) {
    statusTimer = window.setTimeout(() => {
      status.textContent = "";
      status.classList.remove("visible");
      statusTimer = undefined;
    }, durationMs);
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function toLabel(value: string): string {
  return value
    .replace(/[_-]+/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/^./, first => first.toUpperCase());
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, character => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
  })[character] || character);
}

function cssEscape(value: string): string {
  return value.replace(/["\\]/g, "\\$&");
}

function formatDuration(seconds: number): string {
  const normalized = Math.max(0, Math.round(seconds));
  if (normalized < 60) {
    return `${normalized}s`;
  }

  const minutes = Math.floor(normalized / 60);
  const remainingSeconds = normalized % 60;
  if (minutes < 60) {
    return remainingSeconds === 0 ? `${minutes}m` : `${minutes}m ${remainingSeconds}s`;
  }

  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return remainingMinutes === 0 ? `${hours}h` : `${hours}h ${remainingMinutes}m`;
}

function formatDateTime(value: string): string {
  const date = parseUtcApiDate(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return date.toLocaleString(undefined, {
    hour: "numeric",
    minute: "2-digit",
    month: "numeric",
    day: "numeric"
  });
}

function parseUtcApiDate(value: string): Date {
  const trimmed = value.trim();
  const hasTimeZone = /(?:z|[+-]\d{2}:?\d{2})$/i.test(trimmed);
  return new Date(hasTimeZone ? trimmed : `${trimmed}Z`);
}

function formatProductRunDate(value: string | undefined): string {
  if (!value) {
    return "";
  }

  const formatted = formatDateTime(value);
  return formatted ? `Parsed ${formatted}` : "";
}
