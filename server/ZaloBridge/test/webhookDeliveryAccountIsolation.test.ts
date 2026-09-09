import assert from "node:assert/strict";
import test from "node:test";
import { WebhookDeliveryQueue } from "../src/webhookDeliveryQueue.js";

function request(accountId: string, deliveryId: string, content: string) {
  return {
    accountId,
    deliveryId,
    kind: "message" as const,
    url: "https://api.example/zalo/events",
    webhookKey: "secret-key",
    body: { accountId, messageId: deliveryId, content },
  };
}

test("the same provider delivery id stays independent across Zalo accounts", async () => {
  const started: string[] = [];
  let releaseAccount1!: () => void;
  const account1Blocked = new Promise<void>((resolve) => { releaseAccount1 = resolve; });

  const queue = new WebhookDeliveryQueue({
    fetchImpl: async (_input, init) => {
      const body = JSON.parse(String(init?.body)) as { accountId: string };
      started.push(body.accountId);
      if (body.accountId === "acc-1") await account1Blocked;
      return new Response(null, { status: 204 });
    },
  });

  const first = queue.enqueue(request("acc-1", "provider-message-42", "from account one"));
  const second = queue.enqueue(request("acc-2", "provider-message-42", "from account two"));

  await second;
  assert.deepEqual(started, ["acc-1", "acc-2"]);
  assert.equal(queue.snapshot().accepted, 2);
  assert.equal(queue.snapshot().coalescedDuplicates, 0);
  assert.equal(queue.snapshot().idempotencyConflicts, 0);

  releaseAccount1();
  await first;
  assert.equal(queue.snapshot().delivered, 2);
});

test("a conflicting delivery id is rejected only inside the owning account", async () => {
  let release!: () => void;
  const blocked = new Promise<void>((resolve) => { release = resolve; });
  let calls = 0;

  const queue = new WebhookDeliveryQueue({
    fetchImpl: async (_input, init) => {
      calls += 1;
      const body = JSON.parse(String(init?.body)) as { accountId: string };
      if (body.accountId === "acc-1") await blocked;
      return new Response(null, { status: 204 });
    },
  });

  const owner = queue.enqueue(request("acc-1", "same-id", "original"));
  await assert.rejects(
    queue.enqueue(request("acc-1", "same-id", "different")),
    /conflicts with an in-flight payload for this account/,
  );

  await queue.enqueue(request("acc-2", "same-id", "different account is independent"));
  assert.equal(queue.snapshot().accepted, 2);
  assert.equal(queue.snapshot().idempotencyConflicts, 1);

  release();
  await owner;
  assert.equal(calls, 2);
});
