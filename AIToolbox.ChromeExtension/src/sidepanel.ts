// sidepanel.ts -- hosts the actual AI Toolbox chat (/embed/chat) and acts as the "broker" half of
// the aitoolbox: panel protocol (same shapes AiToolboxSelfBroker.js already implements on the
// PIC Search side for the standalone-page case). Context/token requests are relayed through the
// background worker to pic-search-content.ts, which is the piece that actually shares an origin
// (and therefore cookies) with PIC Search's own page.

interface PicsearchSurfaceInfo {
  tabId: number;
  frameId: number;
  surface: string;
  contextKey: string | null;
  pageContext: unknown;
}

const windowTitleElement = document.querySelector<HTMLElement>("#windowTitle");
const idleHintElement = document.querySelector<HTMLElement>("#idleHint");
const chatFrame = document.querySelector<HTMLIFrameElement>("#chatFrame");

let activeTabId: number | null = null;
let boundSurface: PicsearchSurfaceInfo | null = null;
let panelOrigin: string | null = null;
let sessionToken: string | null = null;
let panelReady = false;

void refreshActiveTab();
chrome.tabs.onActivated.addListener(() => void refreshActiveTab());
chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (tab.active && (changeInfo.title !== undefined || changeInfo.url !== undefined)) {
    void refreshActiveTab();
  }
});

chrome.runtime.onMessage.addListener(message => {
  if (!message || typeof message.type !== "string" || message.tabId !== activeTabId) {
    return;
  }

  if (message.type === "picsearchContextUpdate") {
    onSurfaceContextUpdate(message as PicsearchSurfaceInfo);
  } else if (message.type === "picsearchAck" && boundSurface?.frameId === message.frameId) {
    postToIframe({ type: "aitoolbox:ack", version: 1, acks: message.acks });
  } else if (message.type === "picsearchSurfaceGone" && boundSurface?.frameId === message.frameId) {
    deactivateChat();
  }
});

window.addEventListener("message", onIframeMessage);

async function refreshActiveTab(): Promise<void> {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  activeTabId = tab?.id ?? null;

  if (windowTitleElement) {
    windowTitleElement.textContent = isP21Url(tab?.url) && tab?.title
      ? tab.title
      : "No P21 window detected";
  }

  if (activeTabId === null || !isP21Url(tab?.url)) {
    deactivateChat();
    return;
  }

  try {
    const surface = await chrome.runtime.sendMessage({ type: "getPicsearchSurface", tabId: activeTabId }) as PicsearchSurfaceInfo | null;
    if (surface) {
      onSurfaceContextUpdate(surface);
    } else {
      deactivateChat();
    }
  } catch (error) {
    console.error("[AI Toolbox] Failed to query PIC Search surface state", error);
  }
}

function onSurfaceContextUpdate(surface: PicsearchSurfaceInfo): void {
  if (surface.tabId !== activeTabId) {
    return; // stale update for a tab that's no longer focused
  }

  const isNewBinding = !boundSurface || boundSurface.tabId !== surface.tabId || boundSurface.frameId !== surface.frameId;
  boundSurface = surface;

  if (isNewBinding) {
    void activateChat(surface);
  } else if (panelReady) {
    postToIframe({
      type: "aitoolbox:context",
      version: 1,
      surfaces: [surface.surface],
      contextKey: surface.contextKey,
      pageContext: surface.pageContext
    });
  }
}

async function activateChat(surface: PicsearchSurfaceInfo): Promise<void> {
  panelReady = false;
  const minted = await mintToken(surface);
  if (!minted || !minted.token || !minted.aiToolboxUrl) {
    deactivateChat();
    return;
  }

  sessionToken = minted.token;
  panelOrigin = minted.aiToolboxUrl.replace(/\/+$/, "");

  if (idleHintElement) {
    idleHintElement.hidden = true;
  }
  if (chatFrame) {
    chatFrame.hidden = false;
    chatFrame.src = `${panelOrigin}/embed/chat?parentOrigin=${encodeURIComponent(`chrome-extension://${chrome.runtime.id}`)}`;
  }
}

function deactivateChat(): void {
  boundSurface = null;
  panelOrigin = null;
  sessionToken = null;
  panelReady = false;

  if (chatFrame) {
    chatFrame.hidden = true;
    chatFrame.src = "about:blank";
  }
  if (idleHintElement) {
    idleHintElement.hidden = false;
  }
}

interface MintTokenResponse {
  token?: string;
  aiToolboxUrl?: string;
  error?: string;
}

async function mintToken(surface: PicsearchSurfaceInfo): Promise<MintTokenResponse | null> {
  try {
    const response = await chrome.runtime.sendMessage({
      type: "mintSessionToken",
      tabId: surface.tabId,
      frameId: surface.frameId
    }) as MintTokenResponse;
    if (response?.error) {
      console.error("[AI Toolbox] Session token mint failed", response.error);
      return null;
    }
    return response;
  } catch (error) {
    // Extension context invalidated, or the content script in that frame is gone -- either way,
    // there's nothing to recover into; leaving the panel idle is correct.
    console.error("[AI Toolbox] Failed to request a session token", error);
    return null;
  }
}

function postToIframe(msg: Record<string, unknown>): void {
  if (!chatFrame?.contentWindow || !panelOrigin) {
    return;
  }
  try {
    chatFrame.contentWindow.postMessage(msg, panelOrigin);
  } catch (error) {
    console.error("[AI Toolbox] Failed to post message into the chat panel", error);
  }
}

function onIframeMessage(event: MessageEvent): void {
  if (!chatFrame || event.source !== chatFrame.contentWindow || !panelOrigin || event.origin !== panelOrigin) {
    return;
  }

  const data = event.data as { type?: string; [key: string]: unknown } | undefined;
  if (!data || typeof data.type !== "string") {
    return;
  }

  switch (data.type) {
    case "aitoolbox:ready":
      onPanelReady();
      break;
    case "aitoolbox:intent":
      relayIntent(data);
      break;
    case "aitoolbox:token-expired":
      void reissueToken();
      break;
    default:
      // aitoolbox:resize (no-op -- a native side panel isn't extension-resizable) and
      // aitoolbox:conversation (never actually sent by embed-chat.js) both land here.
      break;
  }
}

function onPanelReady(): void {
  panelReady = true;
  if (!boundSurface) {
    return;
  }
  postToIframe({
    type: "aitoolbox:init",
    version: 1,
    sessionToken,
    surfaces: [boundSurface.surface],
    contextKey: boundSurface.contextKey,
    pageContext: boundSurface.pageContext
  });
  postToIframe({ type: "aitoolbox:visibility", version: 1, visible: true });
}

function relayIntent(payload: Record<string, unknown>): void {
  if (!boundSurface) {
    return;
  }
  chrome.runtime.sendMessage({
    type: "sendPicsearchIntent",
    tabId: boundSurface.tabId,
    frameId: boundSurface.frameId,
    payload
  }).catch(error => console.error("[AI Toolbox] Failed to relay intent to PIC Search", error));
}

async function reissueToken(): Promise<void> {
  if (!boundSurface) {
    return;
  }
  const minted = await mintToken(boundSurface);
  if (minted?.token) {
    sessionToken = minted.token;
    postToIframe({ type: "aitoolbox:token", version: 1, sessionToken });
  }
}

function isP21Url(url: string | undefined): boolean {
  if (!url) {
    return false;
  }

  try {
    const parsed = new URL(url);
    return parsed.hostname === "p21web.stdair.com" && parsed.pathname.startsWith("/prophet21");
  } catch {
    return false;
  }
}
