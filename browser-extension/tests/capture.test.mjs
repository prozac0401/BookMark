import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createCaptureWorkflow, validateWebUrl, limitTitle, readTab, validateHostResponse, CaptureError } from "../capture.mjs";

const tab = { id: 7, windowId: 2, active: true, status: "complete", url: "https://example.com/path?x=1&token=synthetic#part-3", title: "Synthetic tab" };
const requestId = "c1f90045-8a87-45f0-8c44-e86d8cfa12c1";
function fake(options = {}) {
  let queries = 0;
  const sent = [];
  const api = {
    tabs: { query: async args => {
      assert.deepEqual(args, { active: true, currentWindow: true });
      const value = options.tabs?.[queries++] ?? [tab];
      return value;
    } },
    sendNative: async request => {
      sent.push(request);
      return options.send ? options.send(request) : { requestId: request.requestId, success: true, code: "CaptureCommitted" };
    }
  };
  return { api, sent, workflow: createCaptureWorkflow(api, { uuid: () => requestId, timeoutMs: options.timeoutMs ?? 100 }) };
}
test("manifest grants temporary current-tab access only", async () => {
  const manifest = JSON.parse(await readFile(new URL("../manifest.json", import.meta.url)));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions.toSorted(), ["activeTab", "nativeMessaging"]);
  assert.equal(manifest.host_permissions, undefined);
  assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.incognito, "not_allowed");
});
test("URL spelling, query ordering, percent escapes and fragment are preserved", () => {
  const original = "https://EXAMPLE.com:443/a%2Fb?b=two&a=%2f&x=1&x=2#p%20a";
  assert.equal(validateWebUrl(original), original);
});
test("unsupported and ambiguous URLs are rejected before native messaging", async () => {
  for (const url of ["chrome://settings/", "edge://history/", "file:///C:/private.txt", "javascript:alert(1)",
    "data:text/plain,hello", "about:blank", "https://user:password@example.com/", "https:\\example.com/",
    " https://example.com/", "https://example.com/\n", "https://", "https://example.com/" + "x".repeat(16384)]) {
    const { workflow, sent } = fake({ tabs: [[{ ...tab, url }]] });
    assert.equal((await workflow.capture()).success, false, url);
    assert.equal(sent.length, 0, url);
  }
});
test("title cap preserves surrogate pairs and removes control characters", () => {
  assert.equal(limitTitle("a".repeat(255) + "😀suffix"), "a".repeat(255));
  assert.equal(limitTitle("first\nsecond\u0000"), "first second ");
  assert.equal(limitTitle(null), "");
});
test("loading, inaccessible, inactive and private tabs are refused", () => {
  for (const value of [{ ...tab, status: "loading" }, { ...tab, pendingUrl: "https://example.com/next" },
    { ...tab, url: undefined }, { ...tab, active: false }, { ...tab, incognito: true }]) {
    assert.throws(() => readTab(value), CaptureError);
  }
});
test("preview reads metadata without contacting the native host", async () => {
  const { workflow, sent } = fake();
  assert.equal((await workflow.preview()).url, tab.url);
  assert.equal(sent.length, 0);
});
test("native capture carries only bounded tab metadata and waits for commit", async () => {
  let complete;
  const { workflow, sent } = fake({ send: request => new Promise(resolve => { complete = () => resolve({ requestId: request.requestId, success: true, code: "CaptureCommitted" }); }) });
  let settled = false;
  const result = workflow.capture().then(value => { settled = true; return value; });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(settled, false);
  assert.equal(sent.length, 1);
  assert.deepEqual(sent[0], { protocolVersion: 1, action: "capture", requestId, url: tab.url, title: tab.title });
  complete();
  assert.equal((await result).success, true);
});
test("popup cannot capture a different tab or a navigated page", async () => {
  for (const expected of [{ tabId: 8, url: tab.url }, { tabId: 7, url: "https://example.com/old" }]) {
    const { workflow, sent } = fake();
    assert.equal((await workflow.capture(expected)).code, "ContextChanged");
    assert.equal(sent.length, 0);
  }
});
test("tab switch, window switch and navigation during metadata collection are refused", async () => {
  for (const changed of [{ ...tab, id: 8 }, { ...tab, windowId: 3 }, { ...tab, url: "https://example.com/next" }]) {
    const { workflow, sent } = fake({ tabs: [[tab], [changed]] });
    assert.equal((await workflow.capture()).code, "ContextChanged");
    assert.equal(sent.length, 0);
  }
});
test("concurrent requests do not start a second host", async () => {
  let complete;
  const { workflow, sent } = fake({ send: request => new Promise(resolve => { complete = () => resolve({ requestId: request.requestId, success: true, code: "CaptureCommitted" }); }) });
  const first = workflow.capture();
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal((await workflow.capture()).code, "Busy");
  assert.equal(sent.length, 1);
  complete();
  assert.equal((await first).success, true);
});
test("host timeout is unknown and never automatically retries", async () => {
  const { workflow, sent } = fake({ timeoutMs: 5, send: () => new Promise(() => {}) });
  const result = await workflow.capture();
  assert.equal(result.success, false);
  assert.equal(result.code, "OutcomeUnknown");
  assert.equal(sent.length, 1);
});
test("native connection loss cannot be reported as confirmed failure or success", async () => {
  const { workflow } = fake({ send: async () => { throw new Error("pipe broke after commit"); } });
  assert.equal((await workflow.capture()).code, "OutcomeUnknown");
});
test("known host startup errors identify setup failure; disconnects remain unknown without retries", async () => {
  for (const [message, expected] of [
    ["Specified native messaging host not found.", "NativeHostMissing"],
    ["Access to the specified native messaging host is forbidden.", "NativeHostForbidden"],
    ["Failed to start native messaging host.", "NativeHostStartFailed"],
    ["Native host has exited.", "OutcomeUnknown"],
    ["Error when communicating with the native messaging host.", "OutcomeUnknown"],
    ["private raw host diagnostic: https://example.com/?secret=synthetic", "OutcomeUnknown"]
  ]) {
    const { workflow, sent } = fake({ send: async () => { throw new Error(message); } });
    const result = await workflow.capture();
    assert.equal(result.success, false);
    assert.equal(result.code, expected, message);
    assert.equal(sent.length, 1);
    assert.equal(result.message.includes(message), false, "raw diagnostic must not reach the popup");
  }
});
test("wrong request ID, malformed replies and noncommit successes are never success", () => {
  for (const response of [null, {}, { requestId: "wrong", success: true, code: "CaptureCommitted" },
    { requestId, success: true, code: "Accepted" }, { requestId, success: "true", code: "CaptureCommitted" },
    { requestId, success: false, code: "CaptureCommitted" }]) {
    assert.throws(() => validateHostResponse(response, requestId), CaptureError);
  }
});
test("host database refusal is displayed as failure without retry", async () => {
  const { workflow, sent } = fake({ send: request => ({ requestId: request.requestId, success: false, code: "PersistenceFailed", message: "저장하지 못했습니다." }) });
  const result = await workflow.capture();
  assert.equal(result.success, false);
  assert.equal(result.code, "PersistenceFailed");
  assert.equal(sent.length, 1);
});

