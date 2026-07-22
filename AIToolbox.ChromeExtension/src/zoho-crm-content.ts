type ParsedProductPayload = {
  id?: number;
  productDataJson?: string;
  productName?: string;
  productCode?: string;
  supplier?: string;
  manufacturer?: string;
  category?: string;
};

type ImportValue = {
  label: string;
  value: string;
  fieldIds: string[];
  labelAliases: string[];
  textArea?: boolean;
};

const buttonClass = "aitoolbox-import-button";
const buttonIconClass = "aitoolbox-import-icon";
const statusClass = "aitoolbox-import-status";

injectStyle();
injectImportButtons();
new MutationObserver(() => injectImportButtons()).observe(document.documentElement, {
  childList: true,
  subtree: true
});
document.addEventListener("pointerup", handleImportButtonPointerUp, true);
document.addEventListener("keydown", handleImportButtonKeyDown, true);

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type !== "AIT_IMPORT_PRODUCT") {
    return false;
  }

  importProduct(message.product as ParsedProductPayload)
    .then(result => sendResponse(result))
    .catch(error => sendResponse({ error: error instanceof Error ? error.message : String(error) }));
  return true;
});

function injectImportButtons(): void {
  const containers = [
    document.querySelector("#crm_create_savebutnProducts")?.closest("crm-create-buttons"),
    document.querySelector("#qc_Products_footerbuttons crm-create-buttons"),
    document.querySelector("#quickCreateSaveAndAssociateProducts")?.closest("crm-create-buttons")
  ].filter((container): container is Element => Boolean(container));

  for (const container of containers) {
    if (container.querySelector(`.${buttonClass}`)) {
      continue;
    }

    const button = document.createElement("button");
    button.type = "button";
    button.className = buttonClass;
    button.innerHTML = `
      <img class="${buttonIconClass}" src="${chrome.runtime.getURL("icons/icon-32.png")}" alt="" aria-hidden="true">
      <span>Toolbox Import</span>
    `;
    const icon = button.querySelector<HTMLImageElement>(`.${buttonIconClass}`);
    const label = button.querySelector("span");
    icon?.addEventListener("error", () => {
      icon.remove();
      if (label) {
        label.textContent = "AI Toolbox Import";
      }
    }, { once: true });

    const anchor = container.querySelector("#crm_create_cancelbutnProducts, #quickCreateCancelProducts");
    if (anchor) {
      anchor.insertAdjacentElement("beforebegin", button);
    } else {
      container.prepend(button);
    }
  }
}

function handleImportButtonPointerUp(event: PointerEvent): void {
  const button = findImportButton(event.target);
  if (!button) {
    return;
  }

  event.preventDefault();
  event.stopImmediatePropagation();
  void openProductImport(button);
}

function handleImportButtonKeyDown(event: KeyboardEvent): void {
  if (event.key !== "Enter" && event.key !== " ") {
    return;
  }

  const button = findImportButton(event.target);
  if (!button) {
    return;
  }

  event.preventDefault();
  event.stopImmediatePropagation();
  void openProductImport(button);
}

function findImportButton(target: EventTarget | null): HTMLButtonElement | null {
  return target instanceof Element ? target.closest<HTMLButtonElement>(`.${buttonClass}`) : null;
}

async function openProductImport(button: HTMLElement): Promise<void> {
  if (button.dataset.aitoolboxOpening === "true") {
    return;
  }

  button.dataset.aitoolboxOpening = "true";
  button.setAttribute("disabled", "true");
  try {
    const response = await chrome.runtime.sendMessage({ type: "openProductImport" });
    if (response?.error) {
      throw new Error(response.error);
    }
  } catch (error) {
    // Keep the working path quiet; only show a message if Chrome blocks the extension open.
    setInlineStatus(button, error instanceof Error ? error.message : String(error), true);
  } finally {
    button.removeAttribute("disabled");
    window.setTimeout(() => {
      delete button.dataset.aitoolboxOpening;
    }, 350);
  }
}

async function importProduct(product: ParsedProductPayload): Promise<{ message: string; populated: string[]; skipped: string[] }> {
  const values = mapProductValues(product);
  const populated: string[] = [];
  const skipped: string[] = [];

  for (const value of values) {
    if (!value.value) {
      skipped.push(value.label);
      continue;
    }

    const field = findTextField(value);
    if (!field) {
      skipped.push(value.label);
      continue;
    }

    setFormValue(field, value.value);
    populated.push(value.label);
  }

  const category = product.category || findProductValue(product, ["productCategory", "category", "productType"]);
  if (category) {
    const selected = await selectPicklistValue("Product Category", category);
    (selected ? populated : skipped).push("Product Category");
  }

  const message = populated.length
    ? `Imported ${populated.join(", ")}.${skipped.length ? ` Skipped ${skipped.join(", ")}.` : ""}`
    : "No matching Zoho fields were found.";
  return { message, populated, skipped };
}

