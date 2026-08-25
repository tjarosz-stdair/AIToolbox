// mic-permission.ts -- a real, ordinary tab (opened via chrome.tabs.create, never a side panel or
// popup) whose only job is to trigger Chrome's real getUserMedia permission prompt exactly once.
// Side panels and popups lack the UX affordance to show that prompt at all -- the request just
// resolves as denied instantly, silently, no dialog ever shown, confirmed against Chrome's own
// extension samples repo and the chromium-extensions group (an offscreen document has the exact
// same limitation, despite chrome.offscreen listing USER_MEDIA as a supported creation reason --
// that reason covers USING an already-granted stream, not prompting for a new one). A normal tab
// is a genuine top-level browsing context, so it gets the same permission UX any website would.
//
// Chrome's microphone permission is granted per-origin (chrome-extension://<id>), not per-page --
// once granted here, the side panel's own getUserMedia calls succeed silently from then on,
// without ever needing to show a prompt themselves (they only fail to show a NEW one).

const statusElement = document.querySelector<HTMLElement>("#status");
const closeButton = document.querySelector<HTMLButtonElement>("#closeButton");

void requestMicPermission();

async function requestMicPermission(): Promise<void> {
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    stream.getTracks().forEach(track => track.stop());
    setStatus("Microphone access is enabled. You can close this tab and use the microphone button in the AI Toolbox panel.");
  } catch (error) {
    console.error("[AI Toolbox] Microphone permission request failed", error);
    setStatus("Microphone access wasn't granted. Voice input in AI Toolbox won't work until this is allowed for this extension.");
  }

  if (closeButton) {
    closeButton.hidden = false;
    closeButton.addEventListener("click", () => window.close());
  }
}

function setStatus(text: string): void {
  if (statusElement) {
    statusElement.textContent = text;
  }
}
