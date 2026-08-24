// pic-search-content.ts -- bridges AiToolboxAdapter.js's "surface" postMessage protocol (same
// window, PIC Search's own origin) to this extension's background worker, so the side panel can
// act as PIC Search's AI chat broker. Runs in EVERY frame on app.p21toolbox.com (all_frames:
// true, document_start) because PIC Search, when opened from Order Entry, is P21Web's nested
// #vbrIFrame -- a sub-frame, not the tab's main frame -- and the listener has to be attached
// before AiToolboxAdapter.js's one-shot aitoolbox:broker-probe fires (no replay if we're late).
//
// Message shapes below are AiToolboxAdapter.js's own protocol, confirmed by reading that file
// directly -- not something to change independently on this side.

const LOG_PREFIX = "[AI Toolbox PICSearch]";

function logError(message: string, error: unknown): void {
  console.error(LOG_PREFIX, message, error);
}

interface PageMessage {
  type: string;
  version?: number;
  [key: string]: unknown;
}

function postToPage(msg: PageMessage): void {
  try {
    window.postMessage(msg, window.location.origin);
  } catch (error) {
    logError("failed to post message into the page", error);
  }
}

function onWindowMessage(event: MessageEvent): void {
  // Same-window only (content scripts and page JS share one window's message bus) and
  // same-origin -- mirrors AiToolboxAdapter.js's own onWindowMessage validation exactly.
  if (event.source !== window || event.origin !== window.location.origin) {
    return;
  }

  const data = event.data as PageMessage | undefined;
  if (!data || typeof data.type !== "string") {
    return;
  }

  switch (data.type) {
    case "aitoolbox:broker-probe":
      postToPage({ type: "aitoolbox:broker-available", version: 1, disabled: false });
      break;
    case "aitoolbox:surface-register":
    case "aitoolbox:context":
    case "aitoolbox:ack":
    case "aitoolbox:surface-unregister":
      relaySurfaceEvent(data);
      break;
    default:
      break;
  }
}

function relaySurfaceEvent(event: PageMessage): void {
  try {
    chrome.runtime.sendMessage({ type: "picsearchSurfaceEvent", event }).catch((error) => {
      // A normal, expected race the first few times a frame loads: no side panel/background
      // listener has registered interest yet. Never let this become an uncaught rejection.
      logError("picsearchSurfaceEvent relay was not handled", error);
    });
  } catch (error) {
    // Synchronous throw = extension context invalidated (reload/update while this frame was
    // already loaded) -- same failure mode as p21-content.ts's handleMenuIconClick. No recovery
    // possible from here short of a page reload; just don't let it go uncaught.
    logError("failed to relay surface event (extension context may be invalidated)", error);
  }
}

interface MintTokenResponse {
  token?: string;
  expiresAt?: string;
  aiToolboxUrl?: string;
  error?: string;
}

async function mintSessionToken(): Promise<MintTokenResponse> {
  const tokenInput = document.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]');
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (tokenInput?.value) {
    headers.RequestVerificationToken = tokenInput.value;
  }

  const response = await fetch("/api/session-token", {
    method: "POST",
    credentials: "same-origin",
    headers,
    // SessionId is always null here -- embed-chat.js never sends the aitoolbox:conversation
    // message AiToolboxSelfBroker.js listens for either, so there is no real conversationId to
    // thread through; matches the self-broker's own actual (not aspirational) behavior.
    body: JSON.stringify({ Surface: "picsearch", SessionId: null })
  });

  if (!response.ok) {
    return { error: `session-token mint failed with status ${response.status}` };
  }

  const minted = await response.json();
  return { token: minted.token, expiresAt: minted.expiresAt, aiToolboxUrl: minted.aiToolboxUrl };
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || typeof message.type !== "string") {
    return undefined;
  }

  if (message.type === "mintSessionToken") {
    mintSessionToken()
      .then(sendResponse)
      .catch((error) => {
        logError("mintSessionToken failed", error);
        sendResponse({ error: error instanceof Error ? error.message : String(error) });
      });
    return true; // async sendResponse
  }

  if (message.type === "picsearchIntent") {
    postToPage(message.payload as PageMessage);
    return undefined;
  }

  return undefined;
});

window.addEventListener("message", onWindowMessage);
