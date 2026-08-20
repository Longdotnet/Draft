import type { StickerReaction } from "./contracts.js";

const keywords: Record<StickerReaction, readonly string[]> = {
  laugh: ["haha", "cười", "lol"],
  cheer: ["chúc mừng", "yay", "vui"],
  love: ["thương", "love", "yêu"],
  wow: ["wow", "ngạc nhiên", "trời ơi"],
  sad: ["buồn", "khóc", "sad"],
  sorry: ["xin lỗi", "sorry", "tha lỗi"],
  facepalm: ["bó tay", "chịu", "facepalm"],
  good_job: ["giỏi", "tốt lắm", "good job"],
  bye: ["tạm biệt", "bye", "chào"],
};

export const stickerReactions = Object.freeze(Object.keys(keywords) as StickerReaction[]);

export function isStickerReaction(value: unknown): value is StickerReaction {
  return typeof value === "string" && stickerReactions.includes(value as StickerReaction);
}

export function stickerKeywordsForReaction(reaction: StickerReaction): readonly string[] {
  return keywords[reaction];
}
