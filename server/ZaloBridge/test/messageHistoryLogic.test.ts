import assert from "node:assert/strict";
import test from "node:test";
import {
  buildMessageHistoryProbe,
  normalizeHistoricalMessage,
  normalizeUnixMs,
} from "../src/messageHistoryLogic.js";

test("normalizes historical sender UID and second timestamps without losing precision", () => {
  const message = normalizeHistoricalMessage({
    isSelf: false,
    data: {
      msgId: "90071992547409931234",
      uidFrom: "12345678901234567890_0",
      dName: "Nguyễn A",
      msgType: "chat",
      ts: "1720000000",
      content: "xin chào",
    },
  });

  assert.ok(message);
  assert.equal(message.messageId, "90071992547409931234");
  assert.equal(message.senderId, "12345678901234567890");
  assert.equal(message.sentAtUnixMs, 1_720_000_000_000);
  assert.equal(message.content, "xin chào");
});

test("rejects history records without a stable message ID or sender UID", () => {
  assert.equal(normalizeHistoricalMessage({ data: { uidFrom: "u1" } }), null);
  assert.equal(normalizeHistoricalMessage({ data: { msgId: "m1" } }), null);
});

test("history probe preserves pagination evidence and timestamp coverage", () => {
  const messages = [
    {
      messageId: "m1",
      senderId: "u1",
      senderName: "A",
      content: "",
      messageType: "chat",
      isFromBot: false,
      sentAtUnixMs: 2_000,
    },
    {
      messageId: "m2",
      senderId: "u2",
      senderName: "B",
      content: "",
      messageType: "chat",
      isFromBot: false,
      sentAtUnixMs: 1_000,
    },
  ];

  const probe = buildMessageHistoryProbe("group", 100, messages, 1, "last", null);

  assert.equal(probe.returnedCount, 2);
  assert.equal(probe.more, 1);
  assert.equal(probe.oldestMessageAtUnixMs, 1_000);
  assert.equal(probe.newestMessageAtUnixMs, 2_000);
});

test("invalid timestamps are not invented", () => {
  assert.equal(normalizeUnixMs(undefined), 0);
  assert.equal(normalizeUnixMs("not-a-time"), 0);
});
