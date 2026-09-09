import assert from "node:assert/strict";
import test from "node:test";
import { WebhookDeliveryQueue } from "../src/webhookDeliveryQueue.js";

function request(deliveryId: string) {
  return {
    accountId: "fuzz-account",
    deliveryId,
    kind: "message" as const,
    url: "https://api.example/zalo/events",
    webhookKey: "test-key",
    body: {
      accountId: "fuzz-account",
      messageId: deliveryId,
      groupId: "fuzz-group",
      content: "stateful fuzz delivery",
    },
  };
}

function nextXorShift32(value: number): number {
  let state = value >>> 0;
  if (state === 0) state = 0x6d2b79f5;
  state ^= state << 13;
  state ^= state >>> 17;
  state ^= state << 5;
  return state >>> 0;
}

async function replayExpiryScenario(seed: number) {
  let randomState = seed >>> 0;
  randomState = nextXorShift32(randomState);
  const baseDelayMs = 100 + (randomState % 401);
  randomState = nextXorShift32(randomState);
  const maxDeliveryAgeMs = baseDelayMs + (randomState % (baseDelayMs * 4 + 1));

  let now = 0;
  const attemptAges: number[] = [];
  const queue = new WebhookDeliveryQueue({
    baseDelayMs,
    maxDelayMs: baseDelayMs * 8,
    maxDeliveryAgeMs,
    now: () => now,
    random: () => 0,
    sleep: async (milliseconds) => {
      now += milliseconds;
    },
    fetchImpl: async () => {
      attemptAges.push(now);
      return new Response("still unavailable", { status: 503 });
    },
    onLog: () => undefined,
  });

  await assert.rejects(
    queue.enqueue(request(`expiry-${seed}`)),
    /remained unavailable/,
  );

  return { baseDelayMs, maxDeliveryAgeMs, attemptAges, stats: queue.snapshot() };
}

test("minimized reproducer: retry budget expiry fences the next webhook side-effect attempt", async () => {
  let now = 0;
  const attemptAges: number[] = [];
  const queue = new WebhookDeliveryQueue({
    baseDelayMs: 100,
    maxDeliveryAgeMs: 100,
    now: () => now,
    random: () => 0,
    sleep: async (milliseconds) => {
      now += milliseconds;
    },
    fetchImpl: async () => {
      attemptAges.push(now);
      return new Response("still unavailable", { status: 503 });
    },
    onLog: () => undefined,
  });

  await assert.rejects(queue.enqueue(request("minimized-expiry")), /remained unavailable/);

  assert.deepEqual(
    attemptAges,
    [0],
    "failure fingerprint webhook-delivery:attempt-after-expiry: no API side-effect attempt may start at or after the retry deadline",
  );
  assert.equal(queue.snapshot().expired, 1);
  assert.equal(queue.snapshot().delivered, 0);
});

test("temporal stateful fuzz corpus never starts a webhook attempt at or after max delivery age", async () => {
  for (let seed = 1; seed <= 128; seed += 1) {
    const result = await replayExpiryScenario(seed);
    assert.ok(result.attemptAges.length >= 1, `seed ${seed} must exercise delivery`);
    assert.ok(
      result.attemptAges.every((age) => age < result.maxDeliveryAgeMs),
      `failure fingerprint webhook-delivery:attempt-after-expiry seed=${seed} base=${result.baseDelayMs} maxAge=${result.maxDeliveryAgeMs} attempts=${result.attemptAges.join(",")}`,
    );
    assert.equal(result.stats.expired, 1, `seed ${seed}`);
    assert.equal(result.stats.delivered, 0, `seed ${seed}`);
  }
});
