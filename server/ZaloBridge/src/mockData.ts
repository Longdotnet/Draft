import type {
  BridgeGroup,
  BridgeHistoricalMessage,
  BridgeMember,
  BridgePoll,
  ZaloCredentials,
} from "./contracts.js";

export const mockCredentials: ZaloCredentials = {
  cookie: [{ key: "mock", value: "mock" }],
  imei: "mock-imei",
  userAgent: "VolleyDraftMock/1.0",
  language: "vi",
};

export const mockGroups: BridgeGroup[] = [
  { id: "group-ute", name: "Bóng chuyền UTE", avatarUrl: null, totalMembers: 24 },
];

const voters = Array.from({ length: 18 }, (_, index) => `zalo-${index + 1}`);
const memberIds = Array.from({ length: 24 }, (_, index) => `zalo-${index + 1}`);

export const mockPolls: BridgePoll[] = [
  {
    id: "poll-weekly",
    question: "Vote sân UTE tuần này 18h-21h15",
    creatorId: "zalo-1",
    allowMultipleChoices: true,
    isAnonymous: false,
    isClosed: false,
    hideVotePreview: false,
    uniqueVoteCount: 18,
    createdAtUnixMs: Date.now() - 86_400_000,
    updatedAtUnixMs: Date.now(),
    expiredAtUnixMs: 0,
    options: [
      { id: "opt-t4", content: "Thứ 4 8/7", voteCount: 12, voterIds: voters.slice(0, 12) },
      { id: "opt-t6", content: "Thứ 6 10/7", voteCount: 12, voterIds: voters.slice(6, 18) },
      { id: "opt-cn", content: "Chủ nhật 12/7", voteCount: 9, voterIds: voters.slice(0, 18).filter((_, index) => index % 2 === 0) },
    ],
  },
];

export const mockMembers: BridgeMember[] = memberIds.map((id, index) => ({
  zaloUserId: id,
  displayName: `Người chơi ${index + 1}`,
  zaloName: `Player ${index + 1}`,
  avatarUrl: `https://api.dicebear.com/9.x/initials/svg?seed=Player-${index + 1}`,
}));

export const mockHistoricalMessages: BridgeHistoricalMessage[] = [
  {
    messageId: "history-1",
    senderId: "zalo-1",
    senderName: "Người chơi 1",
    content: "Mình tham gia thứ 4 nhé",
    messageType: "chat",
    isFromBot: false,
    sentAtUnixMs: Date.now() - 30 * 86_400_000,
  },
  {
    messageId: "history-2",
    senderId: "zalo-7",
    senderName: "Người chơi 7",
    content: "Sân tuần này ở đâu vậy?",
    messageType: "chat",
    isFromBot: false,
    sentAtUnixMs: Date.now() - 7 * 86_400_000,
  },
];
