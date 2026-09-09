import assert from "node:assert/strict";
import test from "node:test";
import { BridgeHttpError, upstreamRetryAfterSeconds } from "../src/bridgeErrors.js";
import { ZaloRateLimitGuard } from "../src/zaloRateLimitGuard.js";

test("blocks later provider calls locally after upstream 429", async () => {
  let now = 1_000_000;
  let providerCalls = 0;
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
  });

  await assert.rejects(
    guard.run("account-a", async () => {
      providerCalls += 1;
      throw { response: { status: 429 } };
    }),
  );

  await assert.rejects(
    guard.run("account-a", async () => {
      providerCalls += 1;
      return "should-not-run";
    }),
    (error: unknown) => {
      assert.ok(error instanceof BridgeHttpError);
      assert.equal(error.kind, "rate_limit_cooldown");
      assert.equal(error.retryAfterSeconds, 60);
      return true;
    },
  );
  assert.equal(providerCalls, 1);

  now += 60_000;
  const result = await guard.run("account-a", async () => {
    providerCalls += 1;
    return "ok";
  });
  assert.equal(result, "ok");
  assert.equal(providerCalls, 2);
});

test("honors a longer upstream Retry-After and keeps account scopes isolated", async () => {
  let now = Date.parse("2026-09-08T05:00:00Z");
  let accountACalls = 0;
  let accountBCalls = 0;
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
  });

  await assert.rejects(
    guard.run("account-a", async () => {
      accountACalls += 1;
      throw {
        response: {
          status: 429,
          headers: { "retry-after": "120" },
        },
      };
    }),
  );

  const otherAccount = await guard.run("account-b", async () => {
    accountBCalls += 1;
    return "ok";
  });
  assert.equal(otherAccount, "ok");

  now += 90_000;
  await assert.rejects(
    guard.run("account-a", async () => {
      accountACalls += 1;
      return "too-early";
    }),
    (error: unknown) => {
      assert.ok(error instanceof BridgeHttpError);
      assert.equal(error.retryAfterSeconds, 30);
      return true;
    },
  );

  assert.equal(accountACalls, 1);
  assert.equal(accountBCalls, 1);
});

test("does not shorten provider Retry-After to the local exponential cooldown cap", async () => {
  let now = Date.parse("2026-09-08T05:00:00Z");
  let providerCalls = 0;
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
    maxCooldownMs: 15 * 60_000,
  });

  await assert.rejects(
    guard.run("account-a", async () => {
      providerCalls += 1;
      throw {
        response: {
          status: 429,
          headers: { "retry-after": "3600" },
        },
      };
    }),
  );

  now += 16 * 60_000;
  await assert.rejects(
    guard.run("account-a", async () => {
      providerCalls += 1;
      return "too-early";
    }),
    (error: unknown) => {
      assert.ok(error instanceof BridgeHttpError);
      assert.equal(error.retryAfterSeconds, 44 * 60);
      return true;
    },
  );
  assert.equal(providerCalls, 1);

  now += 44 * 60_000;
  assert.equal(await guard.run("account-a", async () => {
    providerCalls += 1;
    return "ok";
  }), "ok");
  assert.equal(providerCalls, 2);
});

test("bounds malformed or extreme Retry-After independently from local backoff", async () => {
  let now = Date.parse("2026-09-08T05:00:00Z");
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
    maxCooldownMs: 15 * 60_000,
    maxRetryAfterMs: 2 * 60 * 60_000,
  });

  await assert.rejects(
    guard.run("account-a", async () => {
      throw {
        response: {
          status: 429,
          headers: { "retry-after": String(30 * 24 * 60 * 60) },
        },
      };
    }),
  );

  now += 60 * 60_000;
  await assert.rejects(
    guard.run("account-a", async () => "too-early"),
    (error: unknown) => {
      assert.ok(error instanceof BridgeHttpError);
      assert.equal(error.retryAfterSeconds, 60 * 60);
      return true;
    },
  );

  now += 60 * 60_000;
  assert.equal(await guard.run("account-a", async () => "recovered"), "recovered");
});

