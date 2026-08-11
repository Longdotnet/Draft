import type { BridgeMember } from "./contracts.js";
import { normalizeMemberId } from "./pollLogic.js";

export const ZALO_LARGE_AVATAR_SIZE = 240;
const FULL_AVATAR_CONCURRENCY = 8;
const LARGE_AVATAR_TIMEOUT_MS = 2_500;
const FULL_AVATAR_TIMEOUT_MS = 2_000;
const FULL_AVATAR_BUDGET_MS = 6_000;

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
  return httpAvatarUrl(candidates.full)
    ?? httpAvatarUrl(candidates.backupFull)
    ?? httpAvatarUrl(candidates.large)
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
      const value = values[nextIndex]!;
      nextIndex += 1;
      await worker(value);
    }
  }));
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
  const fullTimeoutMs = Math.max(1, timing.fullAvatarTimeoutMs ?? FULL_AVATAR_TIMEOUT_MS);
  const fullBudgetMs = Math.max(1, timing.fullAvatarBudgetMs ?? FULL_AVATAR_BUDGET_MS);
  const fullConcurrency = Math.max(1, timing.fullAvatarConcurrency ?? FULL_AVATAR_CONCURRENCY);

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

  const fullById = new Map<string, { full?: string; backupFull?: string }>();
  if (typeof api.getFullAvatar === "function") {
    const fullWork = runWithConcurrency(ids, fullConcurrency, async (memberId) => {
      try {
        const lookup = await settleWithin(api.getFullAvatar!(memberId), fullTimeoutMs);
        if (lookup.timedOut) {
          onFailure?.(
            "getFullAvatar",
            new Error(`Full avatar lookup exceeded ${fullTimeoutMs}ms`),
            { memberId },
          );
          return;
        }
        fullById.set(memberId, {
          full: httpAvatarUrl(lookup.value?.full_avatar) ?? undefined,
          backupFull: httpAvatarUrl(lookup.value?.bk_full_avatar) ?? undefined,
        });
      } catch (error) {
        onFailure?.("getFullAvatar", error, { memberId });
      }
    });

    const budget = await settleWithin(fullWork, fullBudgetMs);
    if (budget.timedOut) {
      onFailure?.(
        "getFullAvatar",
        new Error(`Full avatar enrichment exceeded ${fullBudgetMs}ms total budget`),
        { memberCount: ids.length },
      );
    }
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
