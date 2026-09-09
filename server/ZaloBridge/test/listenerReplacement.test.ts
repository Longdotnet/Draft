import assert from "node:assert/strict";
import test from "node:test";
import {
  activateListenerReplacement,
  beginIntentionalManualClose,
  cancelIntentionalManualClose,
  consumeIntentionalManualClose,
  enforceBridgeOwnedListenerReconnect,
  prepareListenerReplacement,
} from "../src/listenerReplacement.js";

test("failed replacement preparation leaves the current listener untouched", async () => {
  let deactivationCount = 0;
  const expected = new Error("credential refresh rejected");

  await assert.rejects(
    prepareListenerReplacement(
      async () => { throw expected; },
      () => { deactivationCount += 1; },
    ),
    expected,
  );

  assert.equal(deactivationCount, 0);
});

test("current listener is deactivated only after replacement preparation succeeds", async () => {
  const order: string[] = [];
  const candidate = { id: "replacement-api" };

  const result = await prepareListenerReplacement(
    async () => {
      order.push("prepare:start");
      await Promise.resolve();
      order.push("prepare:done");
      return candidate;
    },
    () => { order.push("current:stop"); },
  );

  assert.equal(result, candidate);
  assert.deepEqual(order, ["prepare:start", "prepare:done", "current:stop"]);
});

test("first listener startup works without a current listener", async () => {
  const candidate = { id: "first-api" };
  const result = await prepareListenerReplacement(async () => candidate);
  assert.equal(result, candidate);
});

test("replacement activation failure restores the previous listener", async () => {
  const order: string[] = [];
  const expected = new Error("replacement websocket failed to start");

  await assert.rejects(
    activateListenerReplacement({
      prepare: async () => {
        order.push("candidate:prepared");
        return { id: "candidate" };
      },
      deactivateCurrent: () => { order.push("current:stop"); },
      activateCandidate: () => {
        order.push("candidate:start");
        throw expected;
      },
      reactivateCurrent: () => { order.push("current:restart"); },
    }),
    expected,
  );

  assert.deepEqual(order, [
    "candidate:prepared",
    "current:stop",
    "candidate:start",
    "current:restart",
  ]);
});

test("successful replacement activation does not restart the previous listener", async () => {
  const order: string[] = [];
  const candidate = { id: "candidate" };

  const result = await activateListenerReplacement({
    prepare: async () => {
      order.push("candidate:prepared");
      return candidate;
    },
    deactivateCurrent: () => { order.push("current:stop"); },
    activateCandidate: () => { order.push("candidate:start"); },
    reactivateCurrent: () => { order.push("current:restart"); },
  });

  assert.equal(result, candidate);
  assert.deepEqual(order, ["candidate:prepared", "current:stop", "candidate:start"]);
});

test("rollback failure preserves both activation and rollback errors", async () => {
  const activationError = new Error("candidate start failed");
  const rollbackError = new Error("current restart failed");

  await assert.rejects(
    activateListenerReplacement({
      prepare: async () => ({ id: "candidate" }),
      deactivateCurrent: () => undefined,
      activateCandidate: () => { throw activationError; },
      reactivateCurrent: () => { throw rollbackError; },
    }),
    (error: unknown) => {
      assert.ok(error instanceof AggregateError);
      assert.deepEqual(error.errors, [activationError, rollbackError]);
      return true;
    },
  );
});

test("replacement boundary forces zca-js provider-managed retry off", async () => {
  const observedOptions: Array<{ retryOnClose?: boolean } | undefined> = [];
  const candidate = {
    api: {
      listener: {
        start: (options?: { retryOnClose?: boolean }) => { observedOptions.push(options); },
      },
    },
  };

  await activateListenerReplacement({
    prepare: async () => candidate,
    activateCandidate: (prepared) => prepared.api.listener.start({ retryOnClose: true }),
  });

  assert.deepEqual(observedOptions, [{ retryOnClose: false }]);
});

test("reconnect ownership wrapper is idempotent across repeated preparation", () => {
  const observedOptions: Array<{ retryOnClose?: boolean } | undefined> = [];
  const candidate = {
    api: {
      listener: {
        start: (options?: { retryOnClose?: boolean }) => { observedOptions.push(options); },
      },
    },
  };

  enforceBridgeOwnedListenerReconnect(candidate);
  enforceBridgeOwnedListenerReconnect(candidate);
  candidate.api.listener.start({ retryOnClose: true });

  assert.deepEqual(observedOptions, [{ retryOnClose: false }]);
});

test("reconnect ownership wrapper ignores candidates without a live provider listener", () => {
  assert.doesNotThrow(() => enforceBridgeOwnedListenerReconnect(null));
  assert.doesNotThrow(() => enforceBridgeOwnedListenerReconnect({ api: null }));
  assert.doesNotThrow(() => enforceBridgeOwnedListenerReconnect({ id: "mock-listener" }));
});

test("late manual close from intentional stop is consumed exactly once", () => {
  const fence = { pendingManualCloseEvents: 0 };

  beginIntentionalManualClose(fence);

  assert.equal(consumeIntentionalManualClose(fence, 1000), true);
  assert.equal(fence.pendingManualCloseEvents, 0);
  assert.equal(consumeIntentionalManualClose(fence, 1000), false);
});

test("non-manual close never consumes an intentional manual-close fence", () => {
  const fence = { pendingManualCloseEvents: 0 };

  beginIntentionalManualClose(fence);

  assert.equal(consumeIntentionalManualClose(fence, 1006), false);
  assert.equal(fence.pendingManualCloseEvents, 1);
  assert.equal(consumeIntentionalManualClose(fence, 1000), true);
});

test("failed stop can cancel its manual-close fence without underflow", () => {
  const fence = { pendingManualCloseEvents: 0 };

  beginIntentionalManualClose(fence);
  cancelIntentionalManualClose(fence);
  cancelIntentionalManualClose(fence);

  assert.equal(fence.pendingManualCloseEvents, 0);
  assert.equal(consumeIntentionalManualClose(fence, 1000), false);
});
