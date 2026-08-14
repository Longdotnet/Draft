import assert from "node:assert/strict";
import test from "node:test";
import { resolveBotAddressing } from "../src/messageAddressingLogic.js";

test("direct reply to bot is treated as addressed without textual mention", () => {
  const resolved = resolveBotAddressing(
    [],
    {
      messageId: "bot-message",
      senderId: "bot-123_0",
      senderName: "Bot",
      content: "Bạn muốn trận nào?",
      messageType: "chat",
      sentAtUnixMs: 1_720_000_000_000,
      attachment: null,
    },
    "bot-123",
  );

  assert.equal(resolved.mentionedBot, true);
  assert.equal(resolved.addressedByReply, true);
  assert.equal(resolved.mentions.length, 1);
  assert.equal(resolved.mentions[0]?.uid, "bot-123");
});

test("normal group chatter stays unaddressed", () => {
  const resolved = resolveBotAddressing([], null, "bot-123");

  assert.equal(resolved.mentionedBot, false);
  assert.equal(resolved.addressedByReply, false);
  assert.deepEqual(resolved.mentions, []);
});

test("real bot mention is preserved without adding duplicate synthetic mention", () => {
  const mentions = [{ uid: "bot-123", pos: 4, len: 8 }];
  const resolved = resolveBotAddressing(mentions, null, "bot-123");

  assert.equal(resolved.mentionedBot, true);
  assert.equal(resolved.addressedByReply, false);
  assert.deepEqual(resolved.mentions, mentions);
});
