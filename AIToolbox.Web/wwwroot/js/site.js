document.addEventListener("DOMContentLoaded", () => {
  wireProviderExport();
  wireCompareProgress();
  wirePdfDropZone();
  wireJobsMenu();
  wireProductDetailControls();
  wireSortableTables();
  wireReadOnlyContentModalTriggers();
  wireProductParserDirectivePreview();
});

function wireProviderExport() {
  const button = document.querySelector("[data-export-providers-url]");
  if (!button) {
    return;
  }

  const status = document.querySelector("[data-export-status]");
  button.addEventListener("click", async () => {
    const originalText = button.textContent;
    button.disabled = true;
    button.textContent = "Copying...";

    try {
      const response = await fetch(button.dataset.exportProvidersUrl, {
        headers: { Accept: "application/json" }
      });

      if (!response.ok) {
        throw new Error(`Export failed with HTTP ${response.status}.`);
      }

      const config = await response.json();
      const json = JSON.stringify(config, null, 2);
      await copyTextToClipboard(json);
      showExportStatus(status, "Provider configuration copied to clipboard. API keys were not included.", false);
    } catch (error) {
      showExportStatus(status, error.message || "Provider configuration export failed.", true);
    } finally {
      button.disabled = false;
      button.textContent = originalText;
    }
  });
}

async function copyTextToClipboard(text) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const textArea = document.createElement("textarea");
  textArea.value = text;
  textArea.setAttribute("readonly", "readonly");
  textArea.style.position = "fixed";
  textArea.style.left = "-9999px";
  document.body.appendChild(textArea);
  textArea.select();
  document.execCommand("copy");
  document.body.removeChild(textArea);
}

function showExportStatus(status, message, isError) {
  if (!status) {
    return;
  }

  status.hidden = false;
  status.classList.toggle("alert-success", !isError);
  status.classList.toggle("alert-danger", isError);
  status.textContent = message;
}

function wireCompareProgress() {
  const form = document.querySelector("[data-progress-form='compare']");
  if (!form || !window.ReadableStream) {
    return;
  }

  form.addEventListener("submit", async event => {
    if (!formIsValid(form)) {
      return;
    }

    const checkedProviders = Array.from(form.querySelectorAll("input[name='SelectedProviderIds']:checked"));
    if (checkedProviders.length === 0) {
      return;
    }

    event.preventDefault();
    if (form.dataset.submitting === "true") {
      return;
    }

    form.dataset.submitting = "true";
    const formData = new FormData(form);
    const progress = createProgressView(form, checkedProviders);
    setFormBusy(form, true);

    try {
      const response = await fetch(form.dataset.progressUrl, {
        method: "POST",
        body: formData,
        headers: { "X-Requested-With": "fetch" }
      });

      const contentType = response.headers.get("content-type") || "";
      if (!contentType.includes("application/x-ndjson")) {
        throw new Error(`The server returned HTTP ${response.status} instead of a progress stream.`);
      }

      if (!response.body) {
        throw new Error(`Request failed with HTTP ${response.status}.`);
      }

      await readProgressStream(response, progress);
    } catch (error) {
      progress.setStatus("The request did not complete.", error.message || "Unexpected error.");
      setFormBusy(form, false);
      form.dataset.submitting = "false";
    }
  });
}

function formIsValid(form) {
  if (window.jQuery && window.jQuery.fn && typeof window.jQuery.fn.valid === "function") {
    return window.jQuery(form).valid();
  }

  return form.checkValidity();
}

