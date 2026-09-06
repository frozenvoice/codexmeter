(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.ProMeterCanonical = factory();
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var EXPECTED_ORIGIN = "https://chatgpt.com";
  var MAX_NATIVE_MESSAGE_BYTES = 1048576;
  var MAX_ASSEMBLED_PROJECTED_BYTES = 16777216;
  var CHUNK_RAW_BYTES = 393216;
  var MAX_CHUNK_FRAME_BYTES = 716800;
  var MAX_CHUNK_COUNT = 48;
  var PAGE_BRIDGE_VERSION = 3;

  function utf8ByteLength(text) {
    return new TextEncoder().encode(String(text)).byteLength;
  }

  function containsControl(value) {
    for (var i = 0; i < value.length; i++) {
      if (value.charCodeAt(i) < 32) {
        return true;
      }
    }
    return false;
  }

  function isHex(ch) {
    return /[0-9a-fA-F]/.test(ch);
  }

  function isWellFormedPercent(value) {
    for (var i = 0; i < value.length; i++) {
      if (value.charAt(i) !== "%") {
        continue;
      }
      if (i + 2 >= value.length || !isHex(value.charAt(i + 1)) || !isHex(value.charAt(i + 2))) {
        return false;
      }
    }
    return true;
  }

  function pathContainsEncodedTraversal(candidate) {
    var cut = candidate.indexOf("?");
    var path = cut >= 0 ? candidate.slice(0, cut) : candidate;
    var lower = path.toLowerCase();
    return lower.indexOf("%2e") >= 0 || lower.indexOf("%2f") >= 0 || lower.indexOf("%5c") >= 0 || path.indexOf("..") >= 0;
  }

  function tryValidate(candidate) {
    if (!candidate || typeof candidate !== "string") {
      return { ok: false, error: "empty target" };
    }
    if (candidate.indexOf("\\") >= 0 || containsControl(candidate)) {
      return { ok: false, error: "illegal target characters" };
    }
    if (candidate.indexOf("://") >= 0 || candidate.indexOf("//") === 0) {
      return { ok: false, error: "absolute or scheme-relative target rejected" };
    }
    if (candidate.charAt(0) !== "/") {
      return { ok: false, error: "target must be a relative same-origin path" };
    }
    if (candidate.indexOf("#") >= 0) {
      return { ok: false, error: "fragments rejected" };
    }
    if (!isWellFormedPercent(candidate)) {
      return { ok: false, error: "malformed percent encoding" };
    }
    if (pathContainsEncodedTraversal(candidate)) {
      return { ok: false, error: "encoded path traversal rejected" };
    }

    var url;
    try {
      url = new URL(candidate, EXPECTED_ORIGIN + "/");
    } catch (error) {
      return { ok: false, error: "target is not a valid URL" };
    }
    if (url.origin !== EXPECTED_ORIGIN || url.username || url.password || url.port) {
      return { ok: false, error: "canonical origin is not https://chatgpt.com" };
    }
    if (url.pathname.indexOf("..") >= 0 || url.pathname.indexOf("//") >= 0 || url.pathname.indexOf("\\") >= 0) {
      return { ok: false, error: "path traversal rejected" };
    }
    var parts = url.pathname.split("/");
    for (var p = 1; p < parts.length; p++) {
      if (parts[p] === "." || parts[p] === "..") {
        return { ok: false, error: "path traversal rejected" };
      }
    }
    if (url.pathname !== "/api/auth/session" && url.pathname.indexOf("/backend-api/") !== 0) {
      return { ok: false, error: "target prefix is not approved" };
    }
    return { ok: true, canonical: url.pathname + url.search, origin: url.origin, pathname: url.pathname };
  }

  return {
    tryValidate: tryValidate,
    EXPECTED_ORIGIN: EXPECTED_ORIGIN,
    MAX_NATIVE_MESSAGE_BYTES: MAX_NATIVE_MESSAGE_BYTES,
    MAX_ASSEMBLED_PROJECTED_BYTES: MAX_ASSEMBLED_PROJECTED_BYTES,
    CHUNK_RAW_BYTES: CHUNK_RAW_BYTES,
    MAX_CHUNK_FRAME_BYTES: MAX_CHUNK_FRAME_BYTES,
    MAX_CHUNK_COUNT: MAX_CHUNK_COUNT,
    PAGE_BRIDGE_VERSION: PAGE_BRIDGE_VERSION,
    utf8ByteLength: utf8ByteLength
  };
});
