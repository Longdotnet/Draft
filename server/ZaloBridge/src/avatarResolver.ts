import type { BridgeMember } from "./contracts.js";
import { normalizeMemberId } from "./pollLogic.js";

export const ZALO_LARGE_AVATAR_SIZE = 240;
const FULL_AVATAR_CONCURRENCY = 4;

export type AvatarProfileApi = {
  getAvatarUrlProfile?: (
    friendIds: string | string[],
    avatarSize?: number,
  ) => Promise<Record<string, { avatar?: string }>>;
  getFullAvatar?: (friendId: string) => Promise<{
    full_avatar?: string;
    bk_full_avatar?: string;
  }>;
};

export type AvatarResolutionFailure = (
  operation: "getAvatarUrlProfile" | "getFullAvatar",
  error: unknown,
  context: { memberId?: string; memberCount?: number },
) => void;

export type AvatarCandidateSet = {
  current?: unknown;
  large?: unknown;
  full?: unknown;
  backupFull?: unknown;
};

function httpAvatarUrl(value: unknown): string | null {
  const text = String(value ?? "").trim();
  if (!text) return null;
  try {
    const parsed = new URL(text);
    return parsed.protocol === "https:" || parsed.protocol === "http:" ? text : null;
  } catch {
    return null;
  }
}

export function chooseBestAvatarUrl(candidates: AvatarCandidateSet): string | null {
  return httpAvatarUrl(candidates.full)
    ?? httpAvatarUrl(candidates.backupFull)
    ?? httpAvatarUrl(candidates.large)
    ?? httpAvatarUrl(candidates.current);
}

async function runWithConcurrency<T>(
  values: T[],
  concurrency: number,
  worker: (value: T) => Promise<void>,
): Promise<void> {
  if (values.length === 0) return;
  let nextIndex = 0;
  const workerCount = Math.min(Math.max(1, concurrency), values.length);
  await Promise.all(Array.from({ length: workerCount }, async () => {
    while (nextIndex < values.length) {
      const value = values[nextIndex];
      nextIndex += 1;
      await worker(value);
    }
  }));
}

export async function enrichMemberAvatars(
  api: AvatarProfileApi,
  members: BridgeMember[],
  onFailure?: AvatarResolutionFailure,
): Promise<BridgeMember[]> {
  const ids = [...new Set(
    members
      .map((member) => normalizeMemberId(member.zaloUserId))
      .filter(Boolean),
  )];
  if (ids.length === 0) return members;

  const largeById = new Map<string, string>();
  if (typeof api.getAvatarUrlProfile === "function") {
    try {
      const response = await api.getAvatarUrlProfile(ids, ZALO_LARGE_AVATAR_SIZE);
      for (const [rawId, profile] of Object.entries(response ?? {})) {
        const memberId = normalizeMemberId(rawId);
        const avatar = httpAvatarUrl(profile?.avatar);
        if (memberId && avatar) largeById.set(memberId, avatar);
      }
    } catch (error) {
      onFailure?.("getAvatarUrlProfile", error, { memberCount: ids.length });
    }
  }

  const fullById = new Map<string, { full?: string; backupFull?: string }>();
  if (typeof api.getFullAvatar === "function") {
    await runWithConcurrency(ids, FULL_AVATAR_CONCURRENCY, async (memberId) => {
      try {
        const response = await api.getFullAvatar!(memberId);
        fullById.set(memberId, {
          full: httpAvatarUrl(response?.full_avatar) ?? undefined,
          backupFull: httpAvatarUrl(response?.bk_full_avatar) ?? undefined,
        });
      } catch (error) {
        onFailure?.("getFullAvatar", error, { memberId });
      }
    });
  }

  return members.map((member) => {
    const memberId = normalizeMemberId(member.zaloUserId);
    const full = fullById.get(memberId);
    const avatarUrl = chooseBestAvatarUrl({
      current: member.avatarUrl,
      large: largeById.get(memberId),
      full: full?.full,
      backupFull: full?.backupFull,
    });
    return avatarUrl === member.avatarUrl ? member : { ...member, avatarUrl };
  });
}
