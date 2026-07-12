import type { BridgeMember, BridgePoll, BridgePollOption } from "./contracts.js";

export function normalizeId(value: unknown): string {
  if (typeof value === "bigint") return value.toString();
  if (typeof value === "number" && Number.isFinite(value)) return Math.trunc(value).toString();
  return String(value ?? "").trim();
}

export function normalizeMemberId(value: unknown): string {
  return normalizeId(value).replace(/_0$/, "");
}

export function uniqueVoterIds(options: BridgePollOption[], selectedOptionIds: string[]): string[] {
  const selected = new Set(selectedOptionIds.map(normalizeId));
  const voterIds = new Set<string>();

  for (const option of options) {
    if (!selected.has(normalizeId(option.id))) continue;
    for (const voterId of option.voterIds) {
      const normalized = normalizeMemberId(voterId);
      if (normalized) voterIds.add(normalized);
    }
  }

  return [...voterIds];
}

export function normalizePoll(raw: Record<string, unknown>): BridgePoll {
  const rawOptions = Array.isArray(raw.options) ? raw.options : [];
  const options = rawOptions.map((value) => {
    const option = value as Record<string, unknown>;
    return {
      id: normalizeId(option.option_id),
      content: String(option.content ?? ""),
      voteCount: Number(option.votes ?? 0),
      voterIds: Array.isArray(option.voters)
        ? option.voters.map(normalizeMemberId).filter(Boolean)
        : [],
    } satisfies BridgePollOption;
  });

  return {
    id: normalizeId(raw.poll_id),
    question: String(raw.question ?? ""),
    creatorId: normalizeMemberId(raw.creator),
    options,
    allowMultipleChoices: Boolean(raw.allow_multi_choices),
    isAnonymous: Boolean(raw.is_anonymous),
    isClosed: Boolean(raw.closed),
    hideVotePreview: Boolean(raw.is_hide_vote_preview),
    uniqueVoteCount: new Set(options.flatMap((option) => option.voterIds)).size,
    createdAtUnixMs: Number(raw.created_time ?? 0),
    updatedAtUnixMs: Number(raw.updated_time ?? 0),
    expiredAtUnixMs: Number(raw.expired_time ?? 0),
  };
}

export function normalizeMember(profileKey: string, raw: Record<string, unknown>): BridgeMember {
  const zaloUserId = normalizeMemberId(raw.id || raw.globalId || profileKey);
  const displayName = String(raw.displayName || raw.zaloName || `Zalo ${zaloUserId}`);
  return {
    zaloUserId,
    displayName,
    zaloName: raw.zaloName ? String(raw.zaloName) : null,
    avatarUrl: raw.avatar ? String(raw.avatar) : null,
  };
}
