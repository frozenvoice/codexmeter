/* global importScripts, chrome, ProMeterSanitize */
importScripts("sanitize.js");

var NATIVE_HOST = "com.prometer.bridge";
var port = null;
var connected = false;

function setConnected(value) {
  connected = value;
  chrome.storage.local.set({ companionConnected: value });
}

function connectNative() {
  if (port) {
    return;
  }
  try {
    port = chrome.runtime.connectNative(NATIVE_HOST);
  } catch (error) {
    setConnected(false);
    return;
  }
  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(function () {
    port = null;
    setConnected(false);
  });
  port.postMessage({ type: "hello" });
}

function postResult(requestId, payload) {
  if (!port) {
    return;
  }
  payload.type = "fetchResult";
  payload.requestId = requestId;
  port.postMessage(payload);
}

function onHostMessage(message) {
  if (!message || typeof message !== "object") {
    return;
  }
  if (message.type === "helloAck") {
    setConnected(message.accepted === true);
    return;
  }
  if (message.type !== "fetch") {
    return;
  }
  handleFetch(message);
}

function handleFetch(message) {
  var requestId = message.requestId;
  var method = message.method || "GET";
  var path = message.path;
  if (!ProMeterSanitize.isAllowedTarget(path)) {
    postResult(requestId, {
      status: 0,
      schemaMismatch: true,
      error: "target rejected before fetch"
    });
    return;
  }

  var init = {
    method: method,
    credentials: "include",
    headers: { Accept: "application/json" }
  };
  if (message.body) {
    init.headers["Content-Type"] = "application/json";
    init.body = message.body;
  }

  fetch("https://chatgpt.com" + path, init)
    .then(function (response) {
      return response.text().then(function (text) {
        var sanitized = "";
        try {
          sanitized = text ? ProMeterSanitize.sanitizeJsonText(text) : "";
        } catch (error) {
          postResult(requestId, {
            status: 0,
            schemaMismatch: true,
            error: "malformed ChatGPT body"
          });
          return;
        }
        if (sanitized && ProMeterSanitize.containsPromptOrResponseText(JSON.parse(sanitized))) {
          postResult(requestId, {
            status: 0,
            schemaMismatch: true,
            error: "refusing unsanitized body"
          });
          return;
        }
        postResult(requestId, {
          status: response.status,
          retryAfter: response.headers.get("retry-after"),
          body: sanitized
        });
      });
    })
    .catch(function (error) {
      postResult(requestId, {
        status: 0,
        error: error && error.message ? error.message : "offline"
      });
    });
}

chrome.runtime.onMessage.addListener(function (message, _sender, sendResponse) {
  if (message && message.type === "connect") {
    connectNative();
    sendResponse({ connected: connected });
    return true;
  }
  if (message && message.type === "status") {
    sendResponse({ connected: connected });
    return true;
  }
  return false;
});
