(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }
  root.ProMeterCompanionReconnect = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var DELAYS_MS = [1000, 2000, 5000, 10000, 30000];
  var ALARM_NAME = "prometer-companion-reconnect";

  function createState() {
    return {
      backoffIndex: 0,
      scheduled: false,
      hasPort: false,
      optIn: false,
      lastScheduledDelayMs: 0
    };
  }

  function nextDelayMs(state) {
    return DELAYS_MS[Math.min(state.backoffIndex, DELAYS_MS.length - 1)];
  }

  function onServiceWorkerStart(state, optIn) {
    state.optIn = optIn === true;
    state.scheduled = false;
    if (state.optIn && !state.hasPort) {
      return { connect: true, cancelSchedule: true };
    }
    return { connect: false, cancelSchedule: false };
  }

  function onManualConnect(state) {
    state.optIn = true;
    state.backoffIndex = 0;
    state.scheduled = false;
    return { connect: true, replacePort: state.hasPort, cancelSchedule: true };
  }

  function onPortOpened(state) {
    state.hasPort = true;
    state.scheduled = false;
    return { cancelSchedule: true };
  }

  function onHelloAck(state, accepted) {
    if (accepted === true) {
      state.backoffIndex = 0;
      state.scheduled = false;
      state.hasPort = true;
      return { connected: true, resetBackoff: true, cancelSchedule: true };
    }
    return { connected: false, resetBackoff: false, cancelSchedule: false };
  }

  function onDisconnect(state, optIn) {
    state.hasPort = false;
    state.optIn = optIn === true;
    if (!state.optIn) {
      state.scheduled = false;
      return { schedule: false, cancelSchedule: true };
    }
    if (state.scheduled) {
      return { schedule: false, alreadyScheduled: true, cancelSchedule: false };
    }
    var delayMs = nextDelayMs(state);
    state.scheduled = true;
    state.lastScheduledDelayMs = delayMs;
    if (state.backoffIndex < DELAYS_MS.length - 1) {
      state.backoffIndex += 1;
    }
    return { schedule: true, delayMs: delayMs, cancelSchedule: false };
  }

  function onReconnectDue(state) {
    state.scheduled = false;
    if (!state.optIn || state.hasPort) {
      return { connect: false };
    }
    return { connect: true };
  }

  function onConnectFailed(state, optIn) {
    state.hasPort = false;
    return onDisconnect(state, optIn);
  }

  function formatDelayLog(delayMs) {
    return "companion reconnect scheduled delay=" + (delayMs / 1000) + "s";
  }

  return {
    DELAYS_MS: DELAYS_MS,
    ALARM_NAME: ALARM_NAME,
    createState: createState,
    nextDelayMs: nextDelayMs,
    onServiceWorkerStart: onServiceWorkerStart,
    onManualConnect: onManualConnect,
    onPortOpened: onPortOpened,
    onHelloAck: onHelloAck,
    onDisconnect: onDisconnect,
    onReconnectDue: onReconnectDue,
    onConnectFailed: onConnectFailed,
    formatDelayLog: formatDelayLog
  };
});
