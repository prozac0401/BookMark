import { HOST_NAME, DEFAULT_TITLE, createCaptureWorkflow, asFailure, nativeConnectionFailure } from "./capture.mjs";

const workflow = createCaptureWorkflow({
  tabs: chrome.tabs,
  sendNative: request => new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST_NAME, request, response => {
      // Read lastError in its callback; do not expose raw host diagnostics, URLs, or titles.
      const error = chrome.runtime.lastError;
      if (error) reject(nativeConnectionFailure(error));
      else resolve(response);
    });
  })
});
const badges = new Map();
async function showResult(result) {
  if (!Number.isInteger(result.tabId)) return;
  const tabId = result.tabId;
  const token = Symbol();
  try {
    const current = await chrome.tabs.query({ active: true, currentWindow: true });
    if (current.length !== 1 || current[0].id !== tabId || current[0].url !== result.capturedUrl || current[0].status === "loading") return;
    badges.set(tabId, token);
    await chrome.action.setBadgeBackgroundColor({ tabId, color: result.success ? "#256D47" : "#9A5B00" });
    if (badges.get(tabId) !== token) return;
    await chrome.action.setBadgeText({ tabId, text: result.success ? "✓" : "!" });
    if (badges.get(tabId) !== token) return;
    await chrome.action.setTitle({ tabId, title: "WorkBookmark · " + result.message });
    setTimeout(() => {
      if (badges.get(tabId) !== token) return;
      badges.delete(tabId);
      chrome.action.setBadgeText({ tabId, text: "" }).catch(() => {});
      chrome.action.setTitle({ tabId, title: DEFAULT_TITLE }).catch(() => {});
    }, 8000);
  } catch { /* Closing a tab must not turn a committed bookmark into a failed result. */ }
}
// Clear an old result when that tab starts navigating; no URL or page contents are read here.
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status !== "loading" || !badges.has(tabId)) return;
  badges.delete(tabId);
  chrome.action.setBadgeText({ tabId, text: "" }).catch(() => {});
  chrome.action.setTitle({ tabId, title: DEFAULT_TITLE }).catch(() => {});
});
chrome.tabs.onRemoved.addListener(tabId => { badges.delete(tabId); });
chrome.commands.onCommand.addListener((command, tab) => {
  if (command !== "capture-current-tab") return;
  // The supplied tab is the command's user-invoked context. Query again before sending.
  const expected = tab?.id != null && tab?.url ? { tabId: tab.id, url: tab.url } : undefined;
  workflow.capture(expected).then(showResult).catch(() => {});
});
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  // Only our popup can request capture. There are no content scripts or external message listeners.
  if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL("popup.html")) return false;
  if (message?.action === "preview") {
    workflow.preview().then(respond).catch(error => respond(asFailure(error)));
    return true;
  }
  if (message?.action === "capture" && Number.isInteger(message.tabId) && typeof message.url === "string") {
    workflow.capture({ tabId: message.tabId, url: message.url }).then(async result => {
      await showResult(result); respond(result);
    }).catch(error => respond(asFailure(error)));
    return true;
  }
  return false;
});
