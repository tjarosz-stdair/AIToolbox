const windowTitleElement = document.querySelector<HTMLElement>("#windowTitle");

void refreshActiveWindowTitle();
chrome.tabs.onActivated.addListener(() => void refreshActiveWindowTitle());
chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (tab.active && (changeInfo.title !== undefined || changeInfo.url !== undefined)) {
    void refreshActiveWindowTitle();
  }
});

async function refreshActiveWindowTitle(): Promise<void> {
  const [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (windowTitleElement) {
    windowTitleElement.textContent = isP21Url(activeTab?.url) && activeTab?.title
      ? activeTab.title
      : "No P21 window detected";
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
