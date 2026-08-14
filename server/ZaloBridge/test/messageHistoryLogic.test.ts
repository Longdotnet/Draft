import assert from "node:assert/strict";
import test from "node:test";
import {
  buildMessageHistoryProbe,
  buildUnsupportedMessageHistoryProbe,
  isHistoryEndpointUnavailable,
  normalizeHistoricalMessage,
  normalizeMessageQuote,
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
  assert.equal(message.quote, null);
});

test("normalizes quoted Zalo messages for reply-chain context", () => {
  const quote = normalizeMessageQuote({
    ownerId: "12345678901234567890_0",
    globalMsgId: 998877,
    cliMsgId: 112233,
    cliMsgType: 1,
    ts: 1_720_000_001,
    msg: "T6 này tui nghỉ nha",
    attach: "{\"kind\":\"photo\"}",
    fromD: "Long",
  });

  assert.deepEqual(quote, {
    messageId: "998877",
    senderId: "12345678901234567890",
    senderName: "Long",
    content: "T6 này tui nghỉ nha",
    messageType: "1",
    sentAtUnixMs: 1_720_000_001_000,
    attachment: "{\"kind\":\"photo\"}",
  });
});

test("historical message preserves its quote relation", () => {
  const message = normalizeHistoricalMessage({
    isSelf: false,
    data: {
      msgId: "m2",
      uidFrom: "u2",
      dName: "Tùng",
      msgType: "chat",
      ts: "1720000001000",
      content: "ông này hả?",
      quote: {
        ownerId: "u1",
        globalMsgId: "m1",
        fromD: "Long",
        msg: "Long đánh libero nha",
        ts: "1720000000000",
      },
    },
  });

  assert.ok(message);
  assert.equal(message.quote?.messageId, "m1");
  assert.equal(message.quote?.senderId, "u1");
  assert.equal(message.quote?.senderName, "Long");
  assert.equal(message.quote?.content, "Long đánh libero nha");
});

test("empty quote payload is ignored rather than creating fake context", () => {
  assert.equal(normalizeMessageQuote({}), null);
  assert.equal(normalizeMessageQuote(undefined), null);
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
      quote: null,
    },
    {
      messageId: "m2",
      senderId: "u2",
      senderName: "B",
      content: "",
      messageType: "chat",
      isFromBot: false,
      sentAtUnixMs: 1_000,
      quote: null,
    },
  ];

  const probe = buildMessageHistoryProbe("group", 100, messages, 1, "last", null);

  assert.equal(probe.returnedCount, 2);
  assert.equal(probe.isSupported, true);
  assert.equal(probe.limitationCode, null);
  assert.equal(probe.more, 1);
  assert.equal(probe.oldestMessageAtUnixMs, 1_000);
  assert.equal(probe.newestMessageAtUnixMs, 2_000);
});

test("maps the unavailable Zalo history endpoint to an explicit capability result", () => {
  assert.equal(
    isHistoryEndpointUnavailable(new Error("Request failed with status code 404")),
    true,
  );
  assert.equal(
    isHistoryEndpointUnavailable({ response: { status: 404 } }),
    true,
  );
  assert.equal(
    isHistoryEndpointUnavailable(new Error("Request failed with status code 502")),
    false,
  );

  const probe = buildUnsupportedMessageHistoryProbe(
    "group",
    500,
    "ZaloHistoryEndpointNotFound",
  );
  assert.equal(probe.isSupported, false);
  assert.equal(probe.limitationCode, "ZaloHistoryEndpointNotFound");
  assert.equal(probe.returnedCount, 0);
  assert.deepEqual(probe.messages, []);
});

test("invalid timestamps are not invented", () => {
  assert.equal(normalizeUnixMs(undefined), 0);
  assert.equal(normalizeUnixMs("not-a-time"), 0);
});
