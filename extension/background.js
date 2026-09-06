/* global importScripts, chrome, ProMeterCanonical, ProMeterOperations, ProMeterPageTab, ProMeterCompanionReconnect, ProMeterChunk */
importScripts("canonical.js", "operations.js", "page-tab.js", "companion-reconnect.js", "chunk.js");

var NATIVE_HOST = "com.prometer.bridge";
var MAX_BYTES = ProMeterCanonical.MAX_NATIVE_MESSAGE_BYTES;
var port = null;
var portEpoch = 0;
var connected = false;
var lastError = "";
var reconnect = ProMeterCompanionReconnect.createState();
var reconnectTimer = null;

function utf8ByteLength(text) {
  return ProMeterCanonical.utf8ByteLength(text);
}

function safeLog(message) {
  if (typeof console !== "undefined" && console.log) {
    console.log(String(message || ""));
  }
}

function setConnected(value, error) {
  connected = value === true;
  lastError = error || "";
  chrome.storage.local.set({ companionConnected: connected, companionLastError: lastError });
}

function clearReconnectSchedule() {
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (chrome.alarms && chrome.alarms.clear) {
    chrome.alarms.clear(ProMeterCompanionReconnect.ALARM_NAME);
  }
  reconnect.scheduled = false;
}

function applyDecision(decision) {
  if (!decision) {
    return;
  }
  if (decision.cancelSchedule) {
    clearReconnectSchedule();
  }
  if (decision.schedule && !reconnectTimer) {
    reconnect.scheduled = true;
    safeLog(ProMeterCompanionReconnect.formatDelayLog(decision.delayMs));
    reconnectTimer = setTimeout(runScheduledReconnect, decision.delayMs);
    if (chrome.alarms && chrome.alarms.create) {
      chrome.alarms.create(ProMeterCompanionReconnect.ALARM_NAME, { when: Date.now() + decision.delayMs });
    }
  }
}

function runScheduledReconnect() {
  var decision = ProMeterCompanionReconnect.onReconnectDue(reconnect);
  clearReconnectSchedule();
  if (decision.connect) {
    safeLog("companion reconnect attempt");
    connectNative(false);
  }
}

function scheduleReconnectFromStorage() {
  chrome.storage.local.get({ companionOptIn: false }, function (stored) {
    reconnect.optIn = stored.companionOptIn === true;
    if (port) {
      return;
    }
    applyDecision(ProMeterCompanionReconnect.onDisconnect(reconnect, reconnect.optIn));
  });
}

function disconnectPort() {
  if (!port) {
    return;
  }
  portEpoch += 1;
  try {
    port.onMessage.removeListener(onHostMessage);
  } catch (error) {
  }
  try {
    port.disconnect();
  } catch (error) {
  }
  port = null;
  reconnect.hasPort = false;
}

function onNativeDisconnect(epoch) {
  if (epoch !== portEpoch) {
    return;
  }
  portEpoch += 1;
  var message = chrome.runtime.lastError && chrome.runtime.lastError.message ? chrome.runtime.lastError.message : "disconnected";
  port = null;
  reconnect.hasPort = false;
  setConnected(false, message);
  safeLog("companion disconnected");
  scheduleReconnectFromStorage();
}

function connectNative(fromUser) {
  var decision = fromUser
    ? ProMeterCompanionReconnect.onManualConnect(reconnect)
    : { connect: true, replacePort: false, cancelSchedule: true };
  applyDecision(decision);
  if (fromUser) {
    reconnect.optIn = true;
    chrome.storage.local.set({ companionOptIn: true });
  }
  if (decision.replacePort) {
    disconnectPort();
  }
  if (port) {
    return;
  }
  try {
    port = chrome.runtime.connectNative(NATIVE_HOST);
  } catch (error) {
    setConnected(false, error && error.message ? error.message : "native host unavailable");
    chrome.storage.local.get({ companionOptIn: false }, function (stored) {
      reconnect.optIn = stored.companionOptIn === true;
      applyDecision(ProMeterCompanionReconnect.onConnectFailed(reconnect, reconnect.optIn));
    });
    return;
  }
  portEpoch += 1;
  var epoch = portEpoch;
  ProMeterCompanionReconnect.onPortOpened(reconnect);
  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(function () {
    onNativeDisconnect(epoch);
  });
  port.postMessage({ type: "hello" });
}

function postPayloadTooLarge(requestId, operation) {
  port.postMessage({
    type: "invokeResult",
    requestId: requestId,
    operation: operation,
    status: 0,
    payloadTooLarge: true,
    error: "PayloadTooLarge",
    schemaMismatch: false
  });
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
  var size = utf8ByteLength(json);
  if (size <= MAX_BYTES) {
    port.postMessage(payload);
    return;
  }
  if (!ProMeterChunk.isChunkableOperation(operation) || typeof payload.body !== "string") {
    postPayloadTooLarge(requestId, operation);
    return;
  }
  var frames = ProMeterChunk.buildFrames(requestId, operation, payload);
  if (!frames.ok) {
    postPayloadTooLarge(requestId, operation);
    return;
  }
  safeLog("companion chunked response operation=" + operation + " chunks=" + frames.chunkCount + " projectedBytes=" + frames.totalProjectedBytes);
  for (var i = 0; i < frames.messages.length; i++) {
    port.postMessage(frames.messages[i]);
  }
}

function onHostMessage(message) {
  if (!message || typeof message !== "object") {
    return;
  }
  if (message.type === "helloAck") {
    var accepted = message.accepted === true;
    var ack = ProMeterCompanionReconnect.onHelloAck(reconnect, accepted);
    applyDecision(ack);
    setConnected(accepted, message.error);
    if (accepted) {
      safeLog("companion reconnected");
    }
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

if (chrome.alarms && chrome.alarms.onAlarm) {
  chrome.alarms.onAlarm.addListener(function (alarm) {
    if (!alarm || alarm.name !== ProMeterCompanionReconnect.ALARM_NAME) {
      return;
    }
    chrome.storage.local.get({ companionOptIn: false }, function (stored) {
      reconnect.optIn = stored.companionOptIn === true;
      runScheduledReconnect();
    });
  });
}

chrome.storage.local.get({ companionOptIn: false }, function (stored) {
  var start = ProMeterCompanionReconnect.onServiceWorkerStart(reconnect, stored.companionOptIn === true);
  applyDecision(start);
  if (start.connect) {
    connectNative(false);
  }
});
