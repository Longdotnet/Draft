import assert from "node:assert/strict";
import test from "node:test";
import {
  classifyListenerClose,
  listenerReconnectAttemptLimit,
  listenerReconnectDelayMs,
} from "../src/listenerReconnectPolicy.js";

test("listener close taxonomy keeps manual, duplicate and kick failures out of transient reconnect", () => {
  assert.equal(classifyListenerClose(1000), "manual");
  assert.equal(classifyListenerClose(3000), "duplicate_connection");
  assert.equal(classifyListenerClose(3003), "kicked_or_auth");
  assert.equal(classifyListenerClose(1006), "transient");
  assert.equal(classifyListenerClose(1011), "transient");
});

test("listener reconnect policy is bounded and exponentially backs off with capped jitter", () => {
  assert.equal(listenerReconnectAttemptLimit(), 5);
  assert.equal(listenerReconnectDelayMs(1, () => 0), 1600);
  assert.equal(listenerReconnectDelayMs(1, () => 0.5), 2000);
  assert.equal(listenerReconnectDelayMs(1, () => 1), 2400);
  assert.equal(listenerReconnectDelayMs(2, () => 0.5), 5000);
  assert.equal(listenerReconnectDelayMs(3, () => 0.5), 10000);
  assert.equal(listenerReconnectDelayMs(4, () => 0.5), 20000);
  assert.equal(listenerReconnectDelayMs(5, () => 0.5), 30000);
  assert.equal(listenerReconnectDelayMs(6, () => 0.5), null);
});

test("listener reconnect policy rejects invalid attempt counters", () => {
  assert.throws(() => listenerReconnectDelayMs(0), RangeError);
  assert.throws(() => listenerReconnectDelayMs(1.5), RangeError);
});
