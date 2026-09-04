(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("./canonical.js"));
  } else {
    root.ProMeterOperations = factory(root.ProMeterCanonical);
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function (canonical) {
  var OPERATIONS = {
    GetSessionStatus: { method: "GET", path: "/api/auth/session" },
    GetAccountCheck: { method: "GET", path: "/backend-api/accounts/check/v4-2023-04-27" },
    GetAccountMe: { method: "GET", path: "/backend-api/me" },
    GetModels: { method: "GET", path: "/backend-api/models" },
    GetConversationIndex: { method: "GET" },
    GetArchivedConversationIndex: { method: "GET" },
    GetProjects: { method: "GET" },
    GetProjectConversations: { method: "GET" },
    GetConversationHead: { method: "GET" },
    GetConversationFull: { method: "GET" },
    GetConversationLegacy: { method: "GET" },
    GetOlderConversationMessages: { method: "GET" },
    GetQuotaInit: { method: "POST", path: "/backend-api/conversation/init", body: '{"conversation_mode_kind":"primary_assistant"}' }
  };

  function isSafeId(value) {
    return typeof value === "string" && value.length > 0 && value.length <= 128 && /^[A-Za-z0-9._-]+$/.test(value) && value.indexOf("..") < 0;
  }

  function isSafeCursor(value, optional) {
    if (!value) {
      return optional === true;
    }
    return typeof value === "string" && value.length <= 512 && value.indexOf("\\") < 0;
  }

  function build(operation, args) {
    args = args || {};
    if (!OPERATIONS[operation]) {
      return { ok: false, error: "unknown operation" };
    }
    var method = OPERATIONS[operation].method;
    var path = OPERATIONS[operation].path;
    var body = OPERATIONS[operation].body || null;
    if (operation === "GetConversationIndex" || operation === "GetArchivedConversationIndex") {
      var offset = args.offset || 0;
      var limit = args.limit || 100;
      if (offset < 0 || offset > 100000 || limit < 1 || limit > 100) {
        return { ok: false, error: "offset or limit out of range" };
      }
      var archived = operation === "GetArchivedConversationIndex" || args.archived === true;
      path = "/backend-api/conversations?offset=" + offset + "&limit=" + limit + "&order=updated&is_archived=" + (archived ? "true" : "false");
    } else if (operation === "GetProjects") {
      path = "/backend-api/gizmos/snorlax/sidebar?conversations_per_gizmo=0&owned_only=true";
      if (args.cursor) {
        if (!isSafeCursor(args.cursor, false)) {
          return { ok: false, error: "invalid cursor" };
        }
        path += "&cursor=" + encodeURIComponent(args.cursor);
      }
    } else if (operation === "GetProjectConversations") {
      if (!isSafeId(args.projectId) || !isSafeCursor(args.cursor || "0", false)) {
        return { ok: false, error: "invalid identifier" };
      }
      path = "/backend-api/gizmos/" + encodeURIComponent(args.projectId) + "/conversations?cursor=" + encodeURIComponent(args.cursor || "0");
    } else if (operation === "GetConversationHead") {
      if (!isSafeId(args.conversationId)) {
        return { ok: false, error: "invalid identifier" };
      }
      path = "/backend-api/conversations/" + encodeURIComponent(args.conversationId) + "?include_has_versions=true&num_turns=100";
    } else if (operation === "GetConversationFull") {
      if (!isSafeId(args.conversationId)) {
        return { ok: false, error: "invalid identifier" };
      }
      path = "/backend-api/conversation/" + encodeURIComponent(args.conversationId) + "?include_full_conversation=true";
    } else if (operation === "GetConversationLegacy") {
      if (!isSafeId(args.conversationId)) {
        return { ok: false, error: "invalid identifier" };
      }
      path = "/backend-api/conversation/" + encodeURIComponent(args.conversationId);
    } else if (operation === "GetOlderConversationMessages") {
      if (!isSafeId(args.conversationId) || !isSafeCursor(args.cursor, false)) {
        return { ok: false, error: "invalid identifier" };
      }
      path = "/backend-api/conversations/" + encodeURIComponent(args.conversationId) + "/messages?before=" + encodeURIComponent(args.cursor) + "&include_has_versions=true&num_turns=100";
    } else if (operation === "GetQuotaInit") {
      body = '{"conversation_mode_kind":"primary_assistant"}';
    }

    var validated = canonical.tryValidate(path);
    if (!validated.ok) {
      return validated;
    }
    return { ok: true, method: method, path: validated.canonical, body: body };
  }

  return { build: build, OPERATIONS: OPERATIONS, isSafeId: isSafeId };
});
