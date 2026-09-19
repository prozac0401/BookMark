export const HOST_NAME = "com.workbookmark.capture";
export const MAX_URL_LENGTH = 16384;
export const DEFAULT_TITLE = "WorkBookmark · 현재 페이지 저장";

export class CaptureError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}
function fail(code, message) { throw new CaptureError(code, message); }

// Parse to validate only. The original string, including query and fragment, is the bookmark.
export function validateWebUrl(raw) {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > MAX_URL_LENGTH ||
      raw !== raw.trim() || /[\u0000-\u0020\u007f\\]/u.test(raw))
    fail("UnsupportedUrl", "이 페이지 주소는 저장할 수 없습니다.");
  let parsed;
  try { parsed = new URL(raw); } catch { fail("UnsupportedUrl", "이 페이지 주소는 저장할 수 없습니다."); }
  if (!/^https?:\/\//iu.test(raw) || !["http:", "https:"].includes(parsed.protocol) || !parsed.hostname ||
      parsed.username || parsed.password)
    fail("UnsupportedUrl", "일반 http·https 페이지에서 사용해주세요. 내부 페이지나 계정 정보가 포함된 주소는 지원하지 않습니다.");
  return raw;
}
export function limitTitle(value) {
  if (typeof value !== "string") return "";
  let title = value.replace(/[\u0000-\u001f\u007f]/gu, " ").slice(0, 256);
  if (/[\uD800-\uDBFF]$/u.test(title)) title = title.slice(0, -1);
  return title;
}
export function readTab(tab) {
  if (!tab || !Number.isInteger(tab.id) || tab.id < 0 || tab.active !== true || tab.incognito)
    fail("UnsupportedTab", "현재 일반 브라우저 탭에서 다시 시도해주세요.");
  if (tab.pendingUrl || tab.status === "loading")
    fail("PageLoading", "페이지 이동이 끝난 뒤 다시 저장해주세요.");
  return { tabId: tab.id, windowId: tab.windowId, url: validateWebUrl(tab.url), title: limitTitle(tab.title) };
}
export function validateHostResponse(response, requestId) {
  if (!response || typeof response !== "object" || response.requestId !== requestId ||
      typeof response.success !== "boolean" || typeof response.code !== "string")
    fail("OutcomeUnknown", "저장 결과를 확인하지 못했습니다. WorkBookmark 목록을 확인해주세요.");
  if (response.success === true && response.code === "CaptureCommitted")
    return { success: true, code: response.code, message: "WorkBookmark에 저장했습니다." };
  if (response.success === true || response.code === "CaptureCommitted")
    fail("OutcomeUnknown", "저장 결과를 확인하지 못했습니다. WorkBookmark 목록을 확인해주세요.");
  // Host messages are displayed as text, never HTML; cap unexpected output and never log URL/title.
  return { success: false, code: response.code,
    message: typeof response.message === "string" && response.message.trim()
      ? response.message.slice(0, 400) : "저장하지 못했습니다. WorkBookmark를 확인해주세요." };
}
export function asFailure(error) {
  return { success: false, code: error instanceof CaptureError ? error.code : "Unavailable",
    message: error instanceof CaptureError ? error.message : "연결을 확인하지 못했습니다. WorkBookmark 실행 및 브라우저 연결 설정을 확인해주세요." };
}

// Chrome and Edge report documented startup failures through runtime.lastError.
// Only these exact failures prove that no native request was processed. Never echo raw diagnostics.
export function nativeConnectionFailure(error) {
  const message = typeof error?.message === "string" ? error.message.trim() : "";
  switch (message) {
    case "Specified native messaging host not found.":
      return new CaptureError("NativeHostMissing", "브라우저 연결을 찾지 못했습니다. WorkBookmark 설치 폴더의 브라우저 연결 등록을 확인해주세요.");
    case "Access to the specified native messaging host is forbidden.":
      return new CaptureError("NativeHostForbidden", "이 확장의 브라우저 연결이 허용되지 않았습니다. 현재 브라우저에 표시된 확장 ID로 연결을 등록해주세요. 조직에서 관리하는 연결은 관리자에게 문의해주세요.");
    case "Failed to start native messaging host.":
      return new CaptureError("NativeHostStartFailed", "브라우저 연결 프로그램을 실행하지 못했습니다. WorkBookmark 설치 폴더와 browser-host 프로그램의 실행 가능 여부를 확인해주세요.");
    default:
      return new CaptureError("OutcomeUnknown", "연결을 확인하지 못했습니다. WorkBookmark 실행 및 브라우저 연결 설정을 확인하고, 저장 여부는 목록에서 확인해주세요.");
  }
}

export function createCaptureWorkflow(api, options = {}) {
  let pending = false;
  const timeoutMs = options.timeoutMs ?? 15000;
  const uuid = options.uuid ?? (() => crypto.randomUUID());
  async function current() {
    const tabs = await api.tabs.query({ active: true, currentWindow: true });
    if (tabs.length !== 1) fail("ContextChanged", "현재 탭을 확인하지 못했습니다. 다시 시도해주세요.");
    return readTab(tabs[0]);
  }
  async function preview() {
    try { return { success: true, ...(await current()) }; }
    catch (error) { return asFailure(error); }
  }
  async function capture(expected) {
    if (pending) return { success: false, code: "Busy", message: "이전 저장 결과를 확인하고 있습니다." };
    pending = true;
    let tabId, capturedUrl;
    try {
      const first = await current();
      tabId = first.tabId;
      capturedUrl = first.url;
      if (expected && (first.tabId !== expected.tabId || first.url !== expected.url))
        fail("ContextChanged", "페이지가 바뀌었습니다. 확장을 다시 열어 현재 페이지를 확인해주세요.");
      const second = await current();
      if (first.tabId !== second.tabId || first.windowId !== second.windowId || first.url !== second.url)
        fail("ContextChanged", "현재 탭이나 주소가 바뀌었습니다. 다시 시도해주세요.");
      const request = { protocolVersion: 1, action: "capture", requestId: uuid(), url: second.url, title: second.title };
      let timer;
      // A timeout is an unknown result: a host may commit after its response is lost. Never retry automatically.
      const reply = await Promise.race([
        Promise.resolve().then(() => api.sendNative(request)).catch(error => {
          throw error instanceof CaptureError ? error : nativeConnectionFailure(error);
        }),
        new Promise((_, reject) => { timer = setTimeout(() => reject(new CaptureError("OutcomeUnknown",
          "저장 결과를 확인하지 못했습니다. WorkBookmark 목록을 확인해주세요.")), timeoutMs); })
      ]).finally(() => clearTimeout(timer));
      return { ...validateHostResponse(reply, request.requestId), tabId, capturedUrl };
    } catch (error) { return { ...asFailure(error), tabId, capturedUrl }; }
    finally { pending = false; }
  }
  return { preview, capture };
}
