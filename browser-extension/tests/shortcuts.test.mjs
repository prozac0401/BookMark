import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const tab = { id: 7, windowId: 2, active: true, status: "complete", url: "https://example.com/?test=shortcut#part", title: "Shortcut fixture" };

test("new installation avoids the Edge Collections shortcut without extra permissions", async () => {
  const manifest = JSON.parse(await readFile(new URL("../manifest.json", import.meta.url)));
  assert.equal(manifest.commands["capture-current-tab"].suggested_key.windows, "Ctrl+Shift+9");
  assert.deepEqual(manifest.permissions.toSorted(), ["activeTab", "nativeMessaging"]);
});

test("keyboard capture sends the invoked tab once and waits for the committed reply", async t => {
  let command, respond;
  const sent = [], badges = [];
  globalThis.chrome = {
    commands: { onCommand: { addListener: fn => { command = fn; } } },
    runtime: {
      onMessage: { addListener() {} },
      sendNativeMessage: (_host, request, callback) => { sent.push(request); respond = callback; }
    },
    tabs: { query: async () => [tab], onUpdated: { addListener() {} }, onRemoved: { addListener() {} } },
    action: {
      setBadgeBackgroundColor: async () => {},
      setBadgeText: async value => badges.push(value),
      setTitle: async () => {}
    }
  };
  t.after(() => { delete globalThis.chrome; });
  t.mock.method(globalThis, "setTimeout", () => ({}));
  await import("../service-worker.js?test=keyboard");
  command("unrelated-command", tab);
  await new Promise(setImmediate);
  assert.equal(sent.length, 0);
  command("capture-current-tab", tab);
  await new Promise(setImmediate);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].url, tab.url);
  assert.equal(badges.length, 0, "no success before the native commit acknowledgement");
  respond({ requestId: sent[0].requestId, success: true, code: "CaptureCommitted" });
  await new Promise(setImmediate);
  assert.deepEqual(badges, [{ tabId: tab.id, text: "✓" }]);
});

test("keyboard capture refuses a tab that changed after the command invocation", async t => {
  let command;
  const sent = [];
  globalThis.chrome = {
    commands: { onCommand: { addListener: fn => { command = fn; } } },
    runtime: { onMessage: { addListener() {} }, sendNativeMessage: (...args) => sent.push(args) },
    tabs: { query: async () => [{ ...tab, id: 8 }], onUpdated: { addListener() {} }, onRemoved: { addListener() {} } },
    action: { setBadgeBackgroundColor: async () => {}, setBadgeText: async () => {}, setTitle: async () => {} }
  };
  t.after(() => { delete globalThis.chrome; });
  t.mock.method(globalThis, "setTimeout", () => ({}));
  await import("../service-worker.js?test=keyboard-changed");
  command("capture-current-tab", tab);
  await new Promise(setImmediate);
  assert.equal(sent.length, 0);
});

for (const [name, commandResult, expectedText, userAgent, settingsUrl] of [
  ["unassigned Edge shortcut", [{ name: "capture-current-tab", shortcut: "" }], "배정되지 않았습니다", "Mozilla/5.0 Chrome/140.0 Edg/140.0", "edge://extensions/shortcuts"],
  ["custom Chrome shortcut", [{ name: "capture-current-tab", shortcut: "Alt+Shift+7" }], "현재 저장 단축키: Alt+Shift+7", "Mozilla/5.0 Chrome/140.0", "chrome://extensions/shortcuts"],
  ["shortcut query failure", null, "현재 단축키를 확인하지 못했습니다", "Mozilla/5.0 Chrome/140.0", "chrome://extensions/shortcuts"]
]) {
  test("popup recovers " + name + " without claiming a default is active", async t => {
    const elements = new Map();
    const created = [], messages = [];
    const originalNavigator = Object.getOwnPropertyDescriptor(globalThis, "navigator");
    globalThis.document = { querySelector: selector => {
      if (!elements.has(selector)) elements.set(selector, {
        textContent: "", disabled: selector === "#capture", dataset: {},
        addEventListener(event, listener) { this[event] = listener; }
      });
      return elements.get(selector);
    } };
    Object.defineProperty(globalThis, "navigator", { configurable: true, value: { userAgent } });
    globalThis.chrome = {
      runtime: { sendMessage: async message => { messages.push(message); return { success: true, tabId: tab.id, url: tab.url, title: tab.title }; } },
      commands: { getAll: async () => { if (!commandResult) throw new Error("Unavailable"); return commandResult; } },
      tabs: { create: async options => created.push(options) }
    };
    t.after(() => {
      delete globalThis.chrome; delete globalThis.document;
      if (originalNavigator) Object.defineProperty(globalThis, "navigator", originalNavigator);
      else delete globalThis.navigator;
    });
    await import("../popup.js?test=" + encodeURIComponent(name));
    assert.ok(elements.get("#shortcut").textContent.includes(expectedText));
    assert.equal(elements.get("#shortcut").textContent.includes("Ctrl+Shift+Y"), false);
    assert.equal(elements.get("#capture").disabled, false, "manual capture remains available without a keyboard shortcut");
    await elements.get("#shortcut-settings").click();
    assert.deepEqual(created, [{ url: settingsUrl }]);
    assert.deepEqual(messages, [{ action: "preview" }], "opening settings must not save a page");
  });
}