function createProgressView(form, checkedProviders) {
  const panel = form.querySelector("[data-submit-progress]");
  const status = form.querySelector("[data-progress-status]");
  const detail = form.querySelector("[data-progress-detail]");
  const list = form.querySelector("[data-progress-list]");
  const providers = checkedProviders.map(input => ({
    id: input.value,
    name: input.dataset.providerName || input.value
  }));
  const providerItems = new Map();

  panel.hidden = false;
  list.replaceChildren();

  providers.forEach(provider => {
    const item = document.createElement("li");
    item.dataset.providerId = provider.id;
    item.innerHTML = `<span class="progress-check pending" aria-hidden="true"></span><span>${escapeHtml(provider.name)}</span><small>Waiting...</small>`;
    providerItems.set(provider.id, item);
    list.appendChild(item);
  });

  return {
    setStatus(message, details) {
      status.textContent = message;
      detail.textContent = details || "";
    },
    markCompleted(message) {
      const item = providerItems.get(String(message.providerId));
      if (!item) {
        return;
      }

      const indicator = item.querySelector(".progress-check");
      const note = item.querySelector("small");
      indicator.classList.remove("pending", "success", "failed");
      indicator.classList.add(message.succeeded ? "success" : "failed");
      indicator.textContent = message.succeeded ? "\u2713" : "X";
      note.textContent = message.succeeded
        ? `Completed in ${message.durationMs} ms`
        : `Failed in ${message.durationMs} ms`;
    }
  };
}

async function readProgressStream(response, progress) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }

    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() || "";

    for (const line of lines) {
      if (line.trim().length === 0) {
        continue;
      }

      handleProgressMessage(JSON.parse(line), progress);
    }
  }
}

function handleProgressMessage(message, progress) {
  switch (message.type) {
    case "started":
      progress.setStatus(`Submitting to ${message.total} selected AI tools...`, "Waiting for provider responses.");
      break;
    case "providerCompleted":
      progress.markCompleted(message);
      progress.setStatus(`${message.providerName} ${message.succeeded ? "completed" : "failed"}.`, "Waiting on any remaining providers...");
      break;
    case "complete":
      progress.setStatus("All selected providers finished.", "Opening results...");
      window.location.assign(message.redirectUrl);
      break;
    case "error":
      throw new Error(message.message);
  }
}

function setFormBusy(form, isBusy) {
  const submitButton = form.querySelector("button[type='submit']");
  if (submitButton) {
    submitButton.disabled = isBusy;
    submitButton.textContent = isBusy ? "Working..." : "Submit to selected tools";
  }
}

function escapeHtml(value) {
  const div = document.createElement("div");
  div.textContent = value;
  return div.innerHTML;
}

window.AIToolbox = window.AIToolbox || {};
window.AIToolbox.openReadOnlyContentModal = openReadOnlyContentModal;

function wireReadOnlyContentModalTriggers() {
  document.querySelectorAll("[data-readonly-modal-trigger]").forEach(button => {
    button.addEventListener("click", () => {
      const source = button.dataset.readonlyContentSource
        ? document.getElementById(button.dataset.readonlyContentSource)
        : null;
      const content = source?.textContent || button.dataset.readonlyContent || "";
      openReadOnlyContentModal({
        title: button.dataset.readonlyTitle || "Preview",
        sections: [{ content }]
      });
    });
  });
}

function openReadOnlyContentModal(options) {
  const modal = getReadOnlyContentModal();
  const title = modal.querySelector("[data-readonly-modal-title]");
  const body = modal.querySelector("[data-readonly-modal-body]");
  const sections = Array.isArray(options?.sections) ? options.sections : [];

  title.textContent = options?.title || "Preview";
  body.replaceChildren(...sections.map(createReadOnlyModalSection));

  const instance = bootstrap.Modal.getOrCreateInstance(modal);
  instance.show();
}

function getReadOnlyContentModal() {
  const existing = document.getElementById("readonly-content-modal");
  if (existing) {
    return existing;
  }

  const wrapper = document.createElement("div");
  wrapper.className = "modal fade";
  wrapper.id = "readonly-content-modal";
  wrapper.tabIndex = -1;
  wrapper.setAttribute("aria-labelledby", "readonly-content-modal-title");
  wrapper.setAttribute("aria-hidden", "true");
  wrapper.innerHTML = `
    <div class="modal-dialog modal-xl modal-dialog-scrollable readonly-content-dialog">
      <div class="modal-content readonly-content-modal">
        <div class="modal-header">
          <h2 class="modal-title" id="readonly-content-modal-title" data-readonly-modal-title>Preview</h2>
          <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
        </div>
        <div class="modal-body" data-readonly-modal-body></div>
        <div class="modal-footer">
          <button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Close</button>
        </div>
      </div>
    </div>`;
  document.body.appendChild(wrapper);
  return wrapper;
}