test("serializes outbound work per account and enforces the configured minimum gap", async () => {
  let now = 10_000;
  const starts: number[] = [];
  const sleeps: number[] = [];
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 750,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });

  const first = guard.run("account-a", async () => {
    starts.push(now);
    return "first";
  });
  const second = guard.run("account-a", async () => {
    starts.push(now);
    return "second";
  });

  assert.deepEqual(await Promise.all([first, second]), ["first", "second"]);
  assert.deepEqual(starts, [10_000, 10_750]);
  assert.deepEqual(sleeps, [750]);
});

test("backs off queued provider work after retryable non-429 failures", async () => {
  let now = 20_000;
  const starts: number[] = [];
  const sleeps: number[] = [];
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 750,
    transientBaseDelayMs: 2_000,
    transientMaxDelayMs: 30_000,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });

  const first = guard.run("account-a", async () => {
    starts.push(now);
    throw { response: { status: 503 } };
  });
  const second = guard.run("account-a", async () => {
    starts.push(now);
    throw { response: { status: 503 } };
  });
  const third = guard.run("account-a", async () => {
    starts.push(now);
    return "recovered";
  });

  await assert.rejects(first);
  await assert.rejects(second);
  assert.equal(await third, "recovered");
  assert.deepEqual(starts, [20_000, 22_000, 26_000]);
  assert.deepEqual(sleeps, [2_000, 4_000]);
});

test("transient provider backoff is isolated per account", async () => {
  let now = 30_000;
  const starts: Array<[string, number]> = [];
  const sleeps: number[] = [];
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 0,
    transientBaseDelayMs: 2_000,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });

  await assert.rejects(guard.run("account-a", async () => {
    starts.push(["a", now]);
    throw { response: { status: 503 } };
  }));

  assert.equal(await guard.run("account-b", async () => {
    starts.push(["b", now]);
    return "ok";
  }), "ok");

  assert.equal(await guard.run("account-a", async () => {
    starts.push(["a", now]);
    return "recovered";
  }), "recovered");

  assert.deepEqual(starts, [["a", 30_000], ["b", 30_000], ["a", 32_000]]);
  assert.deepEqual(sleeps, [2_000]);
});

test("successful provider call resets transient failure backoff", async () => {
  let now = 40_000;
  const starts: number[] = [];
  const sleeps: number[] = [];
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 0,
    transientBaseDelayMs: 2_000,
    transientMaxDelayMs: 30_000,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });

  await assert.rejects(guard.run("account-a", async () => {
    starts.push(now);
    throw { response: { status: 503 } };
  }));
  assert.equal(await guard.run("account-a", async () => {
    starts.push(now);
    return "ok";
  }), "ok");
  await assert.rejects(guard.run("account-a", async () => {
    starts.push(now);
    throw { response: { status: 503 } };
  }));
  assert.equal(await guard.run("account-a", async () => {
    starts.push(now);
    return "ok-again";
  }), "ok-again");

  assert.deepEqual(starts, [40_000, 42_000, 42_000, 44_000]);
  assert.deepEqual(sleeps, [2_000, 2_000]);
});

test("non-retryable auth failures keep only the normal minimum gap", async () => {
  let now = 50_000;
  const starts: number[] = [];
  const sleeps: number[] = [];
  const guard = new ZaloRateLimitGuard({
    now: () => now,
    minGapMs: 750,
    transientBaseDelayMs: 2_000,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });

  await assert.rejects(guard.run("account-a", async () => {
    starts.push(now);
    throw { response: { status: 401 } };
  }));
  assert.equal(await guard.run("account-a", async () => {
    starts.push(now);
    return "ok";
  }), "ok");

  assert.deepEqual(starts, [50_000, 50_750]);
  assert.deepEqual(sleeps, [750]);
});

test("parses Retry-After seconds and HTTP-date without exposing provider bodies", () => {
  const now = Date.parse("2026-09-08T05:00:00Z");
  assert.equal(
    upstreamRetryAfterSeconds({ response: { status: 429, headers: { "Retry-After": "42" } } }, now),
    42,
  );
  assert.equal(
    upstreamRetryAfterSeconds({
      response: {
        status: 429,
        headers: { "retry-after": "Mon, 08 Sep 2026 05:02:00 GMT" },
      },
    }, now),
    120,
  );
});
