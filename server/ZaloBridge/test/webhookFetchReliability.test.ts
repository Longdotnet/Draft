import assert from "node:assert/strict";
import test from "node:test";
import {
  createReliableWebhookFetch,
  WebhookTerminalFailureMemo,
} from "../src/webhookFetchReliability.js";

function webhookInit(body: Record<string, unknown>): RequestInit {
  return {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-zalo-bridge-key": "secret-rotates-without-changing-event-identity",
    },
    body: JSON.stringify(body),
  };
}

test("final queue failure is memoized so legacy outer retries do not start new delivery cycles", async () => {
  let queueCalls = 0;
  const terminal = new Error("permanent webhook failure");
  const queue = {
    async enqueue() {
      queueCalls += 1;
      throw terminal;
    },
  };
  const baseFetch = async () => {
    throw new Error("base fetch should not be used for recognized webhooks");
  };
  const reliableFetch = createReliableWebhookFetch(baseFetch as typeof fetch, queue);
  const body = {
    accountId: "account-a",
    groupId: "group-a",
    messageId: "message-1",
    senderId: "member-a",
    content: "hello",
  };

  for (let attempt = 0; attempt < 3; attempt += 1) {
    await assert.rejects(
      reliableFetch("https://api.example/zalo/events", webhookInit(body)),
      (error) => error === terminal,
    );
  }

  assert.equal(queueCalls, 1, "one event must have one queue-owned retry lifecycle");
});

test("memo expires so a later provider/listener replay can recover the event", async () => {
  let now = 1_000;
  let queueCalls = 0;
  const terminal = new Error("temporary outage exhausted queue budget");
  const queue = {
    async enqueue() {
      queueCalls += 1;
      if (queueCalls === 1) throw terminal;
    },
  };
  const memo = new WebhookTerminalFailureMemo(10_000, () => now);
  const baseFetch = async () => {
    throw new Error("base fetch should not be used for recognized webhooks");
  };
  const reliableFetch = createReliableWebhookFetch(baseFetch as typeof fetch, queue, memo);
  const body = {
    accountId: "account-a",
    groupId: "group-a",
    messageId: "message-2",
    senderId: "member-a",
    content: "recover me",
  };

  await assert.rejects(reliableFetch("https://api.example/zalo/events", webhookInit(body)));
  now += 3_000;
  await assert.rejects(reliableFetch("https://api.example/zalo/events", webhookInit(body)));
  assert.equal(queueCalls, 1);

  now += 8_000;
  const response = await reliableFetch("https://api.example/zalo/events", webhookInit(body));
  assert.equal(response.status, 204);
  assert.equal(queueCalls, 2, "a genuine later replay must be allowed after the short suppression window");
});

test("terminal failure memo is payload-sensitive", () => {
  let now = 100;
  const memo = new WebhookTerminalFailureMemo(10_000, () => now);
  const error = new Error("failed");

  memo.remember("message:a:1", "signature-a", error);
  assert.equal(memo.get("message:a:1", "signature-a"), error);
  assert.equal(memo.get("message:a:1", "signature-b"), null);

  now += 10_001;
  assert.equal(memo.get("message:a:1", "signature-a"), null);
});
