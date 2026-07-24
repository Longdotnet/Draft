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

export type BridgeGroupMemberDirectory = {
  groupId: string;
  groupName: string;
  groupCreatedAtUnixMs: number;
  expectedMemberCount: number;
  isComplete: boolean;
  members: BridgeMember[];
};

export type BridgeBoardItem = {
  stableId: string;
  boardType: number;
  isPoll: boolean;
  pollId: string | null;
  poll: BridgePoll | null;
};

export type BridgeBoardPage = {
  groupId: string;
  page: number;
  pageSize: number;
  totalCount: number;
  items: BridgeBoardItem[];
};

export type BridgeHistoricalMessage = {
  messageId: string;
  senderId: string;
  senderName: string;
  content: string;
  messageType: string;
  isFromBot: boolean;
  sentAtUnixMs: number;
};

export type BridgeMessageHistoryProbe = {
  groupId: string;
  requestedCount: number;
  returnedCount: number;
  more: number;
  lastActionId: string | null;
  lastActionIdOther: string | null;
  oldestMessageAtUnixMs: number | null;
  newestMessageAtUnixMs: number | null;
  messages: BridgeHistoricalMessage[];
};

export type BridgeGroupRoles = {
  groupId: string;
  creatorId: string;
  adminIds: string[];
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

export type PollBoardChangedEvent = {
  accountId: string;
  groupId: string;
  eventType: "update_board" | "remove_board";
  actorId: string | null;
  boardType: string | null;
  boardId: string | null;
  occurredAtUnixMs: number;
};
