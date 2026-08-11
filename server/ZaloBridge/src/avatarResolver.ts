import type { BridgeMember } from "./contracts.js";
import { normalizeMemberId } from "./pollLogic.js";

export const ZALO_LARGE_AVATAR_SIZE = 240;
const LARGE_AVATAR_TIMEOUT_MS = 2_500;

export type AvatarProfileApi = {
  getAvatarUrlProfile?: (
    friendIds: string | string[],
    avatarSize?: number,
  ) => Promise<Record<string, { avatar?: string }>>;
  // Kept in the interface because zca-js exposes it, but the bridge deliberately does
  // not persist URLs from getFullAvatar. Production showed those URLs can be unusable
  // from the Draft API container, which replaced a fetchable thumbnail with a broken
  // source and made Poster 01 fall back to captain initials.
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

export type AvatarEnrichmentTiming = {
  largeAvatarTimeoutMs?: number;
  fullAvatarTimeoutMs?: number;
  fullAvatarBudgetMs?: number;
  fullAvatarConcurrency?: number;
};

type TimedResult<T> =
  | { timedOut: false; value: T }
  | { timedOut: true };

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
  // Only URLs returned by the normal profile endpoint are persisted into Draft.
  // `full_avatar`/`bk_full_avatar` may be session-bound or otherwise unavailable to
  // the backend HTTP client, so preferring them can turn a valid avatar into null.
  return httpAvatarUrl(candidates.large)
    ?? httpAvatarUrl(candidates.current);
}

async function settleWithin<T>(promise: Promise<T>, timeoutMs: number): Promise<TimedResult<T>> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      promise.then((value) => ({ timedOut: false, value }) as const),
      new Promise<TimedResult<T>>((resolve) => {
        timer = setTimeout(() => resolve({ timedOut: true }), Math.max(1, timeoutMs));
      }),
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

export async function enrichMemberAvatars(
  api: AvatarProfileApi,
  members: BridgeMember[],
  onFailure?: AvatarResolutionFailure,
  timing: AvatarEnrichmentTiming = {},
): Promise<BridgeMember[]> {
  const ids = [...new Set(
    members
      .map((member) => normalizeMemberId(member.zaloUserId))
      .filter(Boolean),
  )];
  if (ids.length === 0) return members;

  const largeTimeoutMs = Math.max(1, timing.largeAvatarTimeoutMs ?? LARGE_AVATAR_TIMEOUT_MS);
  const largeById = new Map<string, string>();

  if (typeof api.getAvatarUrlProfile === "function") {
    try {
      const lookup = await settleWithin(
        api.getAvatarUrlProfile(ids, ZALO_LARGE_AVATAR_SIZE),
        largeTimeoutMs,
      );
      if (lookup.timedOut) {
        onFailure?.(
          "getAvatarUrlProfile",
          new Error(`Large avatar lookup exceeded ${largeTimeoutMs}ms`),
          { memberCount: ids.length },
        );
      } else {
        for (const [rawId, profile] of Object.entries(lookup.value ?? {})) {
          const memberId = normalizeMemberId(rawId);
          const avatar = httpAvatarUrl(profile?.avatar);
          if (memberId && avatar) largeById.set(memberId, avatar);
        }
      }
    } catch (error) {
      onFailure?.("getAvatarUrlProfile", error, { memberCount: ids.length });
    }
  }

  return members.map((member) => {
    const memberId = normalizeMemberId(member.zaloUserId);
    const avatarUrl = chooseBestAvatarUrl({
      current: member.avatarUrl,
      large: largeById.get(memberId),
    });
    return avatarUrl === member.avatarUrl ? member : { ...member, avatarUrl };
  });
}
