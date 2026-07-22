import { getSettings, saveSettings } from "./settings";

const apiBaseUrl = document.querySelector("#apiBaseUrl") as HTMLInputElement;
const clientId = document.querySelector("#clientId") as HTMLInputElement;
const authority = document.querySelector("#authority") as HTMLInputElement;
const scope = document.querySelector("#scope") as HTMLInputElement;
const status = document.querySelector("#optionsStatus") as HTMLElement;
const adminButton = document.querySelector("#adminButton") as HTMLButtonElement;

void init();

async function init(): Promise<void> {
  const settings = await getSettings();
  apiBaseUrl.value = settings.apiBaseUrl;
  clientId.value = settings.clientId;
  authority.value = settings.authority;
  scope.value = settings.scope;
  document.querySelector("#saveButton")?.addEventListener("click", save);
  await checkAdmin();
}

async function save(): Promise<void> {
  await saveSettings({
    apiBaseUrl: apiBaseUrl.value.replace(/\/$/, ""),
    clientId: clientId.value,
    authority: authority.value,
    scope: scope.value
  });
  status.textContent = "Saved.";
}

async function checkAdmin(): Promise<void> {
  try {
    const tokenResponse = await chrome.runtime.sendMessage({ type: "getToken" });
    if (tokenResponse.error) {
      return;
    }

    const settings = await getSettings();
    const response = await fetch(`${settings.apiBaseUrl}/api/v1/me`, {
      headers: { Authorization: `Bearer ${tokenResponse.token}` }
    });
    if (!response.ok) {
      return;
    }

    const me = await response.json();
    if (me.role === "Admin") {
      adminButton.classList.remove("hidden");
      adminButton.addEventListener("click", () => chrome.tabs.create({ url: `${settings.apiBaseUrl}/Admin` }));
    }
  } catch {
    // Options remains usable when the user is not signed in.
  }
}
