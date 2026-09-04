(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.ProMeterSanitize = factory();
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var SECRET_KEYS = [
    "accessToken",
    "access_token",
    "sessionToken",
    "session_token",
    "authorization",
    "cookie"
  ];

  function isObject(value) {
    return value !== null && typeof value === "object";
  }

  function stripBodies(node) {
    if (!isObject(node)) {
      return node;
    }

    if (Array.isArray(node)) {
      for (var i = 0; i < node.length; i++) {
        stripBodies(node[i]);
      }
      return node;
    }

    if (isObject(node.content) && !Array.isArray(node.content)) {
      if (Array.isArray(node.content.parts)) {
        node.content.parts = [];
      }
      delete node.content.text;
    }

    for (var s = 0; s < SECRET_KEYS.length; s++) {
      delete node[SECRET_KEYS[s]];
    }

    var keys = Object.keys(node);
    for (var k = 0; k < keys.length; k++) {
      stripBodies(node[keys[k]]);
    }
    return node;
  }

  function containsPromptOrResponseText(node) {
    if (!isObject(node)) {
      return false;
    }

    if (Array.isArray(node)) {
      return node.some(containsPromptOrResponseText);
    }

    if (isObject(node.content) && !Array.isArray(node.content)) {
      var parts = node.content.parts;
      if (Array.isArray(parts)) {
        for (var i = 0; i < parts.length; i++) {
          if (typeof parts[i] === "string" && parts[i].trim().length > 0) {
            return true;
          }
        }
      }
      if (typeof node.content.text === "string" && node.content.text.trim().length > 0) {
        return true;
      }
    }

    return Object.keys(node).some(function (key) {
      return containsPromptOrResponseText(node[key]);
    });
  }

  function sanitizeJsonText(text) {
    if (!text) {
      return "";
    }
    var parsed = JSON.parse(text);
    stripBodies(parsed);
    return JSON.stringify(parsed);
  }

  function isAllowedTarget(path) {
    if (!path || typeof path !== "string") {
      return false;
    }
    if (path.indexOf("\\") >= 0) {
      return false;
    }
    for (var i = 0; i < path.length; i++) {
      var code = path.charCodeAt(i);
      if (code < 32) {
        return false;
      }
    }
    if (path.indexOf("://") >= 0 || path.indexOf("//") === 0) {
      return false;
    }
    if (path.charAt(0) !== "/" || path.indexOf("//") === 0) {
      return false;
    }

    var cut = path.indexOf("?");
    var rawPath = cut >= 0 ? path.slice(0, cut) : path;
    var parts = rawPath.split("/");
    if (parts.length === 0 || parts[0] !== "") {
      return false;
    }
    var stack = [];
    for (var p = 1; p < parts.length; p++) {
      var part = parts[p];
      if (part === "" || part === ".") {
        continue;
      }
      if (part === "..") {
        if (stack.length === 0) {
          return false;
        }
        stack.pop();
        continue;
      }
      stack.push(part);
    }
    var normalized = "/" + stack.join("/");
    return (
      normalized.toLowerCase() === "/api/auth/session" ||
      normalized.toLowerCase().indexOf("/backend-api/") === 0
    );
  }

  function selfTest() {
    var dirty = {
      accessToken: "must-not-leave-browser",
      mapping: {
        user: {
          message: {
            content: {
              content_type: "text",
              parts: ["SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE"],
              text: "SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE"
            },
            metadata: { request_id: "req-synthetic", model_slug: "gpt-6-pro" }
          }
        }
      }
    };
    stripBodies(dirty);
    if (dirty.accessToken) {
      throw new Error("access token survived sanitizer");
    }
    if (containsPromptOrResponseText(dirty)) {
      throw new Error("prompt or response text survived sanitizer");
    }
    if (!Array.isArray(dirty.mapping.user.message.content.parts) || dirty.mapping.user.message.content.parts.length !== 0) {
      throw new Error("parts were not cleared");
    }
    if (dirty.mapping.user.message.metadata.request_id !== "req-synthetic") {
      throw new Error("metadata was stripped");
    }
    if (isAllowedTarget("//evil.example/x") || isAllowedTarget("https://evil.example/x") || isAllowedTarget("/backend-api/../evil")) {
      throw new Error("dangerous target accepted");
    }
    if (!isAllowedTarget("/backend-api/conversations") || !isAllowedTarget("/api/auth/session")) {
      throw new Error("approved target rejected");
    }
  }

  if (typeof process !== "undefined" && process.argv && process.argv.indexOf("--self-test") >= 0) {
    selfTest();
    process.stdout.write("sanitize self-test ok\n");
  }

  return {
    stripBodies: stripBodies,
    containsPromptOrResponseText: containsPromptOrResponseText,
    sanitizeJsonText: sanitizeJsonText,
    isAllowedTarget: isAllowedTarget,
    selfTest: selfTest
  };
});
