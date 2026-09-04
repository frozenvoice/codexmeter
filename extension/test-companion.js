(async function () {
  const assert = require("assert");
  const canonical = require("./canonical.js");
  const operations = require("./operations.js");
  const project = require("./project.js");
  const auth = require("./auth.js");

function fail(message) {
  throw new Error(message);
}

const encoded = [
  "/backend-api/%2e%2e/evil",
  "/backend-api/.%2e/evil",
  "/backend-api/%2e./evil",
  "/backend-api/%2F%2Fevil",
  "/backend-api/%5Cevil",
  "/backend-api/%zz",
  "/backend-api/../evil"
];
encoded.forEach(function (path) {
  const result = canonical.tryValidate(path);
  assert.strictEqual(result.ok, false, "should reject " + path);
});

const validQuery = canonical.tryValidate("/backend-api/conversations?offset=0&limit=28&order=updated&is_archived=false");
assert.strictEqual(validQuery.ok, true);
assert.ok(validQuery.canonical.indexOf("offset=0") >= 0);

const validEncodedQuery = canonical.tryValidate("/backend-api/conversations/abc/messages?before=cursor%3Dvalue&include_has_versions=true&num_turns=100");
assert.strictEqual(validEncodedQuery.ok, true);

assert.strictEqual(operations.build("NotARealOp", {}).ok, false);
assert.strictEqual(operations.build("GetSessionStatus", {}).method, "GET");
assert.strictEqual(operations.build("GetQuotaInit", {}).method, "POST");
assert.strictEqual(operations.build("GetConversationIndex", { offset: 0, limit: 100 }).ok, true);

const dirty = {
  title: "SYNTHETIC_TITLE_DO_NOT_LEAVE",
  accessToken: "must-not-leave-browser",
  mapping: {
    user: {
      id: "user-1",
      parent: "root",
      children: ["asst-1"],
      message: {
        id: "user-1",
        author: { role: "user" },
        create_time: 1777500000,
        content: {
          content_type: "text",
          parts: ["SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE"],
          text: "SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE"
        },
        metadata: {
          request_id: "req-synthetic",
          model_slug: "gpt-6-pro",
          tool_calls: [{ arguments: "search query" }],
          filename: "notes.txt",
          url: "https://example.invalid"
        }
      }
    }
  }
};
const projected = project.project("GetConversationHead", dirty);
assert.strictEqual(projected.ok, true);
assert.strictEqual(projected.body.title, undefined);
assert.strictEqual(projected.body.accessToken, undefined);
assert.ok(!projected.body.mapping.user.message.content.parts);
assert.ok(!projected.body.mapping.user.message.content.text);
assert.ok(!projected.body.mapping.user.message.metadata.tool_calls);
assert.ok(!projected.body.mapping.user.message.metadata.filename);
assert.ok(!projected.body.mapping.user.message.metadata.url);
assert.strictEqual(projected.body.mapping.user.message.metadata.request_id, "req-synthetic");
assert.strictEqual(project.containsPromptOrResponseText(projected.body), false);

const projects = project.project("GetProjects", {
  gizmos: [{
    id: "proj-1",
    gizmo: {
      id: "proj-1",
      name: "SYNTHETIC_PROJECT_NAME_DO_NOT_LEAVE",
      title: "SYNTHETIC_PROJECT_TITLE",
      display: { name: "SYNTHETIC_DISPLAY_NAME" }
    }
  }]
});
assert.strictEqual(projects.ok, true);
const projectJson = JSON.stringify(projects.body);
assert.ok(projectJson.indexOf("SYNTHETIC_PROJECT_NAME_DO_NOT_LEAVE") < 0);
assert.ok(projectJson.indexOf("SYNTHETIC_PROJECT_TITLE") < 0);
assert.ok(projectJson.indexOf("SYNTHETIC_DISPLAY_NAME") < 0);
assert.strictEqual(projects.body.gizmos[0].gizmo.name, undefined);
assert.strictEqual(projects.body.gizmos[0].gizmo.title, undefined);
assert.strictEqual(projects.body.gizmos[0].gizmo.display, undefined);

assert.strictEqual(canonical.MAX_NATIVE_MESSAGE_BYTES, 1048576);
const korean = "한".repeat(400000);
assert.ok(korean.length < canonical.MAX_NATIVE_MESSAGE_BYTES);
assert.ok(canonical.utf8ByteLength(korean) > canonical.MAX_NATIVE_MESSAGE_BYTES);

auth.applySession({ accessToken: "in-memory-only-token" });
assert.strictEqual(auth.tokenSnapshot(), "in-memory-only-token");
const nativeMessage = { type: "invokeResult", body: JSON.stringify(projected.body) };
assert.ok(JSON.stringify(nativeMessage).indexOf("in-memory-only-token") < 0);
auth.neverSerializeToken(nativeMessage);

const directPaginated = {
  conversation_id: "conv-direct",
  current_node: "asst-1",
  update_time: 1777500900,
  messages: [
    {
      id: "user-1",
      parent: null,
      author: { role: "user" },
      create_time: 1777500898,
      content: {
        content_type: "text",
        parts: ["SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE"],
        text: "SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE"
      },
      metadata: {}
    },
    {
      id: "asst-1",
      parent: "user-1",
      author: { role: "assistant" },
      create_time: 1777500899,
      end_turn: true,
      recipient: "all",
      content: {
        content_type: "text",
        parts: ["SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE"],
        text: "SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE"
      },
      metadata: {
        request_id: "req-direct-pro",
        model_slug: "gpt-5-6-pro"
      }
    }
  ],
  page_info: {
    has_previous_page: false,
    start_cursor: "synthetic-cursor-start"
  }
};
const projectedDirect = project.project("GetConversationFull", directPaginated);
assert.strictEqual(projectedDirect.ok, true);
assert.strictEqual(projectedDirect.body.messages[1].message.author.role, "assistant");
assert.strictEqual(projectedDirect.body.messages[1].message.metadata.request_id, "req-direct-pro");
assert.strictEqual(projectedDirect.body.messages[1].message.metadata.model_slug, "gpt-5-6-pro");
assert.strictEqual(projectedDirect.body.messages[1].id, "asst-1");
assert.strictEqual(projectedDirect.body.messages[1].parent, "user-1");
assert.strictEqual(projectedDirect.body.messages[1].message.content.content_type, "text");
assert.ok(!projectedDirect.body.messages[1].message.content.parts);
assert.ok(!projectedDirect.body.messages[1].message.content.text);
assert.strictEqual(project.containsPromptOrResponseText(projectedDirect.body), false);
const projectedDirectJson = JSON.stringify(projectedDirect.body);
assert.ok(projectedDirectJson.indexOf("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE") < 0);
assert.ok(projectedDirectJson.indexOf("SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE") < 0);

const aliasedDirect = {
  conversation_id: "conv-alias",
  current_node: "asst-1",
  messages: [
    {
      message_id: "user-1",
      parent_id: null,
      author: { role: "user" },
      create_time: 1777500901,
      content: { content_type: "text", parts: ["SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE"] }
    },
    {
      message_id: "asst-1",
      parent_id: "user-1",
      author: { role: "assistant" },
      create_time: 1777500902,
      end_turn: true,
      content: { content_type: "text", parts: ["SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE"] },
      metadata: { request_id: "req-alias", model_slug: "gpt-6-pro" }
    }
  ]
};
const projectedAlias = project.project("GetOlderConversationMessages", aliasedDirect);
assert.strictEqual(projectedAlias.ok, true);
assert.strictEqual(projectedAlias.body.messages[1].id, "asst-1");
assert.strictEqual(projectedAlias.body.messages[1].parent, "user-1");
assert.strictEqual(projectedAlias.body.messages[1].message_id, undefined);
assert.strictEqual(projectedAlias.body.messages[1].parent_id, undefined);
assert.strictEqual(projectedAlias.body.messages[1].message.author.role, "assistant");
assert.strictEqual(projectedAlias.body.messages[1].message.metadata.request_id, "req-alias");
assert.strictEqual(projectedAlias.body.messages[1].message.metadata.model_slug, "gpt-6-pro");
assert.strictEqual(project.containsPromptOrResponseText(projectedAlias.body), false);

let sessionCalls = 0;
let backendCalls = 0;
async function fetchImpl(url, init) {
  if (String(url).indexOf("/api/auth/session") >= 0) {
    sessionCalls += 1;
    return {
      status: 200,
      headers: { get: function () { return null; } },
      text: async function () {
        return JSON.stringify({ accessToken: "in-memory-only-token", user: { id: "u1", email: "a@b.example" } });
      }
    };
  }
  backendCalls += 1;
  return {
    status: backendCalls === 1 ? 401 : 200,
    headers: { get: function () { return null; } },
    text: async function () {
      return backendCalls === 1 ? "unauthorized" : JSON.stringify({ models: [{ slug: "gpt-6-pro" }] });
    }
  };
}

sessionCalls = 0;
backendCalls = 0;
auth.invalidate();
const refreshed = await auth.invokeWithRefresh(fetchImpl, "GET", "/backend-api/models", null, "https://chatgpt.com");
assert.strictEqual(backendCalls, 2);
assert.strictEqual(sessionCalls, 1);
assert.strictEqual(refreshed.status, 200);

backendCalls = 0;
sessionCalls = 0;
auth.invalidate();
async function always401(url) {
  if (String(url).indexOf("/api/auth/session") >= 0) {
    sessionCalls += 1;
    return {
      status: 200,
      headers: { get: function () { return null; } },
      text: async function () {
        return JSON.stringify({ accessToken: "in-memory-only-token", user: { id: "u1" } });
      }
    };
  }
  backendCalls += 1;
  return {
    status: 401,
    headers: { get: function () { return null; } },
    text: async function () { return "unauthorized"; }
  };
}
const second401 = await auth.invokeWithRefresh(always401, "GET", "/backend-api/models", null, "https://chatgpt.com");
assert.strictEqual(backendCalls, 2);
assert.strictEqual(second401.status, 401);

auth.invalidate();
async function session429() {
  return {
    status: 429,
    headers: { get: function () { return "12"; } },
    text: async function () { return "not-json"; }
  };
}
const preserved = await auth.fetchSession(session429);
assert.strictEqual(preserved.status, 429);
assert.strictEqual(preserved.json, false);

  process.stdout.write("companion node tests ok\n");
})().catch(function (error) {
  process.stderr.write(String(error && error.stack ? error.stack : error) + "\n");
  process.exit(1);
});
