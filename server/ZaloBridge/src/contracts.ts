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

export type BridgeMention = {
  uid: string;
  pos: number;
  len: number;
};

export type StartListenerRequest = {
  accountId: string;
  credentials: ZaloCredentials;
  groupIds: string[];
  webhookUrl: string;
  webhookKey: string;
};

export type SendGroupMessageRequest = {
  accountId: string;
  groupId: string;
  message: string;
  mentions?: BridgeMention[];
  imageUrl?: string | null;
  idempotencyKey?: string | null;
};

export type IncomingGroupMessageEvent = {
  accountId: string;
  botId: string;
  groupId: string;
  messageId: string;
  senderId: string;
  senderName: string;
  content: string;
  mentions: BridgeMention[];
  mentionedBot: boolean;
  sentAtUnixMs: number;
};