function createReadOnlyModalSection(section) {
  const container = document.createElement("section");
  container.className = "readonly-content-section";

  if (section.heading) {
    const heading = document.createElement("h3");
    heading.textContent = section.heading;
    container.appendChild(heading);
  }

  const content = document.createElement("pre");
  content.className = "readonly-content-block";
  content.textContent = section.content || "";
  container.appendChild(content);

  return container;
}

function wireProductParserDirectivePreview() {
  const button = document.querySelector("[data-directive-preview-button]");
  const selector = document.querySelector("[data-directive-selector]");
  const payload = document.getElementById("product-parser-directives");
  if (!button || !selector || !payload) {
    return;
  }

  let directives = [];
  try {
    directives = JSON.parse(payload.textContent || "[]");
  } catch {
    directives = [];
  }

  const byId = new Map(directives.map(directive => [String(directive.id), directive]));
  const updateButton = () => {
    button.disabled = !byId.has(selector.value);
  };

  selector.addEventListener("change", updateButton);
  button.addEventListener("click", () => {
    const directive = byId.get(selector.value);
    if (!directive) {
      return;
    }

    openReadOnlyContentModal({
      title: `${directive.name} v${directive.version}`,
      sections: [
        {
          heading: "System Prompt",
          content: directive.systemPrompt || "No system prompt is available."
        },
        {
          heading: "Target Schema JSON",
          content: directive.targetSchemaJson || "No target schema is available."
        }
      ]
    });
  });

  updateButton();
}

function wirePdfDropZone() {
  const form = document.querySelector("[data-pdf-drop-form]");
  if (!form) {
    return;
  }

  const dropZone = form.querySelector("[data-pdf-drop-zone]");
  const input = form.querySelector("[data-pdf-file-input]");
  const browse = form.querySelector("[data-pdf-browse]");
  const list = form.querySelector("[data-pdf-file-list]");
  const submit = form.querySelector("[data-pdf-submit]");

  browse?.addEventListener("click", () => input.click());

  input.addEventListener("change", () => renderPdfFileList(input.files, list, submit));

  dropZone.addEventListener("dragover", event => {
    event.preventDefault();
    dropZone.classList.add("dragging");
  });

  dropZone.addEventListener("dragleave", () => {
    dropZone.classList.remove("dragging");
  });

  dropZone.addEventListener("drop", event => {
    event.preventDefault();
    dropZone.classList.remove("dragging");

    const dropped = Array.from(event.dataTransfer?.files || [])
      .filter(file => file.type === "application/pdf" || file.name.toLowerCase().endsWith(".pdf"));
    if (dropped.length === 0) {
      renderPdfFileList(input.files, list, submit, "Drop one or more PDF files.");
      return;
    }

    const transfer = new DataTransfer();
    dropped.forEach(file => transfer.items.add(file));
    input.files = transfer.files;
    renderPdfFileList(input.files, list, submit);
  });

  form.addEventListener("submit", () => {
    if (submit) {
      submit.disabled = true;
      submit.textContent = "Job submitted";
    }
    renderPdfFileList(input.files, list, submit, null, "Queued for server-side parsing");
  });
}

