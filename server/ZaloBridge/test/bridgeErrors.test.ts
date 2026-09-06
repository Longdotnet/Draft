import assert from "node:assert/strict";
import test from "node:test";
import { BridgeHttpError, bridgeErrorLogFields, classifyBridgeError } from "../src/bridgeErrors.js";
import { OutboundIdempotencyConflictError } from "../src/outboundIdempotency.js";

test("preserves upstream 429 as an explicit rate-limit with safe provenance", () => {
  const error = {
    message: "Request failed with status code 429: secret provider body",
    response: { status: 429, data: { code: "RATE_LIMITED", message: "secret provider body" } },
  };

  const result = classifyBridgeError(error);

  assert.deepEqual(result, {
    status: 429,
    source: "upstream-zalo",
    kind: "rate_limited",
    retryable: true,
    publicMessage: "Zalo upstream request was rate-limited.",
  });
  assert.deepEqual(bridgeErrorLogFields(error, result), {
    source: "upstream-zalo",
    kind: "rate_limited",
    status: 429,
    retryable: true,
    upstreamCode: "RATE_LIMITED",
  });
  assert.equal(JSON.stringify(result).includes("secret provider body"), false);
});

test("maps upstream 5xx to bridge 502 while retaining upstream provenance", () => {
  const result = classifyBridgeError({ response: { status: 503 } });

  assert.equal(result.status, 502);
  assert.equal(result.source, "upstream-zalo");
  assert.equal(result.kind, "upstream_failure");
  assert.equal(result.retryable, true);
});

test("preserves intentional bridge validation errors", () => {
  const result = classifyBridgeError(new BridgeHttpError(
    400,
    "bridge-validation",
    "invalid_credentials",
    false,
    "Valid Zalo credentials are required.",
  ));

  assert.deepEqual(result, {
    status: 400,
    source: "bridge-validation",
    kind: "invalid_credentials",
    retryable: false,
    publicMessage: "Valid Zalo credentials are required.",
  });
});

test("idempotency payload conflicts are explicit non-retryable client conflicts", () => {
  const result = classifyBridgeError(new OutboundIdempotencyConflictError());

  assert.deepEqual(result, {
    status: 409,
    source: "bridge-validation",
    kind: "idempotency_conflict",
    retryable: false,
    publicMessage: "Outbound idempotency key conflicts with a previous request.",
  });
});

test("unknown failures remain sanitized bridge failures", () => {
  const result = classifyBridgeError(new Error("cookie=secret; stack=secret"));

  assert.equal(result.status, 502);
  assert.equal(result.source, "bridge-internal");
  assert.equal(result.kind, "bridge_failure");
  assert.equal(result.publicMessage.includes("secret"), false);
});
