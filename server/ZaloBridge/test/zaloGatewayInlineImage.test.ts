import assert from "node:assert/strict";
import test from "node:test";
import { decodeInlineImage } from "../src/zaloGateway.js";
import type { SendGroupMessageRequest } from "../src/contracts.js";

function request(overrides: Partial<SendGroupMessageRequest>): SendGroupMessageRequest {
  return {
    accountId: "account-1",
    groupId: "group-1",
    message: "team result",
    ...overrides,
  };
}

test("inline image decodes exact bytes and preserves delivery metadata", () => {
  const source = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3]);

  const attachment = decodeInlineImage(request({
    imageBase64: source.toString("base64"),
    imageContentType: "image/png",
    imageFileName: "court-index.png",
  }));

  assert.ok(attachment);
  assert.deepEqual(attachment.data, source);
  assert.equal(attachment.filename, "court-index.png");
  assert.equal(attachment.metadata.totalSize, source.length);
});

test("inline image derives extension from content type and sanitizes filename", () => {
  const attachment = decodeInlineImage(request({
    imageBase64: Buffer.from([1, 2, 3]).toString("base64"),
    imageContentType: "image/jpeg",
    imageFileName: "court index?.png",
  }));

  assert.ok(attachment);
  assert.equal(attachment.filename, "court-index-.jpg");
  assert.equal(attachment.metadata.totalSize, 3);
});

test("inline image rejects invalid base64", () => {
  assert.throws(
    () => decodeInlineImage(request({ imageBase64: "not base64!!!" })),
    /Inline image is not valid base64/,
  );
});

test("inline image rejects decoded payloads larger than 10 MiB", () => {
  const oversized = Buffer.alloc(10 * 1024 * 1024 + 1, 0x5a).toString("base64");

  assert.throws(
    () => decodeInlineImage(request({ imageBase64: oversized })),
    /Inline image exceeds 10 MB/,
  );
});