function renderPdfFileList(files, list, submit, message, statusText) {
  const selected = Array.from(files || []);
  if (message) {
    list.innerHTML = `<p class="text-danger mb-0">${escapeHtml(message)}</p>`;
    if (submit) {
      submit.disabled = true;
    }
    return;
  }

  if (selected.length === 0) {
    list.innerHTML = '<p class="text-muted mb-0">No PDFs selected yet.</p>';
    if (submit) {
      submit.disabled = true;
    }
    return;
  }

  const rows = selected.map(file => `
    <li>
      <span>${escapeHtml(file.name)}</span>
      <small>${formatBytes(file.size)} · ${escapeHtml(statusText || "Ready to parse")}</small>
    </li>`).join("");
  list.innerHTML = `<ul class="pdf-ready-list">${rows}</ul>`;
  if (submit) {
    submit.disabled = false;
  }
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function wireJobsMenu() {
  const menu = document.querySelector("[data-jobs-menu]");
  if (!menu) {
    return;
  }

  const count = menu.querySelector("[data-jobs-count]");
  const completedCount = menu.querySelector("[data-jobs-completed-count]");
  const list = menu.querySelector("[data-jobs-list]");
  const empty = menu.querySelector("[data-jobs-empty]");
  const completedPanel = menu.querySelector("[data-jobs-completed]");
  const completedList = menu.querySelector("[data-jobs-completed-list]");
  const clearCompleted = menu.querySelector("[data-jobs-clear-completed]");
  const notifyRow = menu.querySelector("[data-jobs-notify-row]");
  const enableNotifications = menu.querySelector("[data-jobs-enable-notifications]");
  const token = menu.querySelector("[data-jobs-token-form] input[name='__RequestVerificationToken']")?.value || "";
  let previousCount = 0;
  let previousJobs = new Map();
  let hasLoadedJobs = false;
  let completedJobs = loadCompletedJobs();

  renderCompletedJobs(completedJobs, completedPanel, completedList, completedCount);
  wireJobNotifications(notifyRow, enableNotifications);
  clearCompleted?.addEventListener("click", () => {
    completedJobs = [];
    saveCompletedJobs(completedJobs);
    renderCompletedJobs(completedJobs, completedPanel, completedList, completedCount);
  });

  const refresh = async () => {
    try {
      const response = await fetch(menu.dataset.jobsUrl, {
        headers: { Accept: "application/json" }
      });
      if (!response.ok) {
        return;
      }

      const payload = await response.json();
      const jobs = Array.isArray(payload.jobs) ? payload.jobs : [];
      const nextCount = Number(payload.count || jobs.length || 0);
      const nextJobs = new Map(jobs.map(job => [String(job.id), job]));

      if (hasLoadedJobs) {
        const finished = Array.from(previousJobs.entries())
          .filter(([id]) => !nextJobs.has(id))
          .map(([, job]) => job);
        if (finished.length > 0) {
          completedJobs = addCompletedJobs(completedJobs, finished);
          saveCompletedJobs(completedJobs);
          renderCompletedJobs(completedJobs, completedPanel, completedList, completedCount);
          finished.forEach(showJobFinishedNotification);
        }
      }

      count.hidden = nextCount === 0;
      count.textContent = String(nextCount);
      empty.hidden = nextCount !== 0 || completedJobs.length > 0;
      list.replaceChildren(...jobs.map(job => createJobMenuItem(menu, job, token, refresh)));
      updateJobElapsedLabels(list);

      if (nextCount !== previousCount) {
        menu.classList.remove("jobs-menu-pulse");
        window.requestAnimationFrame(() => menu.classList.add("jobs-menu-pulse"));
      }

      previousCount = nextCount;
      previousJobs = nextJobs;
      hasLoadedJobs = true;
    } catch {
      // Keep nav polling quiet; server logs carry the diagnostic detail.
    }
  };

  refresh();
  window.setInterval(refresh, 10000);
  window.setInterval(() => updateJobElapsedLabels(list), 1000);
}

function wireJobNotifications(notifyRow, enableNotifications) {
  if (!notifyRow || !enableNotifications || !("Notification" in window) || !window.isSecureContext) {
    return;
  }

  notifyRow.hidden = Notification.permission !== "default";
  enableNotifications.addEventListener("click", async event => {
    event.stopPropagation();
    const permission = await Notification.requestPermission();
    notifyRow.hidden = permission !== "default";
  });
}

function showJobFinishedNotification(job) {
  if (!("Notification" in window) || Notification.permission !== "granted") {
    return;
  }

  const title = "AI Toolbox job finished";
  const elapsedSeconds = job.elapsedSeconds || getJobElapsedSeconds(job);
  const duration = elapsedSeconds ? ` after ${formatDurationSeconds(elapsedSeconds)}` : "";
  const body = job.title ? `${job.title} finished${duration}.` : `A job finished${duration}.`;
  new Notification(title, { body });
}

function loadCompletedJobs() {
  try {
    const raw = window.localStorage?.getItem("AIToolbox.CompletedJobs");
    return raw ? JSON.parse(raw) : [];
  } catch {
    return [];
  }
}

function saveCompletedJobs(jobs) {
  try {
    window.localStorage?.setItem("AIToolbox.CompletedJobs", JSON.stringify(jobs.slice(0, 10)));
  } catch {
    // Noncritical UI memory only.
  }
}

function addCompletedJobs(existing, finished) {
  const next = [...existing];
  finished.forEach(job => {
    const id = String(job.id);
    if (next.some(item => String(item.id) === id)) {
      return;
    }

    next.unshift({
      id,
      title: job.title || `Job #${job.id}`,
      elapsedSeconds: getJobElapsedSeconds(job),
      finishedUtc: new Date().toISOString()
    });
  });
  return next.slice(0, 10);
}

function renderCompletedJobs(jobs, panel, list, count) {
  if (count) {
    count.hidden = jobs.length === 0;
    count.textContent = `${jobs.length} done`;
  }

  if (!panel || !list) {
    return;
  }

  panel.hidden = jobs.length === 0;
  list.replaceChildren(...jobs.map(createCompletedJobMenuItem));
}

function createCompletedJobMenuItem(job) {
  const item = document.createElement("div");
  item.className = "jobs-menu-item completed";

  const details = document.createElement("div");
  const title = document.createElement("strong");
  title.textContent = job.title || `Job #${job.id}`;
  const meta = document.createElement("small");
  const duration = job.elapsedSeconds ? ` in ${formatDurationSeconds(job.elapsedSeconds)}` : "";
  meta.textContent = `${formatFinishedAge(job.finishedUtc)}${duration}`;
  details.append(title, meta);
  item.appendChild(details);

  return item;
}

function createJobMenuItem(menu, job, token, refresh) {
  const item = document.createElement("div");
  item.className = "jobs-menu-item";

  const details = document.createElement("div");
  const title = document.createElement("strong");
  title.textContent = job.title || `Job #${job.id}`;
  const meta = document.createElement("small");
  meta.dataset.jobElapsed = "true";
  meta.dataset.jobStatus = job.status || "Processing";
  meta.dataset.jobStartedUtc = job.startedUtc || "";
  meta.dataset.jobCreatedUtc = job.createdUtc || "";
  meta.textContent = formatJobElapsedMeta(job);
  details.append(title, meta);
  item.appendChild(details);

  if (job.canCancel) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "btn btn-sm btn-outline-danger";
    button.textContent = "Cancel";
    button.addEventListener("click", async event => {
      event.stopPropagation();
      button.disabled = true;
      button.textContent = "Cancelling";
      const form = new FormData();
      form.append("__RequestVerificationToken", token);
      const url = (menu.dataset.cancelUrlTemplate || "").replace("__id__", encodeURIComponent(job.id));
      await fetch(url, {
        method: "POST",
        body: form,
        headers: { "X-Requested-With": "fetch" }
      });
      await refresh();
    });
    item.appendChild(button);
  }

  return item;
}

