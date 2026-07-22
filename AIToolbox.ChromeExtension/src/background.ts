import { getSettings } from "./settings";
import type { ActiveToolJob, JobsSnapshot } from "./types";

const zohoImportTargetKey = "zohoImportTargetTabId";
const zohoImportModeKey = "zohoImportModeRequested";
const authCacheKey = "authCache";
const jobsSnapshotKey = "jobsSnapshot";
const jobsAlarmName = "aitoolboxJobsPoll";
const emptyJobsSnapshot: JobsSnapshot = { count: 0, jobs: [] };

type AuthTokenResult = {
  token: string;
  accountName?: string;
  accountUsername?: string;
};

type AuthTokenCache = AuthTokenResult & {
  expiresAt?: number;
  refreshToken?: string;
};

async function getAccessToken(): Promise<AuthTokenResult> {
  const settings = await getSettings();
  if (!settings.clientId || !settings.scope) {
    throw new Error("Configure the extension options before signing in.");
  }

  const cached = await getCachedAccessToken();
  if (cached) {
    return cached;
  }

  const refreshed = await refreshAccessToken();
  if (refreshed) {
    return refreshed;
  }

  const redirectUri = chrome.identity.getRedirectURL();
  const verifier = base64UrlEncode(crypto.getRandomValues(new Uint8Array(32)));
  const challenge = await sha256Base64Url(verifier);
  const scopes = uniqueScopes([settings.scope, "openid", "profile", "email", "offline_access"]).join(" ");
  const authorizeUrl = new URL(`${settings.authority.replace(/\/$/, "")}/oauth2/v2.0/authorize`);
  authorizeUrl.searchParams.set("client_id", settings.clientId);
  authorizeUrl.searchParams.set("response_type", "code");
  authorizeUrl.searchParams.set("redirect_uri", redirectUri);
  authorizeUrl.searchParams.set("response_mode", "query");
  authorizeUrl.searchParams.set("scope", scopes);
  authorizeUrl.searchParams.set("prompt", "select_account");
  authorizeUrl.searchParams.set("code_challenge", challenge);
  authorizeUrl.searchParams.set("code_challenge_method", "S256");

  const redirectUrl = await chrome.identity.launchWebAuthFlow({ url: authorizeUrl.toString(), interactive: true });
  if (!redirectUrl) {
    throw new Error("Microsoft sign-in did not return a redirect URL.");
  }

  const redirect = new URL(redirectUrl);
  const code = redirect.searchParams.get("code");
  const error = redirect.searchParams.get("error_description")
    || redirect.searchParams.get("error");
  if (!code) {
    throw new Error(error || "Microsoft sign-in did not return an authorization code.");
  }

  const tokenResponse = await fetch(`${settings.authority.replace(/\/$/, "")}/oauth2/v2.0/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: settings.clientId,
      code,
      code_verifier: verifier,
      grant_type: "authorization_code",
      redirect_uri: redirectUri,
      scope: scopes
    })
  });
  const tokenJson = await tokenResponse.json();
  if (!tokenResponse.ok) {
    throw new Error(tokenJson.error_description || tokenJson.error || "Microsoft token request failed.");
  }

  const identity = parseJwtPayload(tokenJson.id_token);
  const result = {
    token: tokenJson.access_token as string,
    accountName: asString(identity?.name),
    accountUsername: asString(identity?.preferred_username || identity?.email || identity?.upn)
  };
  await saveAuthCache(tokenJson, result);
  void refreshJobs(false);
  return result;
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "getToken") {
    getAccessToken()
      .then(result => sendResponse(result))
      .catch(error => sendResponse({ error: error instanceof Error ? error.message : String(error) }));
    return true;
  }

  if (message?.type === "getAuthState") {
    getCurrentAuthState()
      .then(result => sendResponse({ signedIn: Boolean(result), accountName: result?.accountName, accountUsername: result?.accountUsername }))
      .catch(error => sendResponse({ error: error instanceof Error ? error.message : String(error) }));
    return true;
  }

  if (message?.type === "signOut") {
    signOut()
      .then(() => sendResponse({ ok: true }))
      .catch(error => sendResponse({ error: error instanceof Error ? error.message : String(error) }));
    return true;
  }

  if (message?.type === "openProductImport") {
    openProductImport(sender.tab?.id)
      .then(() => sendResponse({ ok: true }))
      .catch(error => sendResponse({ error: error instanceof Error ? error.message : String(error) }));
    return true;
  }

  if (message?.type === "getZohoImportTarget") {
    getZohoImportTarget()
      .then(target => sendResponse({ target }))
      .catch(error => sendResponse({ error: error instanceof Error ? error.message : String(error) }));
    return true;
  }

  if (message?.type === "getJobsSnapshot") {
    getStoredJobsSnapshot()
      .then(snapshot => sendResponse(snapshot))
      .catch(error => sendResponse({ ...emptyJobsSnapshot, error: error instanceof Error ? error.message : String(error) }));
    return true;
  }

  if (message?.type === "refreshJobs") {
    refreshJobs(Boolean(message?.notify))
      .then(snapshot => sendResponse(snapshot))
      .catch(error => sendResponse({ ...emptyJobsSnapshot, error: error instanceof Error ? error.message : String(error) }));
    return true;
  }

  return false;
});

chrome.runtime.onInstalled.addListener(() => {
  void initializeJobMonitor();
});

chrome.runtime.onStartup.addListener(() => {
  void initializeJobMonitor();
});

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === jobsAlarmName) {
    void refreshJobs(true);
  }
});

void initializeJobMonitor();

async function getCachedAccessToken(): Promise<AuthTokenResult | null> {
  const stored = await chrome.storage.local.get(authCacheKey);
  const cached = stored[authCacheKey] as AuthTokenCache | undefined;
  if (!cached?.token || !cached.expiresAt || cached.expiresAt <= Date.now()) {
    return null;
  }

  return {
    token: cached.token,
    accountName: cached.accountName,
    accountUsername: cached.accountUsername
  };
}

async function getCurrentAuthState(): Promise<AuthTokenResult | null> {
  return await getCachedAccessToken() || await refreshAccessToken();
}

async function refreshAccessToken(): Promise<AuthTokenResult | null> {
  const settings = await getSettings();
  const stored = await chrome.storage.local.get(authCacheKey);
  const cached = stored[authCacheKey] as AuthTokenCache | undefined;
  if (!cached?.refreshToken) {
    return null;
  }

  const scopes = uniqueScopes([settings.scope, "openid", "profile", "email", "offline_access"]).join(" ");
  const tokenResponse = await fetch(`${settings.authority.replace(/\/$/, "")}/oauth2/v2.0/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: settings.clientId,
      grant_type: "refresh_token",
      refresh_token: cached.refreshToken,
      scope: scopes
    })
  });
  const tokenJson = await tokenResponse.json();
  if (!tokenResponse.ok) {
    if (tokenJson.error === "invalid_grant") {
      await chrome.storage.local.remove(authCacheKey);
    }

    return null;
  }

  const identity = parseJwtPayload(tokenJson.id_token);
  const result = {
    token: tokenJson.access_token as string,
    accountName: asString(identity?.name) || cached.accountName,
    accountUsername: asString(identity?.preferred_username || identity?.email || identity?.upn) || cached.accountUsername
  };
  await saveAuthCache(tokenJson, result, cached.refreshToken);
  void refreshJobs(false);
  return result;
}

