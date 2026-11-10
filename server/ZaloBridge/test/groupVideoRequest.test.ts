import assert from "node:assert/strict";
import test from "node:test";
import { normalizeGroupVideoRequest } from "../src/groupVideoRequest.js";

test("normalizes URL video request and trims idempotency scope", () => {
  const result = normalizeGroupVideoRequest({
    accountId: " account-1 ",
    groupId: " group-1 ",
    videoUrl: " https://cdn.example/video.mp4 ",
    thumbnailUrl: "https://cdn.example/thumb.jpg",
    durationMs: 20_000,
    width: 720,
    height: 1280,
    idempotencyKey: " key-1 ",
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  assert.equal(result.value.accountId, "account-1");
  assert.equal(result.value.groupId, "group-1");
  assert.equal(result.value.idempotencyKey, "key-1");
  assert.equal(result.value.outbound.videoUrl, "https://cdn.example/video.mp4");
});

test("requires exactly one video transport", () => {
  assert.deepEqual(normalizeGroupVideoRequest({ accountId: "a", groupId: "g" }), {
    ok: false,
    error: "provide exactly one of videoBase64 or videoUrl",
  });
  const both = normalizeGroupVideoRequest({
    accountId: "a", groupId: "g", videoBase64: "Zg==", videoUrl: "https://cdn.example/v.mp4",
    thumbnailBase64: "Zg==", thumbnailUrl: "https://cdn.example/t.jpg",
  });
  assert.equal(both.ok, false);
});

test("requires matching thumbnail transport", () => {
  const url = normalizeGroupVideoRequest({ accountId: "a", groupId: "g", videoUrl: "https://cdn.example/v.mp4" });
  assert.deepEqual(url, { ok: false, error: "thumbnailUrl is required when videoUrl is used" });
  const inline = normalizeGroupVideoRequest({ accountId: "a", groupId: "g", videoBase64: "Zg==" });
  assert.deepEqual(inline, { ok: false, error: "thumbnailBase64 is required when videoBase64 is used" });
});

test("rejects non-http URL transport", () => {
  const result = normalizeGroupVideoRequest({
    accountId: "a", groupId: "g", videoUrl: "file:///tmp/video.mp4", thumbnailUrl: "https://cdn.example/t.jpg",
  });
  assert.deepEqual(result, { ok: false, error: "videoUrl and thumbnailUrl must be absolute http(s) URLs" });
});

test("rejects invalid positive integer metadata", () => {
  for (const [field, value] of [["durationMs", 0], ["width", -1], ["height", 1.5]] as const) {
    const result = normalizeGroupVideoRequest({
      accountId: "a",
      groupId: "g",
      videoBase64: "Zg==",
      thumbnailBase64: "Zg==",
      [field]: value,
    });
    assert.deepEqual(result, { ok: false, error: `${field} must be a positive integer when provided` });
  }
});

test("accepts inline transport with optional metadata omitted", () => {
  const result = normalizeGroupVideoRequest({
    accountId: "a", groupId: "g", videoBase64: " Zg== ", thumbnailBase64: " Zg== ",
  });
  assert.equal(result.ok, true);
  if (!result.ok) return;
  assert.equal(result.value.outbound.videoBase64, "Zg==");
  assert.equal(result.value.outbound.durationMs, null);
});
