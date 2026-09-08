import assert from "node:assert/strict";
import test from "node:test";
import { BridgeHttpError } from "../src/bridgeErrors.js";
import type { ZaloCredentials } from "../src/contracts.js";
import { ZaloProviderTrafficGovernor } from "../src/zaloProviderTraffic.js";
import { ZaloRateLimitGuard, type ZaloRateLimitEvent } from "../src/zaloRateLimitGuard.js";

function credentials(seed: string): ZaloCredentials {
  return {
    cookie: [{ name: `cookie-${seed}`, value: `secret-${seed}` }],
    imei: `imei-${seed}`,
    userAgent: `ua-${seed}`,
    language: "vi",
  };
}

test("rate guard telemetry distinguishes a real provider 429 from a local cooldown rejection", async () => {
  let now = 1_000_000;
  let providerCalls = 0;
  const events: ZaloRateLimitEvent[] = [];
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
    onEvent: (event) => events.push(event),
  });

  await assert.rejects(
    guard.run("scope-a", async () => {
      providerCalls += 1;
      throw { response: { status: 429, headers: { "retry-after": "120" } } };
    }, "polls.list"),
  );

  await assert.rejects(
    guard.run("scope-a", async () => {
      providerCalls += 1;
      return "must-not-run";
    }, "message.send"),
    (error: unknown) => {
      assert.ok(error instanceof BridgeHttpError);
      assert.equal(error.kind, "rate_limit_cooldown");
      return true;
    },
  );

  assert.equal(providerCalls, 1);
  assert.deepEqual(
    events.map((event) => ({
      type: event.type,
      operation: event.operation,
      providerTouched: event.providerTouched,
      retryAfterSeconds: event.retryAfterSeconds ?? null,
    })),
    [
      { type: "provider_attempt", operation: "polls.list", providerTouched: true, retryAfterSeconds: null },
      { type: "provider_failure", operation: "polls.list", providerTouched: true, retryAfterSeconds: 120 },
      { type: "cooldown_rejected", operation: "message.send", providerTouched: false, retryAfterSeconds: 120 },
    ],
  );

  now += 120_000;
  assert.equal(await guard.run("scope-a", async () => "recovered", "message.send"), "recovered");
});

test("provider diagnostics expose account-safe operation counters without credential material", async () => {
  let now = 2_000_000;
  const accountCredentials = credentials("diagnostics");
  const governor = new ZaloProviderTrafficGovernor({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
  });
  governor.bindAccount("account-a", accountCredentials);

  assert.equal(
    await governor.runReadWithCredentials(
      accountCredentials,
      "poll:42",
      async () => "poll-ok",
      "poll.detail",
    ),
    "poll-ok",
  );

  await assert.rejects(
    governor.runWithAccount(
      "account-a",
      async () => { throw { response: { status: 429 } }; },
      "message.send",
    ),
  );

  await assert.rejects(
    governor.runReadWithCredentials(
      accountCredentials,
      "group:7:roles",
      async () => "must-not-touch-provider",
      "roles.read",
    ),
  );

  const diagnostics = governor.getDiagnostics();
  assert.equal(diagnostics.aggregate.providerAttempts, 2);
  assert.equal(diagnostics.aggregate.providerSuccesses, 1);
  assert.equal(diagnostics.aggregate.providerFailures, 1);
  assert.equal(diagnostics.aggregate.localCooldownRejects, 1);
  assert.equal(diagnostics.aggregate.activeCooldowns, 1);
  assert.equal(diagnostics.accounts.length, 1);
  assert.equal(diagnostics.accounts[0]!.accountId, "account-a");
  assert.equal(diagnostics.accounts[0]!.coolingDown, true);
  assert.equal(diagnostics.accounts[0]!.retryAfterSeconds, 60);
  assert.equal(diagnostics.accounts[0]!.lastFailureKind, "rate_limited");

  const poll = diagnostics.operations.find((item) => item.operation === "poll.detail");
  const message = diagnostics.operations.find((item) => item.operation === "message.send");
  const roles = diagnostics.operations.find((item) => item.operation === "roles.read");
  assert.equal(poll?.providerAttempts, 1);
  assert.equal(poll?.providerSuccesses, 1);
  assert.equal(message?.providerFailures, 1);
  assert.equal(roles?.providerAttempts, 0);
  assert.equal(roles?.localCooldownRejects, 1);

  const serialized = JSON.stringify(diagnostics);
  assert.equal(serialized.includes("secret-diagnostics"), false);
  assert.equal(serialized.includes("cookie-diagnostics"), false);
  assert.equal(serialized.includes("imei-diagnostics"), false);
  assert.equal(serialized.includes("credentials:"), false, "credential fingerprints are internal scope keys and must not be exposed");

  const health = governor.getHealthSnapshot();
  assert.equal("accounts" in health, false, "public health must not expose account identifiers");
  assert.equal(health.activeCooldowns, 1);
  assert.equal(health.providerAttempts, 2);

  now += 60_000;
  assert.equal(governor.getDiagnostics().aggregate.activeCooldowns, 0);
});

test("coalesced reads are counted as saved calls and never as provider attempts", async () => {
  const accountCredentials = credentials("coalesce-observe");
  const governor = new ZaloProviderTrafficGovernor({ minGapMs: 0 });
  let providerCalls = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });

  const first = governor.runReadWithCredentials(
    accountCredentials,
    "group:123:polls",
    async () => {
      providerCalls += 1;
      await gate;
      return ["poll-a"];
    },
    "polls.list",
  );
  const duplicate = governor.runReadWithCredentials(
    accountCredentials,
    "group:123:polls",
    async () => {
      providerCalls += 1;
      return ["poll-b"];
    },
    "polls.list",
  );

  release();
  assert.deepEqual(await Promise.all([first, duplicate]), [["poll-a"], ["poll-a"]]);
  assert.equal(providerCalls, 1);

  const diagnostics = governor.getDiagnostics();
  const polls = diagnostics.operations.find((item) => item.operation === "polls.list");
  assert.equal(polls?.providerAttempts, 1);
  assert.equal(polls?.providerSuccesses, 1);
  assert.equal(polls?.coalescedReads, 1);
  assert.equal(diagnostics.aggregate.coalescedReads, 1);
});

test("telemetry observer failures cannot break provider traffic", async () => {
  const guard = new ZaloRateLimitGuard({
    minGapMs: 0,
    onEvent: () => { throw new Error("telemetry sink unavailable"); },
  });

  assert.equal(
    await guard.run("scope-a", async () => "provider-result", "groups.list"),
    "provider-result",
  );
});