function updateJobElapsedLabels(scope) {
  scope.querySelectorAll("[data-job-elapsed]").forEach(element => {
    element.textContent = formatJobElapsedMeta({
      status: element.dataset.jobStatus,
      startedUtc: element.dataset.jobStartedUtc,
      createdUtc: element.dataset.jobCreatedUtc
    });
  });
}

function formatJobElapsedMeta(job) {
  const status = job.status || "Processing";
  const startedUtc = job.startedUtc || "";
  const createdUtc = job.createdUtc || "";
  const baseline = startedUtc || createdUtc;
  const labelPrefix = startedUtc ? "elapsed" : "queued";
  const elapsed = formatElapsedSince(baseline);
  return `${status} · ${elapsed} ${labelPrefix}`;
}

function formatElapsedSince(value) {
  const start = Date.parse(value);
  if (!Number.isFinite(start)) {
    return "0s";
  }

  const totalSeconds = Math.max(0, Math.floor((Date.now() - start) / 1000));
  return formatDurationSeconds(totalSeconds);
}

function getJobElapsedSeconds(job) {
  const baseline = job.startedUtc || job.createdUtc;
  const start = Date.parse(baseline);
  if (!Number.isFinite(start)) {
    return null;
  }

  return Math.max(0, Math.floor((Date.now() - start) / 1000));
}

