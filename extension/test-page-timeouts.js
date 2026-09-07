module.exports = async function testPageTimeouts() {
  const assert = require("assert");
  const executor = require("./page-executor.js");
  const pageTab = require("./page-tab.js");
  const auth = require("./auth.js");
  const never = () => new Promise(() => {});
  const response = value => ({ status: 200, headers: { get: () => null }, text: async () => JSON.stringify(value) });
  const timeoutError = "bridge request timed out";
  const hooks = fetchImpl => ({ origin: "https://chatgpt.com", timeoutMs: 10, fetchImpl });
  async function boundedTest(pending) {
    let timer;
    try {
      return await Promise.race([pending, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error("browser operation never completed")), 1000);
      })]);
    } finally { clearTimeout(timer); }
  }

  // A stuck shared session request must release every waiter and allow a new fetch.
  auth.invalidate();
  let sessionCalls = 0;
  let oldSignal;
  let resolveOldFetch;
  const stalledSession = hooks((_url, init) => {
    sessionCalls++;
    oldSignal = init.signal;
    return new Promise(resolve => { resolveOldFetch = resolve; });
  });
  const results = await boundedTest(Promise.all([
    executor.execute("GetSessionStatus", {}, stalledSession),
    executor.execute("GetModels", {}, stalledSession)
  ]));
  assert.strictEqual(sessionCalls, 1, "concurrent requests must share the session probe");
  results.forEach(result => assert.strictEqual(result.error, timeoutError));
  assert.strictEqual(oldSignal.aborted, true);

  let freshCalls = 0;
  const recovered = await executor.execute("GetModels", {}, hooks(url => {
    freshCalls++;
    return Promise.resolve(response(url.includes("/api/auth/session")
      ? { accessToken: "fresh-synthetic-token", user: { id: "synthetic-user" } }
      : { models: [] }));
  }));
  assert.strictEqual(recovered.status, 200);
  assert.strictEqual(freshCalls, 2, "retry must make a fresh session request");
  resolveOldFetch(response({ accessToken: "stale-synthetic-token", user: { id: "old" } }));
  await new Promise(resolve => setImmediate(resolve));
  assert.strictEqual(auth.tokenSnapshot(), "fresh-synthetic-token", "late timed-out session must not overwrite recovered auth");
  assert.ok(!JSON.stringify(results.concat(recovered)).includes("synthetic-token"));

  // Response headers alone do not finish the deadline: a stalled session body also releases single-flight.
  auth.invalidate();
  const bodyTimeout = await boundedTest(executor.execute("GetSessionStatus", {}, hooks(async () => ({
    status: 200, headers: { get: () => null }, text: never
  }))));
  assert.strictEqual(bodyTimeout.error, timeoutError);
  const afterBodyTimeout = await executor.execute("GetSessionStatus", {}, hooks(async () => response({ user: { id: "recovered" } })));
  assert.strictEqual(afterBodyTimeout.status, 200);

  // The approved backend body and a 401 refresh use the same bounded operation.
  const backendTimeout = await boundedTest(executor.execute("GetModels", {}, hooks(async () => ({
    status: 200, headers: { get: () => null }, text: never
  }))));
  assert.strictEqual(backendTimeout.error, timeoutError);
  const refreshTimeout = await boundedTest(executor.execute("GetModels", {}, hooks(async url => {
    if (url.includes("/api/auth/session")) return never();
    return { status: 401, headers: { get: () => null }, text: async () => "" };
  })));
  assert.strictEqual(refreshTimeout.error, timeoutError);
  const afterRefresh = await executor.execute("GetSessionStatus", {}, hooks(async () => response({ user: { id: "recovered" } })));
  assert.strictEqual(afterRefresh.status, 200);

  const readyTab = { id: 1, url: "https://chatgpt.com/", active: true };
  function chromeFor(executeScript) {
    return { tabs: { query: async () => [readyTab] }, scripting: { executeScript } };
  }
  // DOM-independent bridge operations must run even when document_idle never arrives.
  let injections = 0;
  const whileLoading = await boundedTest(pageTab.invoke("GetModels", {}, chromeFor(async details => {
    if (!details.injectImmediately) return never();
    injections++;
    if (details.files) return [{}];
    if (details.args.length === 1) return [{ result: { ready: false } }];
    return [{ result: { status: 200, body: '{"models":[]}' } }];
  })));
  assert.strictEqual(whileLoading.status, 200);
  assert.strictEqual(injections, 3);

  const hungProbe = await boundedTest(pageTab.invoke("GetModels", {}, chromeFor(never), { setupTimeoutMs: 10 }));
  assert.strictEqual(hungProbe.error, timeoutError);
  const hungExecution = await boundedTest(pageTab.invoke("GetModels", {}, chromeFor(async details => {
    return details.args.length === 1 ? [{ result: { ready: true } }] : never();
  }), { timeoutMs: 10 }));
  assert.strictEqual(hungExecution.error, timeoutError);

  // A late probe after the outer deadline must not inject files or start a fetch.
  let resolveProbe;
  let calls = 0;
  const pendingProbe = pageTab.invoke("GetModels", {}, chromeFor(() => {
    calls++;
    return new Promise(resolve => { resolveProbe = resolve; });
  }), { timeoutMs: 10, setupTimeoutMs: 100 });
  assert.strictEqual((await boundedTest(pendingProbe)).error, timeoutError);
  resolveProbe([{ result: { ready: false } }]);
  await new Promise(resolve => setImmediate(resolve));
  assert.strictEqual(calls, 1);

  assert.strictEqual(pageTab.pickPreferredTab([
    { id: 2, url: "https://chatgpt.com/", active: true, frozen: true }, readyTab
  ]).id, 1);
  assert.strictEqual(pageTab.pickPreferredTab([
    { id: 2, url: "https://chatgpt.com/", active: true, discarded: true }, readyTab
  ]).id, 1);

  // Edge can keep a loaded tab frozen; activation must recover the SAME tab,
  // with no navigation/reload and no duplicate backend operation.
  for (const reportedFrozen of [true, false]) {
    let awake = false;
    let wakes = 0;
    let fetches = 0;
    const chrome = {
      tabs: {
        query: async () => [{ ...readyTab, active: false, frozen: reportedFrozen }],
        update: async (id, changes) => {
          assert.strictEqual(id, readyTab.id);
          assert.deepStrictEqual(changes, { active: true });
          awake = true;
          wakes++;
          return { ...readyTab };
        }
      },
      scripting: { executeScript: async details => {
        if (!awake) return never();
        if (details.args.length === 1) return [{ result: { ready: true } }];
        fetches++;
        return [{ result: { status: 200, body: '{"models":[]}' } }];
      } }
    };
    const result = await boundedTest(pageTab.invoke("GetModels", {}, chrome, { setupTimeoutMs: 10 }));
    assert.strictEqual(result.status, 200);
    assert.strictEqual(wakes, 1);
    assert.strictEqual(fetches, 1);
  }
  let failedWakes = 0;
  const cannotWake = chromeFor(never);
  cannotWake.tabs.update = async () => { failedWakes++; return readyTab; };
  assert.strictEqual((await boundedTest(pageTab.invoke("GetModels", {}, cannotWake, { setupTimeoutMs: 10 }))).error, timeoutError);
  assert.strictEqual(failedWakes, 1, "recovery must not loop forever");

  assert.ok(executor.REQUEST_TIMEOUT_MS < pageTab.INVOKE_TIMEOUT_MS);
  assert.ok(pageTab.INVOKE_TIMEOUT_MS < 60000, "extension must settle before native hub timeout");
};