function mapProductValues(product: ParsedProductPayload): ImportValue[] {
  const productName = product.productName || findProductValue(product, [
    "productName",
    "name",
    "descriptionTitle",
    "title"
  ]);
  const productCode = product.productCode || findProductValue(product, [
    "productCode",
    "code",
    "modelNumber",
    "model",
    "sku",
    "catalogNumber"
  ]);

  return [
    {
      label: "Product Name",
      value: productName || productCode || "",
      fieldIds: ["PRODUCTNAME"],
      labelAliases: ["Product Name"]
    },
    {
      label: "Product Code",
      value: productCode || "",
      fieldIds: ["PRODUCTCODE"],
      labelAliases: ["Product Code", "Product Code/SKU", "SKU"]
    },
    {
      label: "Unit Price",
      value: normalizeMoney(findProductValue(product, ["unitPrice", "price", "listPrice", "sellPrice", "sellingPrice"])),
      fieldIds: ["UNITPRICE"],
      labelAliases: ["Unit Price", "Unit Price ($)", "List Price"]
    },
    {
      label: "Cost",
      value: normalizeMoney(findProductValue(product, ["cost", "unitCost", "productCost", "netCost"])),
      fieldIds: ["COST", "PRODUCTCOST", "PRODUCTCF3"],
      labelAliases: ["Cost", "Unit Cost", "Product Cost"]
    },
    {
      label: "Description",
      value: findProductValue(product, ["description", "productDescription", "longDescription", "summary", "notes"]),
      fieldIds: ["DESCRIPTION"],
      labelAliases: ["Description"],
      textArea: true
    },
    {
      label: "Product Tags",
      value: findTags(product),
      fieldIds: ["PRODUCTCF1"],
      labelAliases: ["Product Tags", "Tags"],
      textArea: true
    }
  ];
}

function findTextField(value: ImportValue): HTMLInputElement | HTMLTextAreaElement | null {
  for (const fieldId of value.fieldIds) {
    const selectors = [
      `#Crm_Products_${fieldId}_LInput`,
      `#Products_fldRow_${fieldId} ${value.textArea ? "textarea" : "input"}`,
      `crm-create-fields[fld-id-partial-val="Products_${fieldId}"] ${value.textArea ? "textarea" : "input"}`,
      `crm-create-fields[fld-id*="${fieldId}"] ${value.textArea ? "textarea" : "input"}`,
      `[data-zcqa="${fieldId}"] ${value.textArea ? "textarea" : "input"}`,
      `${value.textArea ? "textarea" : "input"}[aria-labelledby*="${fieldId}"]`
    ];
    const field = queryFirst<HTMLInputElement | HTMLTextAreaElement>(selectors);
    if (field) {
      return field;
    }
  }

  for (const alias of value.labelAliases) {
    const field = findFieldByLabel(alias, value.textArea);
    if (field) {
      return field;
    }
  }

  return null;
}

function findFieldByLabel(label: string, textArea = false): HTMLInputElement | HTMLTextAreaElement | null {
  const wanted = normalizeLabel(label);
  const labels = Array.from(document.querySelectorAll<HTMLElement>(".customfieldLabel, label, [id$='_label']"));
  const labelElement = labels.find(element => normalizeLabel(element.textContent || "") === wanted);
  const row = labelElement?.closest("crm-create-fields, .custabDivCreate, tr, .form-group");
  return row?.querySelector<HTMLInputElement | HTMLTextAreaElement>(textArea ? "textarea" : "input, textarea") || null;
}

async function selectPicklistValue(label: string, value: string): Promise<boolean> {
  const row = findFieldContainerByLabel(label);
  const dropdown = row?.querySelector<HTMLElement>("lyte-dropdown, [role='combobox']");
  if (!dropdown) {
    return false;
  }

  dropdown.click();
  await delay(150);

  const wanted = normalizeLabel(value);
  const option = Array.from(document.querySelectorAll<HTMLElement>("lyte-drop-item, [role='option']"))
    .find(item => normalizeLabel(item.textContent || "") === wanted);
  if (!option) {
    document.body.click();
    return false;
  }

  option.click();
  return true;
}

function findFieldContainerByLabel(label: string): Element | null {
  const wanted = normalizeLabel(label);
  const labels = Array.from(document.querySelectorAll<HTMLElement>(".customfieldLabel, label, [id$='_label']"));
  const labelElement = labels.find(element => normalizeLabel(element.textContent || "") === wanted);
  return labelElement?.closest("crm-create-fields, .custabDivCreate, tr, .form-group") || null;
}

function setFormValue(field: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  field.focus();
  const prototype = field instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
  setter?.call(field, value);
  field.value = value;
  field.dispatchEvent(new Event("input", { bubbles: true }));
  field.dispatchEvent(new Event("change", { bubbles: true }));
  field.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true }));
  field.blur();
}

