/* global importScripts, chrome, ProMeterCanonical, ProMeterOperations, ProMeterPageTab */
importScripts("canonical.js", "operations.js", "page-tab.js");

var NATIVE_HOST = "com.prometer.bridge";
var MAX_BYTES = ProMeterCanonical.MAX_NATIVE_MESSAGE_BYTES;
var port = null;
var connected = false;
var lastError = "";

function utf8ByteLength(text) {
  return ProMeterCanonical.utf8ByteLength(text);
}

function setConnected(value, error) {
  connected = value === true;
  lastError = error || "";
  chrome.storage.local.set({ companionConnected: connected, companionLastError: lastError });
}

function connectNative(fromUser) {
  if (port) {
    return;
  }
  try {
    port = chrome.runtime.connectNative(NATIVE_HOST);
  } catch (error) {
    setConnected(false, error && error.message ? error.message : "native host unavailable");
    return;
  }
  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(function () {
    var message = chrome.runtime.lastError && chrome.runtime.lastError.message ? chrome.runtime.lastError.message : "disconnected";
    port = null;
    setConnected(false, message);
  });
  port.postMessage({ type: "hello" });
  if (fromUser) {
    chrome.storage.local.set({ companionOptIn: true });
  }
}

function postResult(requestId, operation, payload) {
  if (!port) {
    return;
  }
  payload.type = "invokeResult";
  payload.requestId = requestId;
  payload.operation = operation;
  var json = JSON.stringify(payload);
  if (/\baccessToken\b|\baccess_token\b|Bearer\s+/i.test(json)) {
    port.postMessage({ type: "invokeResult", requestId: requestId, operation: operation, status: 0, schemaMismatch: true, error: "refusing to send access token" });
    return;
  }
  if (utf8ByteLength(json) > MAX_BYTES) {
    port.postMessage({ type: "invokeResult", requestId: requestId, operation: operation, payloadTooLarge: true, error: "PayloadTooLarge", schemaMismatch: true });
    return;
  }
  port.postMessage(payload);
}

function onHostMessage(message) {
  if (!message || typeof message !== "object") {
    return;
  }
  if (message.type === "helloAck") {
    setConnected(message.accepted === true, message.error);
    return;
  }
  if (message.type === "error") {
    lastError = message.error || "native host error";
    chrome.storage.local.set({ companionLastError: lastError });
    return;
  }
  if (message.type === "invoke") {
    handleInvoke(message);
  }
}

async function handleInvoke(message) {
  var requestId = message.requestId;
  var operation = message.operation;
  var built = ProMeterOperations.build(operation, message.args || {});
  if (!built.ok) {
    postResult(requestId, operation, { status: 0, schemaMismatch: true, error: built.error });
    return;
  }
  try {
    var result = await ProMeterPageTab.invoke(operation, message.args || {}, chrome);
    if (!result || typeof result !== "object") {
      postResult(requestId, operation, { status: 0, error: ProMeterPageTab.BRIDGE_UNAVAILABLE });
      return;
    }
    postResult(requestId, operation, result);
  } catch (error) {
    postResult(requestId, operation, { status: 0, error: ProMeterPageTab.BRIDGE_UNAVAILABLE });
  }
}

chrome.runtime.onMessage.addListener(function (message, _sender, sendResponse) {
  if (message && message.type === "connect") {
    connectNative(true);
    sendResponse({ connected: connected, error: lastError });
    return true;
  }
  if (message && message.type === "status") {
    sendResponse({ connected: connected, error: lastError });
    return true;
  }
  return false;
});

chrome.storage.local.get({ companionOptIn: false }, function (stored) {
  if (stored.companionOptIn) {
    connectNative(false);
  }
});