test("service worker accepts only its popup and confirms a saved unchanged page", async t => {
  const extensionId = "a".repeat(32);
  let listener, nativeError;
  const actions = [], sent = [];
  globalThis.chrome = {
    runtime: {
      id: extensionId, getURL: path => "chrome-extension://" + extensionId + "/" + path,
      onMessage: { addListener: fn => { listener = fn; } },
      sendNativeMessage: (name, request, respond) => {
        assert.equal(name, "com.workbookmark.capture");
        sent.push(request);
        if (nativeError) {
          chrome.runtime.lastError = { message: nativeError };
          respond(undefined);
          delete chrome.runtime.lastError;
        } else respond({ requestId: request.requestId, success: true, code: "CaptureCommitted" });
      }
    },
    tabs: { query: async () => [tab], onUpdated: { addListener() {} }, onRemoved: { addListener() {} } },
    commands: { onCommand: { addListener() {} } },
    action: {
      setBadgeBackgroundColor: async value => actions.push(["color", value]),
      setBadgeText: async value => actions.push(["badge", value]),
      setTitle: async value => actions.push(["title", value])
    }
  };
  t.after(() => { delete globalThis.chrome; });
  t.mock.method(globalThis, "setTimeout", () => ({}));
  await import("../service-worker.js?integration=popup");
  assert.equal(listener({ action: "capture", tabId: tab.id, url: tab.url },
    { id: extensionId, url: "https://malicious.example/" }, () => {}), false);
  assert.equal(sent.length, 0);
  const result = await new Promise(resolve => {
    assert.equal(listener({ action: "capture", tabId: tab.id, url: tab.url },
      { id: extensionId, url: chrome.runtime.getURL("popup.html") }, resolve), true);
  });
  assert.equal(result.success, true);
  assert.equal(sent.length, 1);
  assert.equal(actions.find(([name]) => name === "badge")[1].text, "✓");
  nativeError = "Specified native messaging host not found.";
  const failure = await new Promise(resolve => {
    listener({ action: "capture", tabId: tab.id, url: tab.url },
      { id: extensionId, url: chrome.runtime.getURL("popup.html") }, resolve);
  });
  assert.equal(failure.success, false);
  assert.equal(failure.code, "NativeHostMissing", "callback-only lastError classification survives the worker boundary");
  assert.equal(sent.length, 2, "failure does not retry the native call");
  assert.equal(actions.filter(([name]) => name === "badge").at(-1)[1].text, "!");
});

test("a late commit for the previous URL never decorates the new page", async t => {
  const extensionId = "b".repeat(32);
  let listener, queries = 0;
  const actions = [];
  globalThis.chrome = {
    runtime: {
      id: extensionId, getURL: path => "chrome-extension://" + extensionId + "/" + path,
      onMessage: { addListener: fn => { listener = fn; } },
      sendNativeMessage: (_name, request, respond) => respond({ requestId: request.requestId, success: true, code: "CaptureCommitted" })
    },
    tabs: {
      query: async () => [++queries <= 2 ? tab : { ...tab, url: "https://example.com/new-page" }],
      onUpdated: { addListener() {} }, onRemoved: { addListener() {} }
    },
    commands: { onCommand: { addListener() {} } },
    action: {
      setBadgeBackgroundColor: async value => actions.push(value),
      setBadgeText: async value => actions.push(value),
      setTitle: async value => actions.push(value)
    }
  };
  t.after(() => { delete globalThis.chrome; });
  t.mock.method(globalThis, "setTimeout", () => ({}));
  await import("../service-worker.js?integration=navigation");
  const result = await new Promise(resolve => {
    listener({ action: "capture", tabId: tab.id, url: tab.url },
      { id: extensionId, url: chrome.runtime.getURL("popup.html") }, resolve);
  });
  assert.equal(result.success, true);
  assert.equal(result.capturedUrl, tab.url);
  assert.equal(actions.length, 0);
});
