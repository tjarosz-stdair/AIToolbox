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
// The tabId:frameId actually loaded into chatFrame right now -- set ONLY inside activateChat,
// right when chatFrame.src is assigned, and cleared ONLY inside deactivateChat. This is
// deliberately a separate, narrower guard from "do we have fresh surface data" (boundSurface):
// onSurfaceContextUpdate fires on every heartbeat (~20s) and every periodic liveness recheck, not
// just on a real change, and comparing against a mutable boundSurface reference that gets
// reassigned on every single call proved too fragile against repeat triggers -- it was reloading
// the iframe (blanking it, losing the in-progress conversation) on every heartbeat. Gating on the
// iframe's own actual loaded state instead means a redundant activation attempt, whatever
// triggers it, can never blow away a session that's already live.
let activatedSurfaceKey: string | null = null;
let activating = false;

function surfaceKeyOf(surface: PicsearchSurfaceInfo): string {
  return `${surface.tabId}:${surface.frameId}`;
}

const LIVENESS_RECHECK_MS = 30000;

void refreshActiveTab();
chrome.tabs.onActivated.addListener(() => void refreshActiveTab());
chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (tab.active && (changeInfo.title !== undefined || changeInfo.url !== undefined)) {
    void refreshActiveTab();
  }
});
// Catches the case where the SAME P21 tab stays focused the whole time and PIC Search's frame
// disappears without its beforeunload/surface-unregister message ever arriving (a hard tab
// discard can skip it) -- nothing above re-checks then, since neither tab event fires. Background
// already prunes a surface once its heartbeat goes stale (see background.ts); this just makes
// sure the panel actually asks again periodically instead of only on a tab switch.
setInterval(() => void refreshActiveTab(), LIVENESS_RECHECK_MS);

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

  boundSurface = surface;
  const key = surfaceKeyOf(surface);

  if (activatedSurfaceKey === key) {
    // Already showing this exact surface's chat -- a heartbeat or periodic recheck confirming
    // "still open" is not a reason to touch the iframe. Just refresh the cached context in place.
    if (panelReady) {
      postToIframe({
        type: "aitoolbox:context",
        version: 1,
        surfaces: [surface.surface],
        contextKey: surface.contextKey,
        pageContext: surface.pageContext
      });
    }
    return;
  }

  if (!activating) {
    void activateChat(surface);
  }
}

async function activateChat(surface: PicsearchSurfaceInfo): Promise<void> {
  activating = true;
  panelReady = false;
  try {
    const minted = await mintToken(surface);
    if (!minted || !minted.token || !minted.aiToolboxUrl) {
      deactivateChat();
      return;
    }

    sessionToken = minted.token;
    panelOrigin = minted.aiToolboxUrl.replace(/\/+$/, "");
    activatedSurfaceKey = surfaceKeyOf(surface);

    if (idleHintElement) {
      idleHintElement.hidden = true;
    }
    if (chatFrame) {
      chatFrame.hidden = false;
      chatFrame.src = `${panelOrigin}/embed/chat?parentOrigin=${encodeURIComponent(`chrome-extension://${chrome.runtime.id}`)}`;
    }
  } finally {
    activating = false;
  }
}

function deactivateChat(): void {
  activatedSurfaceKey = null;
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
    case "aitoolbox:mic-start":
      void handleMicStart();
      break;
    case "aitoolbox:mic-stop":
      void handleMicStop();
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
    pageContext: boundSurface.pageContext,
    // Tells embed-chat.js to delegate mic recording up to us instead of calling getUserMedia
    // itself -- side panels lack the UX affordance to show a NEW permission prompt (Chrome
    // resolves the request as denied instantly, no dialog ever shown) until the extension's
    // origin has been granted mic access once via mic-permission.html's real tab. Standalone PIC
    // Search and the admin test harness never set this, so they keep using their
    // already-working direct getUserMedia path unchanged.
    micDelegated: true
  });
  postToIframe({ type: "aitoolbox:visibility", version: 1, visible: true });
}

// ---- mic delegation ----
//
// Captured directly here, in the side panel's own script -- NOT in an offscreen document. Chrome
// permission is granted PER-ORIGIN (chrome-extension://<id>), not per-page: an offscreen document
// can't show a fresh prompt either (confirmed: the request just fails silently there too, despite
// chrome.offscreen listing USER_MEDIA as a supported reason -- that covers using an
// ALREADY-GRANTED stream, not prompting for a new one). Once the origin has been granted via
// mic-permission.html (a real tab, which DOES get normal prompt UX), this getUserMedia call
// succeeds silently from here with no prompt needed -- side panels only fail to show a NEW one.

let mediaRecorder: MediaRecorder | null = null;
let micStream: MediaStream | null = null;
let micChunks: Blob[] = [];

async function handleMicStart(): Promise<void> {
  try {
    micStream = await navigator.mediaDevices.getUserMedia({ audio: true });
  } catch (error) {
    console.error("[AI Toolbox] getUserMedia failed -- opening the one-time permission tab", error);
    postToIframe({
      type: "aitoolbox:mic-error",
      version: 1,
      message: "Opening a tab to grant microphone access — allow it there, then try the mic again."
    });
    chrome.runtime.sendMessage({ type: "openMicPermissionTab" })
      .catch(err => console.error("[AI Toolbox] Failed to open the mic permission tab", err));
    return;
  }

  micChunks = [];
  try {
    mediaRecorder = new MediaRecorder(micStream);
  } catch (error) {
    releaseMicStream();
    postToIframe({ type: "aitoolbox:mic-error", version: 1, message: "Voice input could not start on this browser." });
    return;
  }

  mediaRecorder.addEventListener("dataavailable", event => {
    if (event.data && event.data.size > 0) {
      micChunks.push(event.data);
    }
  });
  mediaRecorder.start();
  postToIframe({ type: "aitoolbox:mic-started", version: 1 });
}

async function handleMicStop(): Promise<void> {
  if (!mediaRecorder || mediaRecorder.state !== "recording") {
    return;
  }

  const activeRecorder = mediaRecorder;
  const mimeType = activeRecorder.mimeType || "audio/webm";
  const stopped = new Promise<void>(resolve => {
    activeRecorder.addEventListener("stop", () => resolve(), { once: true });
  });
  activeRecorder.stop();
  await stopped;
  releaseMicStream();

  if (micChunks.length === 0) {
    postToIframe({ type: "aitoolbox:mic-error", version: 1, message: "Didn't catch that — try again." });
    return;
  }

  const blob = new Blob(micChunks, { type: mimeType });
  micChunks = [];
  const arrayBuffer = await blob.arrayBuffer();

  // Hand the raw audio down to the iframe rather than transcribing here: embed-chat.js already
  // has a working POST to /api/picsearch/speech-to-text (same-origin, carries the session token
  // it already holds) for the non-delegated capture path -- reusing that avoids a second,
  // cross-origin copy of the same fetch logic, and avoids CORS entirely (this side panel's
  // chrome-extension:// origin isn't on Cors:PicSearchOrigins, and doesn't need to be).
  postToIframe({ type: "aitoolbox:mic-audio", version: 1, mimeType, arrayBuffer });
}

function releaseMicStream(): void {
  if (micStream) {
    micStream.getTracks().forEach(track => track.stop());
    micStream = null;
  }
  mediaRecorder = null;
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
