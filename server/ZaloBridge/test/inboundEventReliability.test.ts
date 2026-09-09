import assert from "node:assert/strict";
import test from "node:test";
import {
  boardEventDebounceKey,
  InboundMessageDeliveryGate,
} from "../src/inboundEventReliability.js";

test("failed inbound delivery releases the message for a later listener replay", () => {
  const gate = new InboundMessageDeliveryGate();
  const first = gate.reserve("acct-1", "msg-1");
  assert.ok(first);
  assert.equal(gate.reserve("acct-1", "msg-1"), null, "concurrent duplicate must be suppressed while delivery is pending");

  assert.equal(gate.release(first), true);
  assert.ok(gate.reserve("acct-1", "msg-1"), "failed delivery must not suppress a later replay for 24h");
});

test("confirmed delivery suppresses replay until the delivered TTL expires", () => {
  let now = 10_000;
  const gate = new InboundMessageDeliveryGate({ now: () => now, deliveredTtlMs: 1_000 });
  const reservation = gate.reserve("acct-1", "msg-1");
  assert.ok(reservation);
  assert.equal(gate.commit(reservation), true);
  assert.equal(gate.reserve("acct-1", "msg-1"), null);

  now += 999;
  assert.equal(gate.reserve("acct-1", "msg-1"), null);
  now += 1;
  assert.ok(gate.reserve("acct-1", "msg-1"));
});

test("stale reservation cannot release a newer delivery lease", () => {
  const gate = new InboundMessageDeliveryGate();
  const first = gate.reserve("acct-1", "msg-1");
  assert.ok(first);
  assert.equal(gate.release(first), true);

  const second = gate.reserve("acct-1", "msg-1");
  assert.ok(second);
  assert.equal(gate.release(first), false);
  assert.equal(gate.reserve("acct-1", "msg-1"), null, "newer pending delivery must remain reserved");
});

test("message identity is framed so punctuation cannot alias account and message fields", () => {
  const gate = new InboundMessageDeliveryGate();
  assert.ok(gate.reserve("a:b", "c"));
  assert.ok(gate.reserve("a", "b:c"));
});

test("board debounce keeps different board ids independent inside one group", () => {
  const first = boardEventDebounceKey({ accountId: "acct", groupId: "group", eventType: "update_board", boardId: "poll-1" });
  const second = boardEventDebounceKey({ accountId: "acct", groupId: "group", eventType: "update_board", boardId: "poll-2" });
  assert.notEqual(first, second);
});

test("board debounce coalesces repeated notification for the same transition only", () => {
  const first = boardEventDebounceKey({ accountId: "acct", groupId: "group", eventType: "update_board", boardId: "poll-1" });
  const repeated = boardEventDebounceKey({ accountId: "acct", groupId: "group", eventType: "update_board", boardId: "poll-1" });
  const removed = boardEventDebounceKey({ accountId: "acct", groupId: "group", eventType: "remove_board", boardId: "poll-1" });
  assert.equal(first, repeated);
  assert.notEqual(first, removed);
});
