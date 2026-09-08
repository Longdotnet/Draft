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
