import assert from "node:assert/strict";
import test from "node:test";
import { WebhookDeliveryQueue } from "../src/webhookDeliveryQueue.js";

function request(accountId: string, deliveryId: string, url = "https://api.example/zalo/events") {
  return {
    accountId,
    deliveryId,
    kind: "message" as const,
    url,
    webhookKey: "secret-key",
    body: { accountId, messageId: deliveryId, content: "hello" },
  };
}

test("transient webhook failures retry until API delivery succeeds", async () => {
  let calls = 0;
  const delays: number[] = [];
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      calls += 1;
      return calls < 3
        ? new Response("sleeping", { status: 503 })
        : new Response(null, { status: 204 });
    },
    sleep: async (milliseconds) => { delays.push(milliseconds); },
    random: () => 0,
  });

  await queue.enqueue(request("acc-1", "m-1"));

  assert.equal(calls, 3);
  assert.deepEqual(delays, [2_000, 4_000]);
  assert.deepEqual(queue.snapshot(), {
    pending: 0,
    accepted: 1,
    delivered: 1,
    retryAttempts: 2,
    permanentFailures: 0,
    expired: 0,
    overflowRejected: 0,
    coalescedDuplicates: 0,
    idempotencyConflicts: 0,
  });
});

test("Retry-After from sleeping API is honored without touching Zalo", async () => {
  let calls = 0;
  const delays: number[] = [];
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      calls += 1;
      return calls === 1
        ? new Response("busy", { status: 429, headers: { "retry-after": "30" } })
        : new Response(null, { status: 204 });
    },
    sleep: async (milliseconds) => { delays.push(milliseconds); },
    random: () => 0,
  });

  await queue.enqueue(request("acc-1", "m-2"));
  assert.equal(calls, 2);
  assert.deepEqual(delays, [30_000]);
});

test("deliveries stay ordered per account and endpoint across retries", async () => {
  const calls: string[] = [];
  let firstAttempts = 0;
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async (_input, init) => {
      const body = JSON.parse(String(init?.body)) as { messageId: string };
      calls.push(body.messageId);
      if (body.messageId === "m-1") {
        firstAttempts += 1;
        if (firstAttempts === 1) return new Response("sleeping", { status: 503 });
      }
      return new Response(null, { status: 204 });
    },
    sleep: async () => undefined,
    random: () => 0,
  });

  await Promise.all([
    queue.enqueue(request("acc-1", "m-1")),
    queue.enqueue(request("acc-1", "m-2")),
  ]);

  assert.deepEqual(calls, ["m-1", "m-1", "m-2"]);
});

test("one account outage does not block another account", async () => {
  let releaseFirst!: () => void;
  const firstBlocked = new Promise<void>((resolve) => { releaseFirst = resolve; });
  let account2Delivered = false;
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async (_input, init) => {
      const body = JSON.parse(String(init?.body)) as { accountId: string };
      if (body.accountId === "acc-1") await firstBlocked;
      else account2Delivered = true;
      return new Response(null, { status: 204 });
    },
  });

  const first = queue.enqueue(request("acc-1", "m-1"));
  await queue.enqueue(request("acc-2", "m-2"));
  assert.equal(account2Delivered, true);
  releaseFirst();
  await first;
});

test("non-retryable webhook auth failure fails fast instead of creating an endless backlog", async () => {
  let calls = 0;
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      calls += 1;
      return new Response("unauthorized", { status: 401 });
    },
    sleep: async () => { throw new Error("must not sleep"); },
  });

  await assert.rejects(queue.enqueue(request("acc-1", "m-3")), /401/);
  assert.equal(calls, 1);
  assert.equal(queue.snapshot().permanentFailures, 1);
});