async function saveAuthCache(
  tokenJson: Record<string, unknown>,
  result: AuthTokenResult,
  fallbackRefreshToken?: string): Promise<void> {
  await chrome.storage.local.set({
    [authCacheKey]: {
      ...result,
      expiresAt: Date.now() + Math.max(0, Number(tokenJson.expires_in || 0) - 60) * 1000,
      refreshToken: asString(tokenJson.refresh_token) || fallbackRefreshToken
    } satisfies AuthTokenCache
  });
}

async function signOut(): Promise<void> {
  await chrome.storage.local.remove([authCacheKey, jobsSnapshotKey]);
  await setJobBadge(0);
}

async function initializeJobMonitor(): Promise<void> {
  await chrome.action.setBadgeBackgroundColor({ color: "#246bfe" });
  await chrome.alarms.create(jobsAlarmName, { periodInMinutes: 1 });
  const snapshot = await getStoredJobsSnapshot();
  await setJobBadge(snapshot.count);
  await refreshJobs(false);
}

async function getStoredJobsSnapshot(): Promise<JobsSnapshot> {
  const stored = await chrome.storage.local.get(jobsSnapshotKey);
  const snapshot = stored[jobsSnapshotKey] as JobsSnapshot | undefined;
  return snapshot?.jobs ? snapshot : emptyJobsSnapshot;
}

