(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("./canonical.js"));
  } else {
    root.ProMeterPageTab = factory(root.ProMeterCanonical);
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function (canonical) {
  var EXPECTED_ORIGIN = canonical.EXPECTED_ORIGIN;
  var PAGE_BRIDGE_VERSION = canonical.PAGE_BRIDGE_VERSION;
  var CHATGPT_HOME = EXPECTED_ORIGIN + "/";
  var PAGE_FILES = ["canonical.js", "operations.js", "project.js", "auth.js", "page-executor.js"];
  var NO_TAB = "Open/sign in to ChatGPT, then retry";
  var BRIDGE_UNAVAILABLE = "ChatGPT page bridge unavailable";

  function isExactChatGptTabUrl(urlString) {
    if (!urlString || typeof urlString !== "string") {
      return false;
    }
    var url;
    try {
      url = new URL(urlString);
    } catch (error) {
      return false;
    }
    return url.origin === EXPECTED_ORIGIN && url.protocol === "https:" && (!url.port || url.port === "443") && !url.username && !url.password;
  }

  function pickPreferredTab(tabs) {
    var exact = (tabs || []).filter(function (tab) {
      return tab && isExactChatGptTabUrl(tab.url);
    });
    if (exact.length === 0) {
      return null;
    }
    exact.sort(function (a, b) {
      if (a.active !== b.active) {
        return a.active ? -1 : 1;
      }
      return (b.lastAccessed || 0) - (a.lastAccessed || 0);
    });
    return exact[0];
  }

  async function queryChatGptTabs(chromeApi) {
    var query = chromeApi.tabs.query({ url: "https://chatgpt.com/*" });
    return typeof query.then === "function" ? await query : await new Promise(function (resolve, reject) {
      chromeApi.tabs.query({ url: "https://chatgpt.com/*" }, function (tabs) {
        var err = chromeApi.runtime && chromeApi.runtime.lastError;
        if (err) {
          reject(new Error(err.message || BRIDGE_UNAVAILABLE));
          return;
        }
        resolve(tabs || []);
      });
    });
  }

  async function openChatGptTab(chromeApi) {
    var created = chromeApi.tabs.create({ url: CHATGPT_HOME, active: true });
    if (created && typeof created.then === "function") {
      await created;
      return;
    }
    await new Promise(function (resolve, reject) {
      chromeApi.tabs.create({ url: CHATGPT_HOME, active: true }, function () {
        var err = chromeApi.runtime && chromeApi.runtime.lastError;
        if (err) {
          reject(new Error(err.message || BRIDGE_UNAVAILABLE));
          return;
        }
        resolve();
      });
    });
  }

  function pageBridgeStatus(expectedVersion) {
    var bridge = globalThis.ProMeterPageBridge;
    var auth = globalThis.ProMeterAuth;
    var executor = globalThis.ProMeterPageExecutor;
    return {
      ready: !!(
        bridge &&
        bridge.version === expectedVersion &&
        auth &&
        typeof auth.isKnown === "function" &&
        executor &&
        typeof executor.execute === "function"
      )
    };
  }

  function pageExecute(operation, args) {
    return globalThis.ProMeterPageExecutor.execute(operation, args);
  }

  async function runExecuteScript(chromeApi, details) {
    var pending = chromeApi.scripting.executeScript(details);
    if (pending && typeof pending.then === "function") {
      return await pending;
    }
    return await new Promise(function (resolve, reject) {
      chromeApi.scripting.executeScript(details, function (value) {
        var err = chromeApi.runtime && chromeApi.runtime.lastError;
        if (err) {
          reject(new Error(err.message || BRIDGE_UNAVAILABLE));
          return;
        }
        resolve(value);
      });
    });
  }

  async function ensurePageBridge(chromeApi, tabId) {
    var probed = await runExecuteScript(chromeApi, {
      target: { tabId: tabId },
      world: "MAIN",
      func: pageBridgeStatus,
      args: [PAGE_BRIDGE_VERSION]
    });
    var status = probed && probed[0] ? probed[0].result : null;
    if (status && status.ready === true) {
      return "reused";
    }
    await runExecuteScript(chromeApi, {
      target: { tabId: tabId },
      world: "MAIN",
      files: PAGE_FILES
    });
    return "injected";
  }

  async function executeOnTab(chromeApi, tabId, operation, args) {
    await ensurePageBridge(chromeApi, tabId);
    var results = await runExecuteScript(chromeApi, {
      target: { tabId: tabId },
      world: "MAIN",
      func: pageExecute,
      args: [operation, args || {}]
    });
    return results && results[0] ? results[0].result : null;
  }

  async function invoke(operation, args, chromeApi) {
    if (!chromeApi || !chromeApi.tabs || !chromeApi.scripting || typeof chromeApi.scripting.executeScript !== "function") {
      return { status: 0, error: BRIDGE_UNAVAILABLE, diagnostic: "PageBridgeUnavailable" };
    }

    var tabs;
    try {
      tabs = await queryChatGptTabs(chromeApi);
    } catch (error) {
      return { status: 0, error: BRIDGE_UNAVAILABLE, diagnostic: "PageBridgeUnavailable" };
    }

    var tab = pickPreferredTab(tabs);
    if (!tab || typeof tab.id !== "number") {
      try {
        await openChatGptTab(chromeApi);
      } catch (error) {
        return { status: 0, error: BRIDGE_UNAVAILABLE, diagnostic: "PageBridgeUnavailable" };
      }
      return { status: 0, error: NO_TAB, diagnostic: "NoChatGptTab" };
    }

    try {
      var result = await executeOnTab(chromeApi, tab.id, operation, args);
      if (!result || typeof result !== "object") {
        return { status: 0, error: BRIDGE_UNAVAILABLE, diagnostic: "PageBridgeUnavailable" };
      }
      return result;
    } catch (error) {
      return { status: 0, error: BRIDGE_UNAVAILABLE, diagnostic: "PageBridgeUnavailable" };
    }
  }

  return {
    invoke: invoke,
    isExactChatGptTabUrl: isExactChatGptTabUrl,
    pickPreferredTab: pickPreferredTab,
    pageBridgeStatus: pageBridgeStatus,
    PAGE_BRIDGE_VERSION: PAGE_BRIDGE_VERSION,
    NO_TAB: NO_TAB,
    BRIDGE_UNAVAILABLE: BRIDGE_UNAVAILABLE,
    PAGE_FILES: PAGE_FILES,
    CHATGPT_HOME: CHATGPT_HOME
  };
});
