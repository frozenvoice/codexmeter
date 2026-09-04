(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
    return;
  }
  var version = root.ProMeterCanonical && root.ProMeterCanonical.PAGE_BRIDGE_VERSION;
  if (root.ProMeterAuth && root.ProMeterPageBridge && root.ProMeterPageBridge.version === version) {
    return;
  }
  root.ProMeterAuth = factory();
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var memory = { token: null, known: false };
  var inflight = null;

  function tokenSnapshot() {
    return memory.token;
  }

  function isKnown() {
    return memory.known === true;
  }

  function invalidate() {
    memory.token = null;
    memory.known = false;
  }

  function applySession(payload) {
    memory.known = true;
    memory.token = payload && typeof payload.accessToken === "string" && payload.accessToken ? payload.accessToken : null;
  }

  function authorizationHeader() {
    return memory.token ? { Authorization: "Bearer " + memory.token } : {};
  }

  function neverSerializeToken(obj) {
    var json = JSON.stringify(obj);
    if (memory.token && json.indexOf(memory.token) >= 0) {
      throw new Error("refusing to serialize ChatGPT access token");
    }
    return json;
  }

  function retryAfterOf(response) {
    if (!response || !response.headers) {
      return null;
    }
    if (typeof response.headers.get === "function") {
      return response.headers.get("retry-after");
    }
    return response.headers["retry-after"] || response.headers["Retry-After"] || null;
  }

  async function fetchSessionOnce(fetchImpl) {
    var fetchFn = fetchImpl || fetch;
    var response = await fetchFn("https://chatgpt.com/api/auth/session", {
      method: "GET",
      credentials: "include",
      headers: { Accept: "application/json" }
    });
    var text = await response.text();
    var payload = null;
    try {
      payload = text ? JSON.parse(text) : null;
    } catch (error) {
      invalidate();
      return { status: response.status, retryAfter: retryAfterOf(response), bodyText: text, json: false };
    }
    applySession(payload);
    return { status: response.status, retryAfter: retryAfterOf(response), body: payload, json: true };
  }

  async function fetchSession(fetchImpl) {
    if (!inflight) {
      inflight = fetchSessionOnce(fetchImpl).then(function (result) {
        inflight = null;
        return result;
      }, function (error) {
        inflight = null;
        throw error;
      });
    }
    return inflight;
  }

  async function ensureSession(fetchImpl) {
    if (memory.known) {
      return { status: 200, skipped: true, json: true };
    }
    return fetchSession(fetchImpl);
  }

  async function sendApproved(fetchImpl, method, path, body, origin) {
    var fetchFn = fetchImpl || fetch;
    var headers = Object.assign({ Accept: "application/json" }, authorizationHeader());
    var init = { method: method, credentials: "include", headers: headers };
    if (body) {
      headers["Content-Type"] = "application/json";
      init.body = body;
    }
    var response = await fetchFn(origin + path, init);
    return response;
  }

  async function invokeWithRefresh(fetchImpl, method, path, body, origin) {
    var first = await sendApproved(fetchImpl, method, path, body, origin);
    if (first.status !== 401) {
      return first;
    }
    invalidate();
    var refreshed = await fetchSession(fetchImpl);
    if (refreshed.status !== 200) {
      return {
        status: refreshed.status,
        headers: { get: function () { return refreshed.retryAfter; } },
        text: async function () { return refreshed.bodyText || ""; },
        refreshFailure: true
      };
    }
    return sendApproved(fetchImpl, method, path, body, origin);
  }

  return {
    tokenSnapshot: tokenSnapshot,
    isKnown: isKnown,
    invalidate: invalidate,
    applySession: applySession,
    authorizationHeader: authorizationHeader,
    neverSerializeToken: neverSerializeToken,
    fetchSession: fetchSession,
    ensureSession: ensureSession,
    invokeWithRefresh: invokeWithRefresh
  };
});
