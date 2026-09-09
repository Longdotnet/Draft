import assert from "node:assert/strict";
import test from "node:test";
import { prepareListenerReplacement } from "../src/listenerReplacement.js";

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
