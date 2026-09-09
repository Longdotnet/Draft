import assert from "node:assert/strict";
import test from "node:test";
import { quiesceListenersAndDrain } from "../src/gracefulShutdown.js";

test("one listener stop failure does not block other listeners or webhook drain", async () => {
  const stopped: string[] = [];
  const lifecycle: string[] = [];
  let quiesced = false;
  let drainedWith: number | null = null;

  const result = await quiesceListenersAndDrain({
    listeners: [{ accountId: "acc-a" }, { accountId: "acc-b" }, { accountId: "acc-c" }],
    runListenerLifecycle: async (accountId, action) => {
      lifecycle.push(accountId);
      return action();
    },
    stopListener: async (accountId) => {
      stopped.push(accountId);
      if (accountId === "acc-b") throw new Error("socket cleanup failed");
    },
    quiesce: async () => { quiesced = true; },
    drainWebhookDeliveries: async (maxWaitMs) => {
      drainedWith = maxWaitMs;
      return true;
    },
    drainBudgetMs: 20_000,
  });

  assert.deepEqual(lifecycle, ["acc-a", "acc-b", "acc-c"]);
  assert.deepEqual(stopped, ["acc-a", "acc-b", "acc-c"]);
  assert.equal(quiesced, true);
  assert.equal(drainedWith, 20_000);
  assert.equal(result.drained, true);
  assert.equal(result.listenerStopFailures.length, 1);
  assert.equal(result.listenerStopFailures[0]?.accountId, "acc-b");
  assert.equal(result.quiesceError, null);
  assert.equal(result.drainError, null);
});

test("webhook drain is still attempted when board-event quiesce fails", async () => {
  let drainCalls = 0;
  const result = await quiesceListenersAndDrain({
    listeners: [{ accountId: "acc-a" }],
    runListenerLifecycle: async (_accountId, action) => action(),
    stopListener: async () => undefined,
    quiesce: async () => { throw new Error("timer failed"); },
    drainWebhookDeliveries: async () => {
      drainCalls += 1;
      return true;
    },
    drainBudgetMs: 10_000,
  });

  assert.equal(drainCalls, 1);
  assert.equal(result.drained, true);
  assert.match(String(result.quiesceError), /timer failed/);
});

test("drain failure is captured only after all listener cleanup was attempted", async () => {
  const stopped: string[] = [];
  const result = await quiesceListenersAndDrain({
    listeners: [{ accountId: "acc-a" }, { accountId: "acc-b" }],
    runListenerLifecycle: async (_accountId, action) => action(),
    stopListener: async (accountId) => { stopped.push(accountId); },
    quiesce: async () => undefined,
    drainWebhookDeliveries: async () => { throw new Error("drain unavailable"); },
    drainBudgetMs: 5_000,
  });

  assert.deepEqual(stopped, ["acc-a", "acc-b"]);
  assert.equal(result.drained, false);
  assert.match(String(result.drainError), /drain unavailable/);
});
