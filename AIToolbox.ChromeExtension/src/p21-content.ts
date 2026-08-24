const menuItemId = "AIToolbox";
const menuItemClass = "ep-menu-bar-item";

injectMenuIcon();
new MutationObserver(() => injectMenuIcon()).observe(document.documentElement, {
  childList: true,
  subtree: true
});

function injectMenuIcon(): void {
  const secondaryMenu = document.querySelector(".ep-menu-bar-section.secondary");
  if (!secondaryMenu || secondaryMenu.querySelector(`#${menuItemId}`)) {
    return;
  }

  const referenceItem = secondaryMenu.querySelector(`.${menuItemClass}`);

  const item = document.createElement("li");
  item.id = menuItemId;
  item.className = `${menuItemClass} ng-star-inserted`;
  item.title = "AI Toolbox";
  item.style.cursor = "pointer";

  const icon = document.createElement("img");
  icon.src = chrome.runtime.getURL("icons/icon-32.png");
  icon.alt = "AI Toolbox";
  icon.width = 24;
  icon.height = 24;
  icon.style.display = "block";
  item.append(icon);

  item.addEventListener("click", handleMenuIconClick);

  if (referenceItem) {
    referenceItem.insertAdjacentElement("beforebegin", item);
  } else {
    secondaryMenu.prepend(item);
  }
}

async function handleMenuIconClick(): Promise<void> {
  console.log("[AI Toolbox] Menu icon clicked, sending openP21Panel message");
  try {
    await chrome.runtime.sendMessage({ type: "openP21Panel" });
  } catch (error) {
    // chrome.runtime.sendMessage throws SYNCHRONOUSLY (not a rejected promise) once the
    // extension context is invalidated -- e.g. the extension was reloaded/updated while this
    // tab sat open. No API call can revive an already-injected content script's dead
    // chrome.runtime handle; only a page reload re-injects it against the current extension
    // instance. The tooltip is the only recovery path available from here.
    console.error("[AI Toolbox] Failed to send openP21Panel message", error);
    const item = document.getElementById(menuItemId);
    if (item) {
      item.title = "AI Toolbox needs the page refreshed to work again";
    }
  }
}
