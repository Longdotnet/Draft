import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeMember } from "../src/contracts.js";
import {
  chooseBestAvatarUrl,
  enrichMemberAvatars,
  ZALO_LARGE_AVATAR_SIZE,
} from "../src/avatarResolver.js";

function member(id: string, avatarUrl = "https://s120.example/avatar.jpg"): BridgeMember {
  return {
    zaloUserId: id,
    displayName: `Player ${id}`,
    zaloName: null,
    avatarUrl,
  };
}

test("chooseBestAvatarUrl prefers full avatar over backup, large and current", () => {
  assert.equal(
    chooseBestAvatarUrl({
      current: "https://cdn.example/current.jpg",
      large: "https://cdn.example/large.jpg",
      backupFull: "https://cdn.example/backup.jpg",
      full: "https://cdn.example/full.jpg",
    }),
    "https://cdn.example/full.jpg",
  );
});

test("chooseBestAvatarUrl rejects non-http candidates and falls back safely", () => {
  assert.equal(
    chooseBestAvatarUrl({
      current: "https://cdn.example/current.jpg",
      large: "javascript:alert(1)",
      full: "file:///tmp/avatar.jpg",
    }),
    "https://cdn.example/current.jpg",
  );
});

test("enrichMemberAvatars uses full avatar and batches the 240px fallback request", async () => {
  const largeCalls: Array<{ ids: string[]; size?: number }> = [];
  const fullCalls: string[] = [];
  const result = await enrichMemberAvatars(
    {
      async getAvatarUrlProfile(ids, size) {
        const list = Array.isArray(ids) ? ids : [ids];
        largeCalls.push({ ids: list, size });
        return Object.fromEntries(list.map((id) => [id, { avatar: `https://cdn.example/${id}-240.jpg` }]));
      },
      async getFullAvatar(id) {
        fullCalls.push(id);
        return {
          full_avatar: `https://cdn.example/${id}-full.jpg`,
          bk_full_avatar: `https://cdn.example/${id}-backup.jpg`,
        };
      },
    },
    [member("101"), member("202")],
  );

  assert.deepEqual(result.map((item) => item.avatarUrl), [
    "https://cdn.example/101-full.jpg",
    "https://cdn.example/202-full.jpg",
  ]);
  assert.equal(largeCalls.length, 1);
  assert.deepEqual(largeCalls[0].ids, ["101", "202"]);
  assert.equal(largeCalls[0].size, ZALO_LARGE_AVATAR_SIZE);
  assert.deepEqual(fullCalls.sort(), ["101", "202"]);
});

test("enrichMemberAvatars falls back to 240px profile avatar when full avatar fails", async () => {
  const failures: string[] = [];
  const result = await enrichMemberAvatars(
    {
      async getAvatarUrlProfile(ids) {
        const list = Array.isArray(ids) ? ids : [ids];
        return Object.fromEntries(list.map((id) => [id, { avatar: `https://cdn.example/${id}-240.jpg` }]));
      },
      async getFullAvatar() {
        throw new Error("full avatar unavailable");
      },
    },
    [member("101")],
    (operation) => failures.push(operation),
  );

  assert.equal(result[0].avatarUrl, "https://cdn.example/101-240.jpg");
  assert.deepEqual(failures, ["getFullAvatar"]);
});

test("enrichMemberAvatars keeps current avatar when all enrichment APIs fail", async () => {
  const current = "https://s120.example/current.jpg";
  const result = await enrichMemberAvatars(
    {
      async getAvatarUrlProfile() {
        throw new Error("large unavailable");
      },
      async getFullAvatar() {
        throw new Error("full unavailable");
      },
    },
    [member("101", current)],
  );

  assert.equal(result[0].avatarUrl, current);
});