function formatDurationSeconds(totalSeconds) {
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return `${hours} hr ${minutes} min`;
  }

  if (minutes > 0) {
    return `${minutes} min ${seconds} sec`;
  }

  return `${seconds} sec`;
}

function formatJobAge(value) {
  const created = Date.parse(value);
  if (!Number.isFinite(created)) {
    return "recently submitted";
  }

  const seconds = Math.max(0, Math.floor((Date.now() - created) / 1000));
  if (seconds < 60) {
    return "just now";
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes} min ago`;
  }

  const hours = Math.floor(minutes / 60);
  return `${hours} hr ago`;
}

function formatFinishedAge(value) {
  const age = formatJobAge(value);
  return age === "just now" || age === "recently submitted"
    ? "Finished just now"
    : `Finished ${age}`;
}

function wireProductDetailControls() {
  document.querySelectorAll("[data-copy-neighbor]").forEach(button => {
    button.addEventListener("click", async () => {
      const input = button.parentElement?.querySelector("input, textarea");
      if (!input) {
        return;
      }

      await navigator.clipboard.writeText(input.value);
      const original = button.textContent;
      button.textContent = "Copied";
      window.setTimeout(() => {
        button.textContent = original;
      }, 900);
    });
  });

  document.querySelectorAll("[data-array-format]").forEach(button => {
    button.addEventListener("click", () => {
      const section = button.closest(".product-detail-section");
      const output = section?.querySelector("[data-array-output]");
      if (!output) {
        return;
      }

      const format = button.dataset.arrayFormat;
      output.value = output.dataset[format] || "";
      section.querySelectorAll("[data-array-format]").forEach(item => {
        item.classList.toggle("btn-primary", item === button);
        item.classList.toggle("btn-outline-secondary", item !== button);
      });
    });
  });
}

function wireSortableTables() {
  document.querySelectorAll("[data-sortable-table]").forEach(table => {
    table.querySelectorAll("[data-sort-column]").forEach(button => {
      button.addEventListener("click", () => {
        const column = Number(button.dataset.sortColumn);
        if (!Number.isInteger(column)) {
          return;
        }

        const direction = button.dataset.sortDirection === "asc" ? "desc" : "asc";
        table.querySelectorAll("[data-sort-column]").forEach(item => {
          item.dataset.sortDirection = "";
          item.removeAttribute("aria-sort");
        });
        button.dataset.sortDirection = direction;
        button.setAttribute("aria-sort", direction === "asc" ? "ascending" : "descending");
        sortTableRows(table, column, button.dataset.sortType || "text", direction);
      });
    });
  });
}

function sortTableRows(table, column, type, direction) {
  const tbody = table.tBodies[0];
  if (!tbody) {
    return;
  }

  const groups = [];
  let current = null;
  Array.from(tbody.rows).forEach(row => {
    if (row.hasAttribute("data-sort-row")) {
      current = { row, attached: [] };
      groups.push(current);
      return;
    }

    if (current) {
      current.attached.push(row);
    }
  });

  const multiplier = direction === "asc" ? 1 : -1;
  groups.sort((left, right) => compareSortValues(
    getSortValue(left.row, column),
    getSortValue(right.row, column),
    type) * multiplier);

  groups.forEach(group => {
    tbody.appendChild(group.row);
    group.attached.forEach(row => tbody.appendChild(row));
  });
}

function getSortValue(row, column) {
  const cell = row.cells[column];
  return cell?.dataset.sortValue ?? cell?.textContent ?? "";
}

function compareSortValues(left, right, type) {
  if (type === "number") {
    return (Number.parseFloat(left) || 0) - (Number.parseFloat(right) || 0);
  }

  if (type === "date") {
    return (Date.parse(left) || 0) - (Date.parse(right) || 0);
  }

  return String(left).localeCompare(String(right), undefined, { numeric: true, sensitivity: "base" });
}
