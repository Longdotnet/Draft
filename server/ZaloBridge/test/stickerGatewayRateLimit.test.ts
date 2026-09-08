import assert from "node:assert/strict";
import test from "node:test";
import type { SendGroupStickerRequest } from "../src/contracts.js";
import { findStickerForRequest, type StickerLookupApi } from "../src/stickerGateway.js";

const request: SendGroupStickerRequest = {
  accountId: "account-a",
  groupId: "group-a",
  credentials: { imei: "imei", userAgent: "ua", cookie: [] },
  reaction: "laugh",
  idempotencyKey: "message-1:sticker",
};

test("provider failure aborts sticker keyword fallback after the first upstream call", async () => {
  const keywords: string[] = [];
  const api: StickerLookupApi = {
    async getStickers(keyword) {
      keywords.push(keyword);
      throw { response: { status: 429, headers: { "retry-after": "120" } } };
    },
    async getStickersDetail() {
      throw new Error("must not request sticker details after provider failure");
    },
  };

  await assert.rejects(findStickerForRequest(api, request));
  assert.deepEqual(keywords, ["haha"]);
});

test("successful empty lookup can still try the next semantic keyword", async () => {
  const keywords: string[] = [];
  const api: StickerLookupApi = {
    async getStickers(keyword) {
      keywords.push(keyword);
      return keyword === "haha" ? [] : [42];
    },
    async getStickersDetail(stickerIds) {
      assert.equal(stickerIds, 42);
      return [{ id: 42, cateId: 7, type: 1 }];
    },
  };

  const sticker = await findStickerForRequest(api, request);
  assert.equal(sticker.id, 42);
  assert.deepEqual(keywords, ["haha", "cười"]);
});