test("pending webhook memory is bounded per account", async () => {
  let release!: () => void;
  const blocked = new Promise<void>((resolve) => { release = resolve; });
  const queue = new WebhookDeliveryQueue({
    maxPendingPerAccount: 1,
    fetchImpl: async () => {
      await blocked;
      return new Response(null, { status: 204 });
    },
  });

  const first = queue.enqueue(request("acc-1", "m-1"));
  await assert.rejects(queue.enqueue(request("acc-1", "m-2")), /backlog exceeded/);
  assert.equal(queue.snapshot().overflowRejected, 1);
  release();
  await first;
});

test("duplicate delivery id shares the exact in-flight promise and provider outcome", async () => {
  let release!: () => void;
  const blocked = new Promise<void>((resolve) => { release = resolve; });
  let calls = 0;
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      calls += 1;
      await blocked;
      return new Response("unauthorized", { status: 401 });
    },
  });

  const first = queue.enqueue(request("acc-1", "m-1"));
  const duplicate = queue.enqueue(request("acc-1", "m-1"));
  assert.equal(duplicate, first, "duplicate callers must observe the original delivery outcome");
  assert.equal(queue.snapshot().coalescedDuplicates, 1);

  release();
  await assert.rejects(first, /401/);
  await assert.rejects(duplicate, /401/);
  assert.equal(calls, 1);
  assert.equal(queue.snapshot().accepted, 1);
});

test("same delivery id with different payload fails closed while original stays in flight", async () => {
  let release!: () => void;
  const blocked = new Promise<void>((resolve) => { release = resolve; });
  let calls = 0;
  const logs: Array<Record<string, unknown>> = [];
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      calls += 1;
      await blocked;
      return new Response(null, { status: 204 });
    },
    onLog: (event) => logs.push(event),
  });

  const first = queue.enqueue(request("acc-1", "m-1"));
  const conflicting = {
    ...request("acc-1", "m-1"),
    body: { accountId: "acc-1", messageId: "m-1", content: "different event" },
  };

  await assert.rejects(queue.enqueue(conflicting), /conflicts with an in-flight payload/);
  assert.equal(queue.snapshot().idempotencyConflicts, 1);
  assert.equal(queue.snapshot().accepted, 1);
  assert.equal(logs.some((entry) => entry.outcome === "idempotency_conflict"), true);

  release();
  await first;
  assert.equal(calls, 1);
});

test("webhook-key rotation does not create an idempotency conflict for the same event", async () => {
  let release!: () => void;
  const blocked = new Promise<void>((resolve) => { release = resolve; });
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      await blocked;
      return new Response(null, { status: 204 });
    },
  });

  const first = queue.enqueue(request("acc-1", "m-rotate"));
  const rotated = queue.enqueue({ ...request("acc-1", "m-rotate"), webhookKey: "rotated-key" });
  assert.equal(rotated, first);
  assert.equal(queue.snapshot().idempotencyConflicts, 0);
  assert.equal(queue.snapshot().coalescedDuplicates, 1);

  release();
  await first;
});

test("drain waits for webhook deliveries accepted before graceful shutdown", async () => {
  let release!: () => void;
  const blocked = new Promise<void>((resolve) => { release = resolve; });
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      await blocked;
      return new Response(null, { status: 204 });
    },
  });

  const delivery = queue.enqueue(request("acc-1", "shutdown-1"));
  const drain = queue.drain(1_000);
  await new Promise<void>((resolve) => setTimeout(resolve, 10));
  assert.equal(queue.snapshot().pending, 1);

  release();
  assert.equal(await drain, true);
  await delivery;
  assert.equal(queue.snapshot().pending, 0);
  assert.equal(queue.snapshot().delivered, 1);
});

test("drain is bounded when an accepted webhook cannot finish inside shutdown grace", async () => {
  const never = new Promise<void>(() => undefined);
  const queue = new WebhookDeliveryQueue({
    fetchImpl: async () => {
      await never;
      return new Response(null, { status: 204 });
    },
  });

  void queue.enqueue(request("acc-1", "shutdown-timeout")).catch(() => undefined);
  assert.equal(await queue.drain(20), false);
  assert.equal(queue.snapshot().pending, 1);
});
