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
    var result = pick(gizmo, ["id", "name", "title"]);
    if (gizmo && gizmo.display) {
      result.display = pick(gizmo.display, ["name"]);
    }
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
    var result = pick(message, ["id", "create_time", "createTime", "end_turn", "recipient"]);
    if (message.author) {
      result.author = pick(message.author, ["role"]);
    }
    if (message.content) {
      result.content = pick(message.content, ["content_type"]);
    }
    if (message.metadata) {
      result.metadata = projectMetadata(message.metadata);
    }
    return result;
  }

  function projectMappingNode(node) {
    var result = pick(node, ["id", "parent", "parent_id", "create_time", "createTime"]);
    if (Array.isArray(node.children)) {
      result.children = node.children.filter(function (child) {
        return typeof child === "string";
      });
    }
    if (node.message) {
      result.message = projectMessage(node.message);
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

  function projectQuota(node) {
    function quotaItem(item) {
      return pick(item, ["feature_name", "name", "slug", "model", "model_slug", "used", "limit", "resets_at", "reset_at", "resetAt", "resetsAt", "period", "window"]);
    }
    var result = {};
    if (Array.isArray(node.limits_progress)) {
      result.limits_progress = node.limits_progress.map(quotaItem);
    }
    if (Array.isArray(node.model_limits)) {
      result.model_limits = node.model_limits.map(quotaItem);
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
