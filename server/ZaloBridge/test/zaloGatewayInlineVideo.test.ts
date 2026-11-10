import assert from "node:assert/strict";
import test from "node:test";

process.env.ZALO_BRIDGE_MOCK = "true";

const { decodeInlineVideo, decodeInlineVideoThumbnail } = await import("../src/zaloGateway.js");

test("decodeInlineVideo accepts a bounded mp4 payload", () => {
  const payload = Buffer.from("fake-mp4").toString("base64");
  const decoded = decodeInlineVideo({
    accountId: "a",
    groupId: "g",
    videoBase64: payload,
    videoContentType: "video/mp4",
    videoFileName: "morning story!.mp4",
  });

  assert.ok(decoded);
  assert.equal(decoded.filename, "morning-story-.mp4");
  assert.equal(decoded.data.toString(), "fake-mp4");
});

test("decodeInlineVideo rejects non-mp4 content types", () => {
  assert.throws(() => decodeInlineVideo({
    accountId: "a",
    groupId: "g",
    videoBase64: Buffer.from("x").toString("base64"),
    videoContentType: "video/webm",
  }), /video\/mp4/);
});

test("decodeInlineVideo rejects malformed base64", () => {
  assert.throws(() => decodeInlineVideo({
    accountId: "a",
    groupId: "g",
    videoBase64: "***",
  }), /valid base64/);
});

test("decodeInlineVideoThumbnail creates provider image metadata", () => {
  const decoded = decodeInlineVideoThumbnail({
    accountId: "a",
    groupId: "g",
    thumbnailBase64: Buffer.from("fake-jpeg").toString("base64"),
    thumbnailContentType: "image/jpeg",
    width: 720,
    height: 1280,
  });
  assert.ok(decoded);
  assert.equal(decoded.filename, "trend-music-thumb.jpg");
  assert.equal(decoded.metadata.width, 720);
  assert.equal(decoded.metadata.height, 1280);
});
