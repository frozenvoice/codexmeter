(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.ProMeterProject = factory();
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var METADATA_KEYS = {
    request_id: true,
    requestId: true,
    model_slug: true,
    resolved_model_slug: true,
    requested_model: true,
    requested_model_slug: true,
    default_model_slug: true,
    reasoning_effort: true,
    thinking_effort: true,
    effort: true,
    reasoningEffort: true,
    thinkingEffort: true,
    is_visually_hidden_from_conversation: true,
    parent_id: true,
    model_experience: true
  };

  function pick(obj, keys) {
    var out = {};
    if (!obj || typeof obj !== "object") {
      return out;
    }
    for (var i = 0; i < keys.length; i++) {
      if (Object.prototype.hasOwnProperty.call(obj, keys[i]) && obj[keys[i]] !== undefined) {
        out[keys[i]] = obj[keys[i]];
      }
    }
    return out;
  }

  function projectSession(node) {
    var user = node.user || node;
    return {
      signedIn: !!(user && (user.id || user.email)),
      user: pick(user, ["id", "email", "name"]),
      expires: node.expires,
      expires_at: node.expires_at,
      expiresAt: node.expiresAt
    };
  }

  function projectAccountMe(node) {
    return pick(node, ["id", "email", "name"]);
  }

  function projectAccountCheck(node) {
    var accounts = {};
    if (node.accounts) {
      Object.keys(node.accounts).forEach(function (key) {
        var account = node.accounts[key] || {};
        accounts[key] = {
          entitlement: pick(account.entitlement, ["subscription_plan", "plan_type", "has_active_subscription"]),
          account: pick(account.account, ["plan_type", "planType"])
        };
      });
    }
    return { accounts: accounts };
  }

  function projectModel(model) {
    return pick(model, ["slug", "id", "model", "model_slug", "title", "display_name", "name", "description", "tags"]);
  }

  function projectModels(node) {
    if (Array.isArray(node.models)) {
      return { models: node.models.map(projectModel) };
    }
    if (Array.isArray(node)) {
      return node.map(projectModel);
    }
    if (Array.isArray(node.categories)) {
      return {
        categories: node.categories.map(function (category) {
          return { models: Array.isArray(category.models) ? category.models.map(projectModel) : [] };
        })
      };
    }
    return {};
  }

  function projectIndexItem(item) {
    return pick(item, ["id", "conversation_id", "create_time", "createTime", "update_time", "updateTime", "is_archived", "archived", "gizmo_id", "project_id"]);
  }

  function projectConversationIndex(node) {
    var result = pick(node, ["total", "total_count", "offset", "limit", "next_offset", "nextOffset", "has_more", "hasMore", "next_cursor", "cursor", "nextCursor"]);
    var key = Array.isArray(node.items) ? "items" : Array.isArray(node.conversations) ? "conversations" : null;
    if (key) {
      result[key] = node[key].map(projectIndexItem);
    }
    return result;
  }

  function projectGizmo(gizmo) {
    if (gizmo && gizmo.gizmo) {
      return { gizmo: projectGizmo(gizmo.gizmo) };
    }
    var result = pick(gizmo, ["id"]);
    return result;
  }

  function projectProjects(node) {
    var result = pick(node, ["has_more", "hasMore", "next_cursor", "cursor", "nextCursor"]);
    var key = Array.isArray(node.gizmos) ? "gizmos" : Array.isArray(node.items) ? "items" : null;
    if (key) {
      result[key] = node[key].map(function (item) {
        var projected = pick(item, ["id"]);
        if (item.gizmo) {
          projected.gizmo = projectGizmo(item.gizmo);
        }
        return projected;
      });
    }
    return result;
  }

  function projectMetadata(metadata) {
    var result = {};
    Object.keys(metadata || {}).forEach(function (key) {
      if (!METADATA_KEYS[key]) {
        return;
      }
      if (key === "model_experience" && metadata[key] && typeof metadata[key] === "object") {
        result[key] = pick(metadata[key], ["reasoning_effort", "thinking_effort", "effort"]);
        return;
      }
      result[key] = metadata[key];
    });
    return result;
  }

  function projectMessage(message) {
    if (!message || typeof message !== "object") {
      return {};
    }
    var result = pick(message, ["id", "create_time", "createTime", "end_turn", "recipient"]);
    if (message.author && typeof message.author === "object") {
      result.author = pick(message.author, ["role"]);
    } else if (typeof message.role === "string") {
      result.author = { role: message.role };
    }
    if (message.content && typeof message.content === "object") {
      result.content = pick(message.content, ["content_type"]);
    }
    if (message.metadata && typeof message.metadata === "object") {
      result.metadata = projectMetadata(message.metadata);
    }
    return result;
  }

  function looksLikeDirectPaginatedMessage(node) {
    if (!node || typeof node !== "object") {
      return false;
    }
    if (node.message && typeof node.message === "object" && !Array.isArray(node.message)) {
      return false;
    }
    return !!(
      node.author ||
      node.metadata ||
      node.content ||
      typeof node.role === "string" ||
      node.message_id ||
      Object.prototype.hasOwnProperty.call(node, "end_turn") ||
      node.recipient
    );
  }

  function projectMappingNode(node) {
    if (!node || typeof node !== "object") {
      return {};
    }
    if (looksLikeDirectPaginatedMessage(node)) {
      return canonicalizeMappingNode(node, node);
    }
    var nested = node.message && typeof node.message === "object" && !Array.isArray(node.message)
      ? node.message
      : null;
    return canonicalizeMappingNode(node, nested);
  }

  function canonicalizeMappingNode(node, messageSource) {
    var result = {};
    var id = node.id || node.message_id || (messageSource && messageSource.id);
    var parent = node.parent || node.parent_id;
    if (id) {
      result.id = id;
    }
    if (parent) {
      result.parent = parent;
    }
    if (Array.isArray(node.children)) {
      result.children = node.children.filter(function (child) {
        return typeof child === "string";
      });
    }
    if (messageSource) {
      result.message = projectMessage(messageSource);
      if (id && !result.message.id) {
        result.message.id = id;
      }
    }
    return result;
  }

  function projectConversationDetail(node) {
    var result = pick(node, ["conversation_id", "id", "current_node", "update_time", "updateTime"]);
    var page = node.page_info || node.pageInfo;
    if (page) {
      result.page_info = pick(page, ["has_previous_page", "hasPreviousPage", "start_cursor", "startCursor"]);
    }
    if (node.mapping && typeof node.mapping === "object") {
      result.mapping = {};
      Object.keys(node.mapping).forEach(function (key) {
        result.mapping[key] = projectMappingNode(node.mapping[key] || {});
      });
    }
    ["messages", "items", "turns"].forEach(function (key) {
      if (Array.isArray(node[key])) {
        result[key] = node[key].map(projectMappingNode);
      }
    });
    return result;
  }

  function pickScalar(obj, keys) {
    var out = {};
    if (!obj || typeof obj !== "object") {
      return out;
    }
    for (var i = 0; i < keys.length; i++) {
      if (!Object.prototype.hasOwnProperty.call(obj, keys[i]) || obj[keys[i]] === undefined) {
        continue;
      }
      var value = obj[keys[i]];
      if (value === null || typeof value === "string" || typeof value === "number" || typeof value === "boolean") {
        out[keys[i]] = value;
      }
    }
    return out;
  }

  function projectQuota(node) {
    var quotaKeys = [
      "feature_name",
      "name",
      "slug",
      "model",
      "model_slug",
      "used",
      "limit",
      "resets_at",
      "reset_at",
      "resetAt",
      "resetsAt",
      "resets_after",
      "reset_after",
      "period",
      "window",
      "description"
    ];
    var blockedKeys = [
      "name",
      "feature_name",
      "limit",
      "resets_after",
      "reset_after",
      "resets_at",
      "reset_at",
      "resetsAt",
      "resetAt",
      "block_reason",
      "description"
    ];
    var result = {};
    if (Array.isArray(node.limits_progress)) {
      result.limits_progress = node.limits_progress.map(function (item) {
        return pickScalar(item, quotaKeys);
      });
    }
    if (Array.isArray(node.model_limits)) {
      result.model_limits = node.model_limits.map(function (item) {
        return pickScalar(item, quotaKeys);
      });
    }
    if (Array.isArray(node.blocked_features)) {
      result.blocked_features = node.blocked_features.map(function (item) {
        return pickScalar(item, blockedKeys);
      });
    }
    return result;
  }

  function project(operation, node) {
    if (!node || typeof node !== "object") {
      return { ok: false, error: "unknown response shape" };
    }
    switch (operation) {
      case "GetSessionStatus":
        return { ok: true, body: projectSession(node) };
      case "GetAccountMe":
        return { ok: true, body: projectAccountMe(node) };
      case "GetAccountCheck":
        return { ok: true, body: projectAccountCheck(node) };
      case "GetModels":
        return { ok: true, body: projectModels(node) };
      case "GetConversationIndex":
      case "GetArchivedConversationIndex":
      case "GetProjectConversations":
        return { ok: true, body: projectConversationIndex(node) };
      case "GetProjects":
        return { ok: true, body: projectProjects(node) };
      case "GetConversationHead":
      case "GetConversationFull":
      case "GetConversationLegacy":
      case "GetOlderConversationMessages":
        return { ok: true, body: projectConversationDetail(node) };
      case "GetQuotaInit":
        return { ok: true, body: projectQuota(node) };
      default:
        return { ok: false, error: "unknown operation" };
    }
  }

  function containsPromptOrResponseText(node) {
    if (!node || typeof node !== "object") {
      return false;
    }
    if (Array.isArray(node)) {
      return node.some(containsPromptOrResponseText);
    }
    if (node.content && typeof node.content === "object") {
      if (Array.isArray(node.content.parts) && node.content.parts.length > 0) {
        return true;
      }
      if (typeof node.content.text === "string" && node.content.text.trim()) {
        return true;
      }
    }
    var keys = ["transcript", "caption", "captions", "file_name", "filename", "attachment", "attachments", "tool_calls", "arguments"];
    for (var i = 0; i < keys.length; i++) {
      if (node[keys[i]] != null) {
        return true;
      }
    }
    return Object.keys(node).some(function (key) {
      return containsPromptOrResponseText(node[key]);
    });
  }

  return {
    project: project,
    containsPromptOrResponseText: containsPromptOrResponseText
  };
});
