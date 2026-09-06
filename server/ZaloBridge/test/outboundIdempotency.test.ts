import assert from "node:assert/strict";
import test from "node:test";
import { ScopedOutboundIdempotency } from "../src/outboundIdempotency.js";

test("same scoped key and payload coalesces concurrent and later retries", async () => {
  const guard = new ScopedOutboundIdempotency<{ messageId: string }>();
  let calls = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const factory = async () => {
    calls += 1;
    await gate;
    return { messageId: "provider-1" };
  };

  const first = guard.run(
    { accountId: "account-1", groupId: "group-1", idempotencyKey: "reply:42" },
    { message: "hello", mentions: [] },
    factory,
    1_000,
  );
  const concurrent = guard.run(
    { accountId: "account-1", groupId: "group-1", idempotencyKey: "reply:42" },
    { mentions: [], message: "hello" },
    factory,
    1_001,
  );
  release();

  assert.deepEqual(await first, { messageId: "provider-1" });
  assert.deepEqual(await concurrent, { messageId: "provider-1" });
  assert.equal(calls, 1);

  const retry = await guard.run(
    { accountId: "account-1", groupId: "group-1", idempotencyKey: "reply:42" },
    { message: "hello", mentions: [] },
    factory,
    2_000,
  );
  assert.deepEqual(retry, { messageId: "provider-1" });
  assert.equal(calls, 1);
});

test("same caller key is isolated across groups and accounts", async () => {
  const guard = new ScopedOutboundIdempotency<number>();
  let calls = 0;
  const send = () => Promise.resolve(++calls);

  assert.equal(await guard.run(
    { accountId: "account-1", groupId: "group-1", idempotencyKey: "same-key" },
    { message: "hello" }, send, 1_000), 1);
  assert.equal(await guard.run(
    { accountId: "account-1", groupId: "group-2", idempotencyKey: "same-key" },
    { message: "hello" }, send, 1_001), 2);
  assert.equal(await guard.run(
    { accountId: "account-2", groupId: "group-1", idempotencyKey: "same-key" },
    { message: "hello" }, send, 1_002), 3);
});

test("scope framing cannot collide when ids or keys contain delimiters", async () => {
  const guard = new ScopedOutboundIdempotency<number>();
  let calls = 0;
  const send = () => Promise.resolve(++calls);

  assert.equal(await guard.run(
    { accountId: "account:a", groupId: "group", idempotencyKey: "reply:42" },
    { message: "first" }, send, 1_000), 1);
  assert.equal(await guard.run(
    { accountId: "account", groupId: "a:group", idempotencyKey: "reply:42" },
    { message: "second" }, send, 1_001), 2);
  assert.equal(calls, 2);
});

test("reusing a scoped key for a different side effect fails closed", async () => {
  const guard = new ScopedOutboundIdempotency<number>();
  let calls = 0;
  const send = () => Promise.resolve(++calls);

  assert.equal(await guard.run(
    { accountId: "account-1", groupId: "group-1", idempotencyKey: "reply:42" },
    { message: "first" }, send, 1_000), 1);

  assert.throws(
    () => guard.run(
      { accountId: "account-1", groupId: "group-1", idempotencyKey: "reply:42" },
      { message: "changed" }, send, 1_001),
    /reused with a different outbound payload/,
  );
  assert.equal(calls, 1);
});

test("failed sends release the key so a retry can execute", async () => {
  const guard = new ScopedOutboundIdempotency<number>();
  let calls = 0;
  const send = async () => {
    calls += 1;
    if (calls === 1) throw new Error("temporary failure");
    return 2;
  };

  await assert.rejects(() => guard.run(
    { accountId: "account-1", groupId: "group-1", idempotencyKey: "retry-key" },
    { message: "hello" }, send, 1_000), /temporary failure/);

  assert.equal(await guard.run(
    { accountId: "account-1", groupId: "group-1", idempotencyKey: "retry-key" },
    { message: "hello" }, send, 1_001), 2);
  assert.equal(calls, 2);
});

test("in-flight entries never expire and replay ttl starts after completion", async () => {
  let now = 1_000;
  const guard = new ScopedOutboundIdempotency<number>(100, () => now);
  let calls = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const send = async () => {
    calls += 1;
    await gate;
    return calls;
  };

  const first = guard.run(
    { accountId: "a", groupId: "g", idempotencyKey: "slow" },
    { message: "x" }, send);

  now = 1_500;
  const retryAfterNominalTtl = guard.run(
    { accountId: "a", groupId: "g", idempotencyKey: "slow" },
    { message: "x" }, send);
  assert.equal(calls, 0, "factory starts on the promise microtask");

  await Promise.resolve();
  assert.equal(calls, 1);
  release();
  assert.equal(await first, 1);
  assert.equal(await retryAfterNominalTtl, 1);
  assert.equal(calls, 1);

  now = 1_599;
  assert.equal(await guard.run(
    { accountId: "a", groupId: "g", idempotencyKey: "slow" },
    { message: "x" }, send), 1);
  assert.equal(calls, 1);

  now = 1_601;
  assert.equal(await guard.run(
    { accountId: "a", groupId: "g", idempotencyKey: "slow" },
    { message: "x" }, send), 2);
  assert.equal(calls, 2);
});

test("expired completed entries allow a fresh execution", async () => {
  let now = 1_000;
  const guard = new ScopedOutboundIdempotency<number>(100, () => now);
  let calls = 0;
  const send = () => Promise.resolve(++calls);

  assert.equal(await guard.run(
    { accountId: "a", groupId: "g", idempotencyKey: "k" }, { message: "x" }, send), 1);
  now = 1_099;
  assert.equal(await guard.run(
    { accountId: "a", groupId: "g", idempotencyKey: "k" }, { message: "x" }, send), 1);
  now = 1_101;
  assert.equal(await guard.run(
    { accountId: "a", groupId: "g", idempotencyKey: "k" }, { message: "x" }, send), 2);
});
