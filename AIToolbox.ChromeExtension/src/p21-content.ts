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

function handleMenuIconClick(): void {
  console.log("[AI Toolbox] Menu icon clicked, sending openP21Panel message");
  chrome.runtime.sendMessage({ type: "openP21Panel" })
    .catch(error => console.error("[AI Toolbox] Failed to send openP21Panel message", error));
}
