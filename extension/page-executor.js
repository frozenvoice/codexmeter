(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory(
      require("./canonical.js"),
      require("./operations.js"),
      require("./project.js"),
      require("./auth.js")
    );
    return;
  }
  var version = root.ProMeterCanonical && root.ProMeterCanonical.PAGE_BRIDGE_VERSION;
  if (!(root.ProMeterPageExecutor && root.ProMeterPageBridge && root.ProMeterPageBridge.version === version)) {
    root.ProMeterPageExecutor = factory(
      root.ProMeterCanonical,
      root.ProMeterOperations,
      root.ProMeterProject,
      root.ProMeterAuth
    );
  }
  root.ProMeterPageBridge = { version: version };
})(typeof globalThis !== "undefined" ? globalThis : this, function (canonical, operations, project, auth) {
  var EXPECTED_ORIGIN = canonical.EXPECTED_ORIGIN;
  var FORBIDDEN = "ChatGPT rejected the page request (403)";

  function currentOrigin(hooks) {
    if (hooks && typeof hooks.origin === "string") {
      return hooks.origin;
    }
    if (typeof location !== "undefined" && location && typeof location.origin === "string") {
      return location.origin;
    }
    return "";
  }

  function toRelativeUrl(url) {
    if (typeof url !== "string" || !url) {
      throw new Error("empty request URL");
    }
    if (url.charAt(0) === "/") {
      var relative = canonical.tryValidate(url);
      if (!relative.ok) {
        throw new Error(relative.error);
      }
      return relative.canonical;
    }
    if (url.indexOf(EXPECTED_ORIGIN) === 0) {
      var sliced = url.slice(EXPECTED_ORIGIN.length) || "/";
      var validated = canonical.tryValidate(sliced);
      if (!validated.ok) {
        throw new Error(validated.error);
      }
      return validated.canonical;
    }
    throw new Error("refusing non-relative ChatGPT request");
  }

  function headerValue(headers, name) {
    if (!headers) {
      return null;
    }
    if (typeof headers.get === "function") {
      return headers.get(name);
    }
    var key = name.toLowerCase();
    var match = Object.keys(headers).filter(function (item) {
      return item.toLowerCase() === key;
    })[0];
    return match ? headers[match] : null;
  }

  function cloneHeaders(init) {
    var headers = {};
    if (!init || !init.headers) {
      return headers;
    }
    var source = init.headers;
    if (typeof source.forEach === "function") {
      source.forEach(function (value, key) {
        headers[key] = value;
      });
      return headers;
    }
    Object.keys(source).forEach(function (key) {
      headers[key] = source[key];
    });
    return headers;
  }

  function stripSecrets(value) {
    if (value && typeof value === "object") {
      if (Object.prototype.hasOwnProperty.call(value, "accessToken")) {
        delete value.accessToken;
      }
      if (Object.prototype.hasOwnProperty.call(value, "access_token")) {
        delete value.access_token;
      }
    }
    var json = JSON.stringify(value);
    if (auth.tokenSnapshot() && json.indexOf(auth.tokenSnapshot()) >= 0) {
      throw new Error("refusing to serialize ChatGPT access token");
    }
    return value;
  }

  var REQUEST_TIMEOUT_MS = 40000;
  var TIMEOUT_ERROR = "bridge request timed out";

  // Race both headers and body consumption against the same operation deadline.
  // Abort alone is insufficient when an underlying promise never observes the signal.
  function abortable(pending, signal) {
    return new Promise(function (resolve, reject) {
      function aborted() { reject(new Error(TIMEOUT_ERROR)); }
      if (signal.aborted) {
        // Still observe a late rejection from an already-started request.
        Promise.resolve(pending).catch(function () {});
        aborted();
        return;
      }
      signal.addEventListener("abort", aborted, { once: true });
      Promise.resolve(pending).then(resolve, reject).finally(function () {
        signal.removeEventListener("abort", aborted);
      });
    });
  }

  async function execute(operation, args, hooks) {
    hooks = hooks || {};
    var controller = new AbortController();
    var timer = setTimeout(function () { controller.abort(); }, hooks.timeoutMs || REQUEST_TIMEOUT_MS);
    try {
      return await abortable(executeCore(operation, args, hooks, controller.signal), controller.signal);
    } catch (error) {
      if (controller.signal.aborted) {
        return { status: 0, error: TIMEOUT_ERROR, diagnostic: "PageRequestTimeout" };
      }
      return { status: 0, error: "page executor failed" };
    } finally {
      clearTimeout(timer);
    }
  }

  async function executeCore(operation, args, hooks, signal) {
    hooks = hooks || {};
    var origin = currentOrigin(hooks);
    if (origin !== EXPECTED_ORIGIN) {
      return { status: 0, schemaMismatch: true, error: "execution origin is not https://chatgpt.com" };
    }

    var built = operations.build(operation, args || {});
    if (!built.ok) {
      return { status: 0, schemaMismatch: true, error: built.error };
    }

    var fetchImpl = hooks.fetchImpl || fetch;
    async function pageFetch(url, init) {
      var relative = toRelativeUrl(url);
      var nextInit = init ? Object.assign({}, init) : {};
      nextInit.credentials = "include";
      nextInit.headers = cloneHeaders(init);
      if (nextInit.headers.Authorization || nextInit.headers.authorization) {
        delete nextInit.headers.Authorization;
        delete nextInit.headers.authorization;
        var authHeaders = auth.authorizationHeader();
        Object.keys(authHeaders).forEach(function (key) {
          nextInit.headers[key] = authHeaders[key];
        });
      }
      if (signal.aborted) {
        throw new Error(TIMEOUT_ERROR);
      }
      nextInit.signal = signal;
      var response = await abortable(fetchImpl(relative, nextInit), signal);
      // Only this small response interface is used by auth/project. Keep the raw
      // response and credentials inside this page; no browser transport changes.
      return {
        status: response.status,
        headers: response.headers,
        text: function () { return abortable(response.text(), signal); }
      };
    }

    try {
      var response;
      if (operation === "GetSessionStatus") {
        var session = await auth.fetchSession(pageFetch);
        if (session.status === 403) {
          return { status: 403, retryAfter: session.retryAfter || null, body: "", error: FORBIDDEN };
        }
        if (!session.json) {
          return {
            status: session.status,
            retryAfter: session.retryAfter || null,
            body: "",
            error: session.status === 401 ? undefined : undefined
          };
        }
        var projectedSession = project.project(operation, session.body);
        if (!projectedSession.ok) {
          return { status: 0, schemaMismatch: true, error: projectedSession.error };
        }
        if (project.containsPromptOrResponseText(projectedSession.body)) {
          return { status: 0, schemaMismatch: true, error: "refusing unsanitized body" };
        }
        var sessionBody = stripSecrets(projectedSession.body);
        return {
          status: session.status,
          retryAfter: session.retryAfter || null,
          body: JSON.stringify(sessionBody),
          error: session.status === 403 ? FORBIDDEN : undefined
        };
      }

      var primed = await auth.ensureSession(pageFetch);
      if (!primed.skipped && primed.status === 403) {
        return { status: 403, retryAfter: primed.retryAfter || null, body: "", error: FORBIDDEN };
      }
      if (!primed.skipped && primed.status && primed.status !== 200) {
        return { status: primed.status, retryAfter: primed.retryAfter || null, body: "" };
      }

      response = await auth.invokeWithRefresh(pageFetch, built.method, built.path, built.body, EXPECTED_ORIGIN);
      var retryAfter = headerValue(response.headers, "retry-after");
      var status = response.status;
      if (status === 403) {
        return { status: 403, retryAfter: retryAfter, body: "", error: FORBIDDEN };
      }

      var text = typeof response.text === "function" ? await response.text() : "";
      if (!text) {
        return { status: status, retryAfter: retryAfter, body: "", error: status === 401 ? undefined : undefined };
      }

      var parsed;
      try {
        parsed = JSON.parse(text);
      } catch (error) {
        return { status: status, retryAfter: retryAfter, body: "" };
      }

      var projected = project.project(operation, parsed);
      if (!projected.ok) {
        if (status >= 200 && status < 300) {
          return { status: 0, schemaMismatch: true, error: projected.error };
        }
        return { status: status, retryAfter: retryAfter, body: "" };
      }
      if (project.containsPromptOrResponseText(projected.body)) {
        return { status: 0, schemaMismatch: true, error: "refusing unsanitized body" };
      }
      var body = stripSecrets(projected.body);
      return { status: status, retryAfter: retryAfter, body: JSON.stringify(body) };
    } catch (error) {
      var message = error && error.message ? error.message : "page executor failed";
      if (message.indexOf("session expired") >= 0 || message.indexOf("ChatGPT session expired") >= 0) {
        message = "page executor failed";
      }
      return { status: 0, error: message };
    }
  }

  return { execute: execute, toRelativeUrl: toRelativeUrl, FORBIDDEN: FORBIDDEN, REQUEST_TIMEOUT_MS: REQUEST_TIMEOUT_MS, TIMEOUT_ERROR: TIMEOUT_ERROR };
});
