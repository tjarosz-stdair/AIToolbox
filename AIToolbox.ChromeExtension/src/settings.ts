import type { ExtensionSettings } from "./types";

export const defaultSettings: ExtensionSettings = {
  apiBaseUrl: "https://aitoolbox.stdair.com",
  clientId: "20f3c88a-4988-4ab9-b2c7-e5fcf80e10d5",
  authority: "https://login.microsoftonline.com/common",
  scope: "api://20f3c88a-4988-4ab9-b2c7-e5fcf80e10d5/AIToolbox.Access"
};

export async function getSettings(): Promise<ExtensionSettings> {
  const stored = await chrome.storage.local.get(defaultSettings);
  return {
    apiBaseUrl: normalizeApiBaseUrl(valueOrDefault(stored.apiBaseUrl, defaultSettings.apiBaseUrl)),
    clientId: valueOrDefault(stored.clientId, defaultSettings.clientId),
    authority: valueOrDefault(stored.authority, defaultSettings.authority).replace(/\/$/, ""),
    scope: valueOrDefault(stored.scope, defaultSettings.scope)
  };
}

export async function saveSettings(settings: ExtensionSettings): Promise<void> {
  await chrome.storage.local.set({
    apiBaseUrl: normalizeApiBaseUrl(valueOrDefault(settings.apiBaseUrl, defaultSettings.apiBaseUrl)),
    clientId: valueOrDefault(settings.clientId, defaultSettings.clientId),
    authority: valueOrDefault(settings.authority, defaultSettings.authority).replace(/\/$/, ""),
    scope: valueOrDefault(settings.scope, defaultSettings.scope)
  });
}

function valueOrDefault(value: unknown, fallback: string): string {
  return typeof value === "string" && value.trim() ? value.trim() : fallback;
}

function normalizeApiBaseUrl(value: string): string {
  const trimmed = value.replace(/\/$/, "");
  return trimmed === "https://aitools.stdair.com/aicompare" ? defaultSettings.apiBaseUrl : trimmed;
}
