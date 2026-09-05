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
  assert.ok(background.indexOf("importScripts(\"canonical.js\", \"operations.js\", \"page-tab.js\", \"companion-reconnect.js\")") >= 0);
  assert.ok(background.indexOf("chrome.alarms") >= 0);
  assert.ok(background.indexOf("companion reconnect scheduled") >= 0 || background.indexOf("formatDelayLog") >= 0);
  assert.ok(background.indexOf("companionOptIn") >= 0);
  assert.ok(!/pairingToken/.test(background), "background reconnect must not log pairing tokens");

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
          if (details.args && details.args.length === 1) {
            return [{ result: { ready: false } }];
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
      if (String(url).indexOf("/api/auth/session") >= 0) {
        return {
          status: 200,
          headers: { get: function () { return null; } },
          text: async function () { return JSON.stringify({ user: { id: "u1" } }); }
        };
      }
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
  assert.strictEqual(sessionCalls, 2);
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
      if (String(url).indexOf("/api/auth/session") >= 0) {
        return {
          status: 200,
          headers: { get: function () { return null; } },
          text: async function () { return JSON.stringify({ user: { id: "u1" } }); }
        };
      }
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

  const PAGE_TOKEN = "page-local-only-token";
  function loadPageFiles(world, files) {
    files.forEach(function (name) {
      const code = fs.readFileSync(path.join(__dirname, name), "utf8");
      const run = new Function("globalThis", "window", "location", "module", "exports", "require", code);
      run(world, world, { origin: "https://chatgpt.com", href: "https://chatgpt.com/" });
    });
  }

  function createDocumentChrome(initialWorld) {
    const state = {
      world: initialWorld || {},
      injects: [],
      probes: 0,
      operations: [],
      fetches: [],
      authObjects: []
    };
    async function pageFetch(url, init) {
      state.fetches.push({
        url: url,
        hasAuthorization: !!(init && init.headers && init.headers.Authorization)
      });
      if (String(url).indexOf("/api/auth/session") >= 0) {
        return {
          status: 200,
          headers: { get: function () { return null; } },
          text: async function () {
            return JSON.stringify({ accessToken: PAGE_TOKEN, user: { id: "u1", email: "a@b.example" } });
          }
        };
      }
      if (String(url).indexOf("/backend-api/accounts/check") >= 0) {
        return {
          status: 200,
          headers: { get: function () { return null; } },
          text: async function () {
            return JSON.stringify({ accounts: { default: { entitlement: { subscription_plan: "pro" } } } });
          }
        };
      }
      if (String(url).indexOf("/backend-api/models") >= 0) {
        return {
          status: 200,
          headers: { get: function () { return null; } },
          text: async function () { return JSON.stringify({ models: [{ slug: "gpt-6-pro" }] }); }
        };
      }
      return {
        status: 404,
        headers: { get: function () { return null; } },
        text: async function () { return ""; }
      };
    }
    const chromeApi = {
      tabs: {
        query: async function () {
          return [{ id: 11, url: "https://chatgpt.com/", active: true, lastAccessed: 1 }];
        },
        create: async function () {}
      },
      scripting: {
        executeScript: async function (details) {
          if (details.files) {
            state.injects.push(details.files.slice());
            loadPageFiles(state.world, details.files);
            if (state.world.ProMeterAuth) {
              state.authObjects.push(state.world.ProMeterAuth);
            }
            return [{}];
          }
          if (details.args && details.args.length === 1) {
            state.probes += 1;
            const expected = details.args[0];
            const ready = !!(
              state.world.ProMeterPageBridge &&
              state.world.ProMeterPageBridge.version === expected &&
              state.world.ProMeterAuth &&
              typeof state.world.ProMeterAuth.isKnown === "function" &&
              state.world.ProMeterPageExecutor &&
              typeof state.world.ProMeterPageExecutor.execute === "function"
            );
            return [{ result: { ready: ready } }];
          }
          state.operations.push(details.args[0]);
          const result = await state.world.ProMeterPageExecutor.execute(details.args[0], details.args[1], {
            origin: "https://chatgpt.com",
            fetchImpl: pageFetch
          });
          assert.ok(JSON.stringify(result).indexOf(PAGE_TOKEN) < 0);
          assert.ok(JSON.stringify(result).indexOf("accessToken") < 0);
          assert.ok(JSON.stringify(result).indexOf("Bearer ") < 0);
          return [{ result: result }];
        }
      }
    };
    return { chromeApi: chromeApi, state: state };
  }

  const firstDoc = createDocumentChrome();
  const sessionThenCheck = await pageTab.invoke("GetSessionStatus", {}, firstDoc.chromeApi);
  assert.strictEqual(sessionThenCheck.status, 200);
  assert.strictEqual(firstDoc.state.injects.length, 1);
  assert.ok(firstDoc.state.injects[0].indexOf("auth.js") >= 0);
  const authAfterSession = firstDoc.state.world.ProMeterAuth;
  assert.strictEqual(authAfterSession.isKnown(), true);
  assert.strictEqual(firstDoc.state.fetches.filter(function (item) { return item.url.indexOf("/api/auth/session") >= 0; }).length, 1);

  const accountCheck = await pageTab.invoke("GetAccountCheck", {}, firstDoc.chromeApi);
  assert.strictEqual(accountCheck.status, 200);
  assert.strictEqual(firstDoc.state.injects.length, 1, "must not reinject PAGE_FILES when version is unchanged");
  assert.strictEqual(firstDoc.state.world.ProMeterAuth, authAfterSession);
  const sessionFetchesAfterCheck = firstDoc.state.fetches.filter(function (item) { return item.url.indexOf("/api/auth/session") >= 0; });
  assert.strictEqual(sessionFetchesAfterCheck.length, 1, "must not refetch session when auth is known");
  const checkFetch = firstDoc.state.fetches.filter(function (item) { return item.url.indexOf("/backend-api/accounts/check") >= 0; })[0];
  assert.ok(checkFetch && checkFetch.hasAuthorization === true);
  assert.ok(JSON.stringify(sessionThenCheck).indexOf(PAGE_TOKEN) < 0);
  assert.ok(JSON.stringify(accountCheck).indexOf(PAGE_TOKEN) < 0);

  const modelsReuse = await pageTab.invoke("GetModels", {}, firstDoc.chromeApi);
  assert.strictEqual(modelsReuse.status, 200);
  assert.strictEqual(firstDoc.state.injects.length, 1);
  const modelFetch = firstDoc.state.fetches.filter(function (item) { return item.url.indexOf("/backend-api/models") >= 0; })[0];
  assert.ok(modelFetch && modelFetch.hasAuthorization === true);
  assert.strictEqual(firstDoc.state.fetches.filter(function (item) { return item.url.indexOf("/api/auth/session") >= 0; }).length, 1);

  firstDoc.state.world = {};
  const afterNavigation = await pageTab.invoke("GetModels", {}, firstDoc.chromeApi);
  assert.strictEqual(afterNavigation.status, 200);
  assert.strictEqual(firstDoc.state.injects.length, 2, "a new document must reinject the page bridge");
  assert.ok(JSON.stringify(afterNavigation).indexOf(PAGE_TOKEN) < 0);

  const versionDoc = createDocumentChrome();
  await pageTab.invoke("GetSessionStatus", {}, versionDoc.chromeApi);
  assert.strictEqual(versionDoc.state.injects.length, 1);
  const authBeforeMismatch = versionDoc.state.world.ProMeterAuth;
  versionDoc.state.world.ProMeterPageBridge = { version: pageTab.PAGE_BRIDGE_VERSION - 1 };
  const afterMismatch = await pageTab.invoke("GetModels", {}, versionDoc.chromeApi);
  assert.strictEqual(afterMismatch.status, 200);
  assert.strictEqual(versionDoc.state.injects.length, 2, "a version mismatch must reinject");
  assert.notStrictEqual(versionDoc.state.world.ProMeterAuth, authBeforeMismatch);
  assert.strictEqual(versionDoc.state.world.ProMeterPageBridge.version, pageTab.PAGE_BRIDGE_VERSION);

  auth.invalidate();
  let backend401 = 0;
  let session401 = 0;
  let sawForbidden = false;
  const refreshThenForbidden = await pageExecutor.execute("GetModels", {}, {
    origin: "https://chatgpt.com",
    fetchImpl: async function (url) {
      if (String(url).indexOf("/api/auth/session") >= 0) {
        session401 += 1;
        return {
          status: 200,
          headers: { get: function () { return null; } },
          text: async function () {
            return JSON.stringify({ accessToken: PAGE_TOKEN, user: { id: "u1" } });
          }
        };
      }
      backend401 += 1;
      if (backend401 === 1) {
        return {
          status: 401,
          headers: { get: function () { return null; } },
          text: async function () { return "unauthorized"; }
        };
      }
      return {
        status: 403,
        headers: { get: function () { return null; } },
        text: async function () { return "{\"detail\":\"auth required\"}"; }
      };
    }
  });
  assert.strictEqual(backend401, 2);
  assert.strictEqual(session401, 2);
  assert.strictEqual(refreshThenForbidden.status, 403);
  assert.strictEqual(refreshThenForbidden.error, pageExecutor.FORBIDDEN);
  assert.ok(JSON.stringify(refreshThenForbidden).indexOf("expired") < 0);
  assert.ok(JSON.stringify(refreshThenForbidden).indexOf(PAGE_TOKEN) < 0);
  sawForbidden = refreshThenForbidden.error === pageExecutor.FORBIDDEN;
  assert.strictEqual(sawForbidden, true);
};
