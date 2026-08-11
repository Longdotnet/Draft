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

test("chooseBestAvatarUrl keeps the fetchable large profile url ahead of unverified full urls", () => {
  assert.equal(
    chooseBestAvatarUrl({
      current: "https://cdn.example/current.jpg",
      large: "https://cdn.example/large.jpg",
      backupFull: "https://cdn.example/backup.jpg",
      full: "https://cdn.example/full.jpg",
    }),
    "https://cdn.example/large.jpg",
  );
});

test("chooseBestAvatarUrl rejects non-http large candidate and falls back safely", () => {
  assert.equal(
    chooseBestAvatarUrl({
      current: "https://cdn.example/current.jpg",
      large: "javascript:alert(1)",
      full: "https://cdn.example/unverified-full.jpg",
    }),
    "https://cdn.example/current.jpg",
  );
});

test("enrichMemberAvatars upgrades members with one batched 240px profile request", async () => {
  const largeCalls: Array<{ ids: string[]; size?: number }> = [];
  let fullCalls = 0;
  const result = await enrichMemberAvatars(
    {
      async getAvatarUrlProfile(ids, size) {
        const list = Array.isArray(ids) ? ids : [ids];
        largeCalls.push({ ids: list, size });
        return Object.fromEntries(list.map((id) => [id, { avatar: `https://cdn.example/${id}-240.jpg` }]));
      },
      async getFullAvatar(id) {
        fullCalls += 1;
        return {
          full_avatar: `https://cdn.example/${id}-full.jpg`,
          bk_full_avatar: `https://cdn.example/${id}-backup.jpg`,
        };
      },
    },
    [member("101"), member("202")],
  );

  assert.deepEqual(result.map((item) => item.avatarUrl), [
    "https://cdn.example/101-240.jpg",
    "https://cdn.example/202-240.jpg",
  ]);
  assert.equal(largeCalls.length, 1);
  assert.deepEqual(largeCalls[0]!.ids, ["101", "202"]);
  assert.equal(largeCalls[0]!.size, ZALO_LARGE_AVATAR_SIZE);
  assert.equal(fullCalls, 0, "full-avatar urls must not replace a fetchable profile avatar");
});

test("enrichMemberAvatars keeps current avatar when large profile lookup fails", async () => {
  const current = "https://s120.example/current.jpg";
  const failures: string[] = [];
  const result = await enrichMemberAvatars(
    {
      async getAvatarUrlProfile() {
        throw new Error("large unavailable");
      },
      async getFullAvatar() {
        throw new Error("full should never be called");
      },
    },
    [member("101", current)],
    (operation) => failures.push(operation),
  );

  assert.equal(result[0]!.avatarUrl, current);
  assert.deepEqual(failures, ["getAvatarUrlProfile"]);
});

test("enrichMemberAvatars does not wait on getFullAvatar even when that api never resolves", async () => {
  const never = new Promise<{ full_avatar?: string; bk_full_avatar?: string }>(() => undefined);
  const startedAt = Date.now();
  const result = await enrichMemberAvatars(
    {
      async getAvatarUrlProfile(ids) {
        const list = Array.isArray(ids) ? ids : [ids];
        return Object.fromEntries(list.map((id) => [id, { avatar: `https://cdn.example/${id}-240.jpg` }]));
      },
      async getFullAvatar() {
        return never;
      },
    },
    [member("101")],
    undefined,
    { largeAvatarTimeoutMs: 50 },
  );

  assert.equal(result[0]!.avatarUrl, "https://cdn.example/101-240.jpg");
  assert.ok(Date.now() - startedAt < 250, "unverified full-avatar lookup must not block team-card rendering");
});
