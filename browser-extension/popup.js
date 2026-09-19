const title = document.querySelector("#page-title");
const address = document.querySelector("#page-address");
const button = document.querySelector("#capture");
const status = document.querySelector("#status");
let expected;
function show(message, failed = false) {
  status.textContent = message;
  status.dataset.state = failed ? "error" : "normal";
}
try {
  const result = await chrome.runtime.sendMessage({ action: "preview" });
  if (!result?.success) {
    title.textContent = "저장할 페이지를 확인해주세요";
    show(result?.message ?? "현재 페이지를 확인하지 못했습니다.", true);
  } else {
    expected = { tabId: result.tabId, url: result.url };
    title.textContent = result.title || "제목 없는 페이지";
    address.textContent = result.url;
    button.disabled = false;
  }
} catch { show("확장을 다시 열어주세요.", true); }
try {
  const commands = await chrome.commands.getAll();
  const shortcut = commands.find(item => item.name === "capture-current-tab")?.shortcut;
  document.querySelector("#shortcut").textContent = shortcut
    ? "현재 저장 단축키: " + shortcut
    : "저장 단축키가 배정되지 않았습니다. 아래 설정에서 ‘현재 페이지를 WorkBookmark에 저장’에 Ctrl+Shift+9 등 사용할 키를 지정해주세요.";
} catch {
  document.querySelector("#shortcut").textContent = "현재 단축키를 확인하지 못했습니다. 아래 설정에서 확인하거나 이 페이지 저장 버튼을 사용해주세요.";
}
const shortcutSettingsUrl = /Edg\//u.test(navigator.userAgent)
  ? "edge://extensions/shortcuts" : "chrome://extensions/shortcuts";
document.querySelector("#shortcut-settings").addEventListener("click", async () => {
  try { await chrome.tabs.create({ url: shortcutSettingsUrl }); }
  catch { show("주소창에 " + shortcutSettingsUrl + " 를 입력해 단축키를 설정해주세요.", true); }
});
button.addEventListener("click", async () => {
  if (!expected || button.disabled) return;
  button.disabled = true;
  show("저장 결과를 확인하고 있습니다…");
  try {
    const result = await chrome.runtime.sendMessage({ action: "capture", ...expected });
    show(result?.message ?? "저장 결과를 확인하지 못했습니다. WorkBookmark 목록을 확인해주세요.", result?.success !== true);
    if (result?.success === true) button.textContent = "저장 완료";
    // An unknown outcome must be checked in the app before a manual retry.
    else if (result?.code !== "OutcomeUnknown") button.disabled = false;
  } catch { show("저장 결과를 확인하지 못했습니다. WorkBookmark 목록을 확인해주세요.", true); }
});