async function refreshJobs(notify: boolean): Promise<JobsSnapshot> {
  const token = await getCurrentAuthState();
  if (!token) {
    await chrome.storage.local.set({ [jobsSnapshotKey]: emptyJobsSnapshot });
    await setJobBadge(0);
    return emptyJobsSnapshot;
  }

  const previous = await getStoredJobsSnapshot();
  try {
    const settings = await getSettings();
    const response = await fetch(`${settings.apiBaseUrl}/Jobs/InProgress`, {
      headers: { Authorization: `Bearer ${token.token}` }
    });
    if (!response.ok) {
      throw new Error(await response.text());
    }

    const payload = await response.json() as { count?: number; jobs?: ActiveToolJob[] };
    const snapshot: JobsSnapshot = {
      count: Number(payload.count || payload.jobs?.length || 0),
      jobs: Array.isArray(payload.jobs) ? payload.jobs : [],
      lastUpdatedUtc: new Date().toISOString()
    };
    await chrome.storage.local.set({ [jobsSnapshotKey]: snapshot });
    await setJobBadge(snapshot.count);
    if (notify) {
      await notifyCompletedJobs(previous, snapshot);
    }
    return snapshot;
  } catch (error) {
    const snapshot: JobsSnapshot = {
      ...previous,
      error: error instanceof Error ? error.message : String(error),
      lastUpdatedUtc: new Date().toISOString()
    };
    await chrome.storage.local.set({ [jobsSnapshotKey]: snapshot });
    await setJobBadge(snapshot.count);
    return snapshot;
  }
}

async function setJobBadge(count: number): Promise<void> {
  await chrome.action.setBadgeText({ text: count > 0 ? String(Math.min(count, 99)) : "" });
}

async function notifyCompletedJobs(previous: JobsSnapshot, current: JobsSnapshot): Promise<void> {
  if (!previous.jobs.length) {
    return;
  }

  const currentIds = new Set(current.jobs.map(job => job.id));
  const completed = previous.jobs.filter(job => !currentIds.has(job.id));
  if (completed.length === 0) {
    return;
  }

  const message = completed.length === 1
    ? `${completed[0].title || "A job"} is no longer in progress.`
    : `${completed.length} AI Toolbox jobs are no longer in progress.`;
  await chrome.notifications.create(`aitoolbox-job-complete-${Date.now()}`, {
    type: "basic",
    iconUrl: "icons/icon-128.png",
    title: "AI Toolbox job finished",
    message
  });
}

function uniqueScopes(scopes: string[]): string[] {
  return Array.from(new Set(scopes.flatMap(scope => scope.split(/\s+/)).map(scope => scope.trim()).filter(Boolean)));
}

async function sha256Base64Url(value: string): Promise<string> {
  const bytes = new TextEncoder().encode(value);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return base64UrlEncode(new Uint8Array(digest));
}

function base64UrlEncode(bytes: Uint8Array): string {
  let value = "";
  for (const byte of bytes) {
    value += String.fromCharCode(byte);
  }

  return btoa(value)
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/g, "");
}

function parseJwtPayload(token: unknown): Record<string, unknown> | null {
  if (typeof token !== "string") {
    return null;
  }

  const payload = token.split(".")[1];
  if (!payload) {
    return null;
  }

  try {
    const normalized = payload.replace(/-/g, "+").replace(/_/g, "/");
    const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, "=");
    return JSON.parse(atob(padded)) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function asString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

async function openProductImport(tabId: number | undefined): Promise<void> {
  if (tabId) {
    await chrome.storage.local.set({ [zohoImportTargetKey]: tabId });
  }
  await chrome.storage.local.set({ [zohoImportModeKey]: true });

  const url = chrome.runtime.getURL("popup.html?mode=zoho-import");
  const openPopup = chrome.action.openPopup as (() => Promise<void>) | undefined;
  if (openPopup) {
    try {
      await openPopup();
      return;
    } catch {
      // Fall back to an extension window below.
    }
  }

  try {
    await chrome.windows.create({
      focused: true,
      height: 720,
      type: "popup",
      url,
      width: 480
    });
  } catch {
    await chrome.tabs.create({ active: true, url });
  }
}

async function getZohoImportTarget(): Promise<{ tabId: number; title?: string; url?: string } | null> {
  const activeTabs = await chrome.tabs.query({ active: true });
  const activeZohoTab = activeTabs.find(tab => tab.id && isZohoCrmUrl(tab.url));
  if (activeZohoTab?.id) {
    await chrome.storage.local.set({ [zohoImportTargetKey]: activeZohoTab.id });
    return { tabId: activeZohoTab.id, title: activeZohoTab.title, url: activeZohoTab.url };
  }

  const stored = await chrome.storage.local.get(zohoImportTargetKey);
  const storedTabId = Number(stored[zohoImportTargetKey]);
  if (Number.isFinite(storedTabId) && storedTabId > 0) {
    try {
      const tab = await chrome.tabs.get(storedTabId);
      if (tab.id && isZohoCrmUrl(tab.url)) {
        return { tabId: tab.id, title: tab.title, url: tab.url };
      }
    } catch {
      await chrome.storage.local.remove(zohoImportTargetKey);
    }
  }

  return null;
}

function isZohoCrmUrl(url: string | undefined): boolean {
  if (!url) {
    return false;
  }

  try {
    const parsed = new URL(url);
    return parsed.hostname === "crm.zoho.com" && parsed.pathname.startsWith("/crm/");
  } catch {
    return false;
  }
}
