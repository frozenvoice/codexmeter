module.exports = async function testPageContext() {
  const assert = require("assert");
  const fs = require("fs");
  const path = require("path");
  const pageTab = require("./page-tab.js");
  const pageExecutor = require("./page-executor.js");
  const auth = require("./auth.js");

  const background = fs.readFileSync(path.join(__dirname, "background.js"), "utf8");
  assert.ok(background.indexOf("ProMeterPageTab.invoke") >= 0);
  assert.ok(background.indexOf("handleInvoke") >= 0);
  assert.ok(background.indexOf("invokeWithRefresh") < 0);
  assert.ok(!/\bfetch\s*\(/.test(background), "service worker must not fetch ChatGPT directly");
  assert.ok(background.indexOf("importScripts(\"canonical.js\", \"operations.js\", \"page-tab.js\")") >= 0);

  assert.strictEqual(pageTab.isExactChatGptTabUrl("https://chatgpt.com/"), true);
  assert.strictEqual(pageTab.isExactChatGptTabUrl("https://chatgpt.com/c/abc"), true);
  assert.strictEqual(pageTab.isExactChatGptTabUrl("https://chatgpt.com.evil.example/"), false);
  assert.strictEqual(pageTab.isExactChatGptTabUrl("https://notchatgpt.com/"), false);
  assert.strictEqual(pageTab.isExactChatGptTabUrl("http://chatgpt.com/"), false);
  assert.strictEqual(pageTab.isExactChatGptTabUrl("https://chatgpt.com:443/"), true);
  assert.strictEqual(pageTab.isExactChatGptTabUrl("https://chatgpt.com:8443/"), false);
  assert.strictEqual(pageTab.isExactChatGptTabUrl("https://user@chatgpt.com/"), false);

  const preferred = pageTab.pickPreferredTab([
    { id: 1, url: "https://chatgpt.com.evil.example/", active: true, lastAccessed: 99 },
    { id: 2, url: "https://chatgpt.com/c/old", active: false, lastAccessed: 1 },
    { id: 3, url: "https://chatgpt.com/", active: true, lastAccessed: 5 }
  ]);
  assert.strictEqual(preferred.id, 3);

  assert.strictEqual(pageTab.pickPreferredTab([
    { id: 8, url: "https://chat.openai.com/", active: true }
  ]), null);

  let created = null;
  let executedTabId = null;
  let chatgptFetches = 0;
  function mockChrome(tabs) {
    return {
      tabs: {
        query: async function () {
          return tabs;
        },
        create: async function (info) {
          created = info;
        }
      },
      scripting: {
        executeScript: async function (details) {
          if (details.files) {
            return [{ result: undefined }];
          }
          executedTabId = details.target.tabId;
          const result = await pageExecutor.execute(details.args[0], details.args[1], {
            origin: "https://chatgpt.com",
            fetchImpl: async function () {
              chatgptFetches += 1;
              return {
                status: 200,
                headers: { get: function () { return null; } },
                text: async function () { return JSON.stringify({ models: [] }); }
              };
            }
          });
          return [{ result: result }];
        }
      }
    };
  }

  created = null;
  executedTabId = null;
  const dispatched = await pageTab.invoke("GetModels", {}, mockChrome([
    { id: 1, url: "https://chatgpt.com.evil.example/", active: true, lastAccessed: 9 },
    { id: 4, url: "https://chatgpt.com/c/abc", active: true, lastAccessed: 3 }
  ]));
  assert.strictEqual(executedTabId, 4);
  assert.strictEqual(dispatched.status, 200);
  assert.strictEqual(created, null);

  created = null;
  executedTabId = null;
  const lookalikes = await pageTab.invoke("GetModels", {}, mockChrome([
    { id: 1, url: "https://chatgpt.com.evil.example/", active: true },
    { id: 2, url: "https://notchatgpt.com/", active: true }
  ]));
  assert.strictEqual(lookalikes.error, pageTab.NO_TAB);
  assert.strictEqual(lookalikes.diagnostic, "NoChatGptTab");
  assert.strictEqual(executedTabId, null);
  assert.deepStrictEqual(created, { url: pageTab.CHATGPT_HOME, active: true });

  created = null;
  executedTabId = null;
  const missing = await pageTab.invoke("GetModels", {}, mockChrome([]));
  assert.strictEqual(missing.error, pageTab.NO_TAB);
  assert.strictEqual(missing.diagnostic, "NoChatGptTab");
  assert.strictEqual(executedTabId, null);
  assert.ok(created && created.url === pageTab.CHATGPT_HOME);

  const noBridge = await pageTab.invoke("GetModels", {}, { tabs: { query: async function () { return []; } } });
  assert.strictEqual(noBridge.error, pageTab.BRIDGE_UNAVAILABLE);
  assert.strictEqual(noBridge.diagnostic, "PageBridgeUnavailable");

  const queryFail = await pageTab.invoke("GetModels", {}, {
    tabs: {
      query: async function () { throw new Error("no tab api"); },
      create: async function () {}
    },
    scripting: { executeScript: async function () { return []; } }
  });
  assert.strictEqual(queryFail.error, pageTab.BRIDGE_UNAVAILABLE);

  const injectFail = await pageTab.invoke("GetModels", {}, {
    tabs: {
      query: async function () { return [{ id: 9, url: "https://chatgpt.com/", active: true }]; },
      create: async function () {}
    },
    scripting: {
      executeScript: async function () { throw new Error("MAIN world blocked"); }
    }
  });
  assert.strictEqual(injectFail.error, pageTab.BRIDGE_UNAVAILABLE);
  assert.ok(JSON.stringify(injectFail).indexOf("VPN") < 0);

  assert.throws(function () { pageExecutor.toRelativeUrl("https://example.invalid/backend-api/models"); });
  assert.throws(function () { pageExecutor.toRelativeUrl("https://chatgpt.com.evil.example/backend-api/models"); });
  assert.strictEqual(pageExecutor.toRelativeUrl("https://chatgpt.com/backend-api/models"), "/backend-api/models");
  assert.strictEqual(pageExecutor.toRelativeUrl("/backend-api/models"), "/backend-api/models");

  const wrongOrigin = await pageExecutor.execute("GetModels", {}, { origin: "https://chatgpt.com.evil.example" });
  assert.strictEqual(wrongOrigin.schemaMismatch, true);
  assert.ok(wrongOrigin.error.indexOf("https://chatgpt.com") >= 0);

  const urls = [];
  auth.invalidate();
  const allowed = await pageExecutor.execute("GetModels", {}, {
    origin: "https://chatgpt.com",
    fetchImpl: async function (url) {
      urls.push(url);
      return {
        status: 200,
        headers: { get: function () { return null; } },
        text: async function () { return JSON.stringify({ models: [{ slug: "gpt-6-pro" }] }); }
      };
    }
  });
  assert.strictEqual(allowed.status, 200);
  assert.ok(urls.length >= 1);
  urls.forEach(function (url) {
    assert.strictEqual(url.charAt(0), "/", "page executor must use relative URLs: " + url);
  });
  assert.ok(urls.indexOf("/backend-api/models") >= 0);

  async function statusFetch(status, body, retryAfter) {
    return {
      origin: "https://chatgpt.com",
      fetchImpl: async function () {
        return {
          status: status,
          headers: { get: function (name) { return name && name.toLowerCase() === "retry-after" ? retryAfter : null; } },
          text: async function () { return body; }
        };
      }
    };
  }

  const unauthorized = await pageExecutor.execute("GetModels", {}, await statusFetch(401, "unauthorized", null));
  assert.strictEqual(unauthorized.status, 401);
  assert.ok(!unauthorized.error || String(unauthorized.error).indexOf("expired") < 0);
  assert.ok(JSON.stringify(unauthorized).indexOf("ChatGPT session expired") < 0);

  const forbidden = await pageExecutor.execute("GetModels", {}, await statusFetch(403, "{\"detail\":\"auth required\"}", "7"));
  assert.strictEqual(forbidden.status, 403);
  assert.strictEqual(forbidden.error, pageExecutor.FORBIDDEN);
  assert.strictEqual(forbidden.retryAfter, "7");
  assert.strictEqual(forbidden.body, "");
  assert.ok(JSON.stringify(forbidden).indexOf("session expired") < 0);
  assert.ok(JSON.stringify(forbidden).indexOf("ChatGPT session expired") < 0);
  assert.ok(JSON.stringify(forbidden).indexOf("auth required") < 0);

  const limited = await pageExecutor.execute("GetModels", {}, await statusFetch(429, "slow down", "12"));
  assert.strictEqual(limited.status, 429);
  assert.strictEqual(limited.retryAfter, "12");

  const server = await pageExecutor.execute("GetModels", {}, await statusFetch(503, "unavailable", null));
  assert.strictEqual(server.status, 503);

  let backendCalls = 0;
  let sessionCalls = 0;
  auth.invalidate();
  const refreshed = await pageExecutor.execute("GetModels", {}, {
    origin: "https://chatgpt.com",
    fetchImpl: async function (url) {
      if (String(url).indexOf("/api/auth/session") >= 0) {
        sessionCalls += 1;
        return {
          status: 200,
          headers: { get: function () { return null; } },
          text: async function () {
            return JSON.stringify({ accessToken: "page-local-only-token", user: { id: "u1", email: "a@b.example" } });
          }
        };
      }
      backendCalls += 1;
      return {
        status: backendCalls === 1 ? 401 : 200,
        headers: { get: function () { return null; } },
        text: async function () {
          return backendCalls === 1 ? "unauthorized" : JSON.stringify({ models: [{ slug: "gpt-6-pro" }] });
        }
      };
    }
  });
  assert.strictEqual(backendCalls, 2);
  assert.strictEqual(sessionCalls, 1);
  assert.strictEqual(refreshed.status, 200);
  assert.ok(JSON.stringify(refreshed).indexOf("page-local-only-token") < 0);
  assert.ok(JSON.stringify(refreshed).indexOf("accessToken") < 0);

  auth.invalidate();
  const sessionProjected = await pageExecutor.execute("GetSessionStatus", {}, {
    origin: "https://chatgpt.com",
    fetchImpl: async function (url) {
      assert.strictEqual(url, "/api/auth/session");
      return {
        status: 200,
        headers: { get: function () { return null; } },
        text: async function () {
          return JSON.stringify({
            accessToken: "page-local-only-token",
            user: { id: "u1", email: "a@b.example" }
          });
        }
      };
    }
  });
  assert.strictEqual(sessionProjected.status, 200);
  const sessionBody = JSON.parse(sessionProjected.body);
  assert.strictEqual(sessionBody.accessToken, undefined);
  assert.ok(JSON.stringify(sessionProjected).indexOf("page-local-only-token") < 0);

  const conversation = await pageExecutor.execute("GetConversationFull", { conversationId: "conv-1" }, {
    origin: "https://chatgpt.com",
    fetchImpl: async function (url) {
      assert.ok(url.indexOf("/backend-api/conversation/conv-1") === 0);
      assert.strictEqual(url.charAt(0), "/");
      return {
        status: 200,
        headers: { get: function () { return null; } },
        text: async function () {
          return JSON.stringify({
            conversation_id: "conv-1",
            mapping: {
              asst: {
                id: "asst-1",
                parent: "user-1",
                message: {
                  id: "asst-1",
                  author: { role: "assistant" },
                  content: { content_type: "text", parts: ["SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE"], text: "SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE" },
                  metadata: { request_id: "req-1", model_slug: "gpt-6-pro" }
                }
              }
            }
          });
        }
      };
    }
  });
  assert.strictEqual(conversation.status, 200);
  assert.ok(conversation.body.indexOf("SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE") < 0);
  assert.ok(conversation.body.indexOf("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE") < 0);
  const projectedConversation = JSON.parse(conversation.body);
  assert.strictEqual(projectedConversation.mapping.asst.message.metadata.request_id, "req-1");
  assert.ok(!projectedConversation.mapping.asst.message.content.parts);
  assert.ok(!projectedConversation.mapping.asst.message.content.text);
};
