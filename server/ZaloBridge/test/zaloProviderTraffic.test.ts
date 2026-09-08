import assert from "node:assert/strict";
import test from "node:test";
import { BridgeHttpError } from "../src/bridgeErrors.js";
import type { ZaloCredentials } from "../src/contracts.js";
import { ZaloProviderTrafficGovernor } from "../src/zaloProviderTraffic.js";

function credentials(seed: string): ZaloCredentials {
  return {
    cookie: [{ name: `cookie-${seed}`, value: `value-${seed}` }],
    imei: `imei-${seed}`,
    userAgent: `ua-${seed}`,
    language: "vi",
  };
}

test("a read-side 429 cools down outbound traffic for the same bound account", async () => {
  let now = 1_000_000;
  let readCalls = 0;
  let writeCalls = 0;
  const accountCredentials = credentials("a");
  const governor = new ZaloProviderTrafficGovernor({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
  });
  governor.bindAccount("account-a", accountCredentials);

  await assert.rejects(
    governor.runWithCredentials(accountCredentials, async () => {
      readCalls += 1;
      throw { response: { status: 429 } };
    }),
  );

  await assert.rejects(
    governor.runWithAccount("account-a", async () => {
      writeCalls += 1;
      return "should-not-touch-zalo";
    }),
    (error: unknown) => {
      assert.ok(error instanceof BridgeHttpError);
      assert.equal(error.kind, "rate_limit_cooldown");
      assert.equal(error.retryAfterSeconds, 60);
      return true;
    },
  );

  assert.equal(readCalls, 1);
  assert.equal(writeCalls, 0);

  now += 60_000;
  assert.equal(
    await governor.runWithAccount("account-a", async () => {
      writeCalls += 1;
      return "ok";
    }),
    "ok",
  );
  assert.equal(writeCalls, 1);
});

test("provider cooldown remains isolated across connected accounts", async () => {
  const governor = new ZaloProviderTrafficGovernor({
    minGapMs: 0,
    defaultCooldownMs: 60_000,
  });
  const credentialsA = credentials("a");
  const credentialsB = credentials("b");
  governor.bindAccount("account-a", credentialsA);
  governor.bindAccount("account-b", credentialsB);

  await assert.rejects(
    governor.runWithCredentials(credentialsA, async () => {
      throw { response: { status: 429 } };
    }),
  );

  const result = await governor.runWithAccount("account-b", async () => "account-b-ok");
  assert.equal(result, "account-b-ok");
});

test("read and write work share one pacing queue for a bound account", async () => {
  let now = 20_000;
  const starts: Array<{ kind: string; at: number }> = [];
  const sleeps: number[] = [];
  const accountCredentials = credentials("a");
  const governor = new ZaloProviderTrafficGovernor({
    now: () => now,
    minGapMs: 750,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });
  governor.bindAccount("account-a", accountCredentials);

  const read = governor.runWithCredentials(accountCredentials, async () => {
    starts.push({ kind: "read", at: now });
    return "read";
  });
  const write = governor.runWithAccount("account-a", async () => {
    starts.push({ kind: "write", at: now });
    return "write";
  });

  assert.deepEqual(await Promise.all([read, write]), ["read", "write"]);
  assert.deepEqual(starts, [
    { kind: "read", at: 20_000 },
    { kind: "write", at: 20_750 },
  ]);
  assert.deepEqual(sleeps, [750]);
});

test("identical concurrent provider reads share one authoritative in-flight call", async () => {
  const accountCredentials = credentials("coalesce");
  const governor = new ZaloProviderTrafficGovernor({ minGapMs: 0 });
  let providerCalls = 0;
  let markStarted!: () => void;
  const started = new Promise<void>((resolve) => { markStarted = resolve; });
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });

  const first = governor.runReadWithCredentials(accountCredentials, "group:123:polls", async () => {
    providerCalls += 1;
    markStarted();
    await gate;
    return ["poll-a"];
  });
  const second = governor.runReadWithCredentials(accountCredentials, "group:123:polls", async () => {
    providerCalls += 1;
    return ["poll-b"];
  });

  await started;
  assert.equal(providerCalls, 1, "the duplicate must not reach the provider while the first read is in flight");
  release();
  assert.deepEqual(await Promise.all([first, second]), [["poll-a"], ["poll-a"]]);
  assert.equal(providerCalls, 1);
});

test("completed provider reads are never reused as a stale cache", async () => {
  const accountCredentials = credentials("fresh");
  const governor = new ZaloProviderTrafficGovernor({ minGapMs: 0 });
  let providerCalls = 0;

  const first = await governor.runReadWithCredentials(accountCredentials, "poll:42", async () => {
    providerCalls += 1;
    return { version: 1 };
  });
  const second = await governor.runReadWithCredentials(accountCredentials, "poll:42", async () => {
    providerCalls += 1;
    return { version: 2 };
  });

  assert.deepEqual(first, { version: 1 });
  assert.deepEqual(second, { version: 2 });
  assert.equal(providerCalls, 2, "sequential reads must revalidate with Zalo");
});

test("failed shared reads are evicted so later recovery can reach the provider", async () => {
  const accountCredentials = credentials("failure");
  const governor = new ZaloProviderTrafficGovernor({ minGapMs: 0 });
  let providerCalls = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });

  const first = governor.runReadWithCredentials(accountCredentials, "group:123:roles", async () => {
    providerCalls += 1;
    await gate;
    throw { response: { status: 503 } };
  });
  const duplicate = governor.runReadWithCredentials(accountCredentials, "group:123:roles", async () => {
    providerCalls += 1;
    return "should-not-run";
  });
  release();
  await assert.rejects(first);
  await assert.rejects(duplicate);
  assert.equal(providerCalls, 1);

  const recovered = await governor.runReadWithCredentials(accountCredentials, "group:123:roles", async () => {
    providerCalls += 1;
    return "recovered";
  });
  assert.equal(recovered, "recovered");
  assert.equal(providerCalls, 2);
});

test("read coalescing is isolated by credential scope and authoritative read key", async () => {
  const credentialsA = credentials("scope-a");
  const credentialsB = credentials("scope-b");
  const governor = new ZaloProviderTrafficGovernor({ minGapMs: 0 });
  let providerCalls = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });

  const sameScope = governor.runReadWithCredentials(credentialsA, "poll:1", async () => {
    providerCalls += 1;
    await gate;
    return "a-1";
  });
  const differentKey = governor.runReadWithCredentials(credentialsA, "poll:2", async () => {
    providerCalls += 1;
    return "a-2";
  });
  const differentCredentials = governor.runReadWithCredentials(credentialsB, "poll:1", async () => {
    providerCalls += 1;
    return "b-1";
  });

  release();
  assert.deepEqual(await Promise.all([sameScope, differentKey, differentCredentials]), ["a-1", "a-2", "b-1"]);
  assert.equal(providerCalls, 3);
});