function findProductValue(product: ParsedProductPayload, keys: string[]): string {
  const data = parseProductData(product.productDataJson);
  const normalizedKeys = keys.map(normalizeKey);
  const exact = findDeepValue(data, normalizedKeys);
  return valueToText(exact);
}

function findDeepValue(value: unknown, normalizedKeys: string[], depth = 0): unknown {
  if (!value || depth > 6) {
    return undefined;
  }

  if (Array.isArray(value)) {
    for (const item of value) {
      const found = findDeepValue(item, normalizedKeys, depth + 1);
      if (found !== undefined) {
        return found;
      }
    }
    return undefined;
  }

  if (typeof value !== "object") {
    return undefined;
  }

  const entries = Object.entries(value as Record<string, unknown>);
  for (const [key, childValue] of entries) {
    if (normalizedKeys.includes(normalizeKey(key)) && childValue !== null && childValue !== undefined && childValue !== "") {
      return childValue;
    }
  }

  for (const [, childValue] of entries) {
    const found = findDeepValue(childValue, normalizedKeys, depth + 1);
    if (found !== undefined) {
      return found;
    }
  }

  return undefined;
}

function findTags(product: ParsedProductPayload): string {
  const explicit = findProductValue(product, ["productTags", "tags", "keywords"]);
  if (explicit) {
    return explicit;
  }

  return [
    product.productCode,
    product.productName,
    product.manufacturer,
    product.supplier,
    product.category
  ].filter(Boolean).join(", ");
}

function parseProductData(json: string | undefined): Record<string, unknown> {
  try {
    const parsed = JSON.parse(json || "{}");
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function valueToText(value: unknown): string {
  if (value === null || value === undefined) {
    return "";
  }

  if (typeof value === "string") {
    return value.trim();
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  if (Array.isArray(value)) {
    return value.map(valueToText).filter(Boolean).join(", ");
  }

  if (typeof value === "object") {
    return Object.entries(value as Record<string, unknown>)
      .map(([key, childValue]) => `${toLabel(key)}: ${valueToText(childValue)}`)
      .filter(line => !line.endsWith(": "))
      .join("\n");
  }

  return String(value);
}

function normalizeMoney(value: string): string {
  const match = value.match(/-?\d[\d,]*(?:\.\d+)?/);
  return match ? match[0].replace(/,/g, "") : value;
}

function queryFirst<T extends Element>(selectors: string[]): T | null {
  for (const selector of selectors) {
    const element = document.querySelector<T>(selector);
    if (element) {
      return element;
    }
  }

  return null;
}

function setInlineStatus(anchor: HTMLElement, message: string, isError = false): void {
  let status = anchor.parentElement?.querySelector<HTMLElement>(`.${statusClass}`);
  if (!status) {
    status = document.createElement("span");
    status.className = statusClass;
    anchor.insertAdjacentElement("afterend", status);
  }
  status.textContent = message;
  status.classList.toggle("error", isError);
}

function showPageStatus(message: string, isError = false): void {
  const button = document.querySelector<HTMLElement>(`.${buttonClass}`);
  if (button) {
    setInlineStatus(button, message, isError);
  }
}

function injectStyle(): void {
  if (document.querySelector("#aitoolbox-zoho-style")) {
    return;
  }

  const style = document.createElement("style");
  style.id = "aitoolbox-zoho-style";
  style.textContent = `
    .${buttonClass} {
      background: #182026;
      border: 1px solid #182026;
      border-radius: 4px;
      color: #fff;
      cursor: pointer;
      display: inline-flex;
      align-items: center;
      font: 13px Arial, sans-serif;
      min-height: 30px;
      margin: 0 8px;
      padding: 6px 12px;
      vertical-align: middle;
      white-space: nowrap;
    }
    .${buttonIconClass} {
      display: block;
      flex: 0 0 auto;
      height: 16px;
      margin-right: 7px;
      width: 16px;
    }
    .${buttonClass}:hover {
      background: #2f3d48;
      border-color: #2f3d48;
    }
    .${buttonClass}:disabled {
      cursor: default;
      opacity: 0.6;
    }
    .${statusClass} {
      color: #40505c;
      display: inline-flex;
      font: 12px Arial, sans-serif;
      margin-left: 4px;
      max-width: 320px;
      overflow-wrap: anywhere;
      vertical-align: middle;
    }
    .${statusClass}.error {
      color: #b42318;
    }
  `;
  document.head.append(style);
}

function normalizeKey(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]/g, "");
}

function normalizeLabel(value: string): string {
  return value.toLowerCase().replace(/\s+/g, " ").replace(/\*/g, "").trim();
}

function toLabel(value: string): string {
  return value
    .replace(/[_-]+/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/^./, first => first.toUpperCase());
}

function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => window.setTimeout(resolve, milliseconds));
}
