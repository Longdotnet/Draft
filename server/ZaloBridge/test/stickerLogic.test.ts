import assert from "node:assert/strict";
import test from "node:test";
import { isStickerReaction, stickerKeywordsForReaction, stickerReactions } from "../src/stickerLogic.js";

test("sticker reaction catalog exposes only supported native reaction keys", () => {
  assert.deepEqual(stickerReactions, [
    "laugh",
    "cheer",
    "love",
    "wow",
    "sad",
    "sorry",
    "facepalm",
    "good_job",
    "bye",
  ]);
  for (const reaction of stickerReactions) {
    assert.equal(isStickerReaction(reaction), true);
    assert.ok(stickerKeywordsForReaction(reaction).length >= 2);
  }
});

test("invalid sticker reaction is rejected", () => {
  assert.equal(isStickerReaction("gif"), false);
  assert.equal(isStickerReaction("angry"), false);
  assert.equal(isStickerReaction(null), false);
});
