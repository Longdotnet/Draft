export type ZaloCredentials = {
  cookie: unknown[];
  imei: string;
  userAgent: string;
  language?: string;
};

export type BridgeGroup = {
  id: string;
  name: string;
  avatarUrl: string | null;
  totalMembers: number;
};

export type BridgePollOption = {
  id: string;
  content: string;
  voteCount: number;
  voterIds: string[];
};

export type BridgePoll = {
  id: string;
  question: string;
  creatorId: string;
  options: BridgePollOption[];
  allowMultipleChoices: boolean;
  isAnonymous: boolean;
  isClosed: boolean;
  hideVotePreview: boolean;
  uniqueVoteCount: number;
  createdAtUnixMs: number;
  updatedAtUnixMs: number;
  expiredAtUnixMs: number;
};

export type BridgeMember = {
  zaloUserId: string;
  displayName: string;
  zaloName: string | null;
  avatarUrl: string | null;
};
