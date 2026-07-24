import { createHash, randomUUID } from "node:crypto";
// The published 2.1.0 package points TypeScript at an invalid declaration entrypoint.
// Keep all untyped interaction isolated in this adapter while runtime exports remain valid.
import * as ZaloRuntime from "zca-js";
import type {
  BridgeBoardPage,
  BridgeGroup,
  BridgeGroupMemberDirectory,
  BridgeGroupRoles,
  BridgeHistoricalMessage,
  BridgeMember,
  BridgeMessageHistoryProbe,
  BridgeMention,
  BridgePoll,
  IncomingGroupMessageEvent,
  PollBoardChangedEvent,
  SendGroupMessageRequest,
  StartListenerRequest,
  ZaloCredentials,
} from "./contracts.js";
import {
  mockCredentials,
  mockGroups,
  mockHistoricalMessages,
  mockMembers,
  mockPolls,
} from "./mockData.js";
import { normalizeId, normalizeMember, normalizeMemberId, normalizePoll } from "./pollLogic.js";
import { buildMessageHistoryProbe, normalizeHistoricalMessage, normalizeUnixMs } from "./messageHistoryLogic.js";

type QrLoginStatus = "waiting_qr" | "waiting_scan" | "waiting_confirm" | "completed" | "expired" | "declined" | "failed";

type MinimalZaloApi = {
  getOwnId(): string;
  fetchAccountInfo(): Promise<{ displayName?: string; zaloName?: string; avatar?: string }>;
  getAllGroups(): Promise<{ gridVerMap?: Record<string, string> }>;
  getGroupInfo(ids: string[]): Promise<{ gridInfoMap?: Record<string, Record<string, unknown>> }>;
  getListBoard(options: { page: number; count: number }, groupId: string): Promise<{
    items?: Array<{ boardType: number; data: Record<string, unknown> }>;
    count?: number;
  }>;
  getPollDetail(pollId: string): Promise<Record<string, unknown>>;
  getGroupMembersInfo(ids: string[]): Promise<{ profiles?: Record<string, Record<string, unknown>> }>;
  getGroupChatHistory(groupId: string, count?: number): Promise<{
    lastActionId?: string;
    lastActionIdOther?: string;
    more?: number;
    groupMsgs?: Array<{
      isSelf?: boolean;
      data?: {
        actionId?: string;
        msgId?: string;
        cliMsgId?: string;
        msgType?: string;
        uidFrom?: string;
        dName?: string;
        ts?: string | number;
        content?: unknown;
      };
    }>;
  }>;
  listener: {
    on(event: "message", callback: (message: MinimalMessage) => unknown): void;
    on(event: "group_event", callback: (event: MinimalGroupEvent) => unknown): void;
    on(event: "error", callback: (error: unknown) => unknown): void;
    on(event: "closed", callback: (code: number, reason: string) => unknown): void;
    start(options?: { retryOnClose?: boolean }): void;
    stop(): void;
  };
  sendMessage(
    message: {
      msg: string;
      mentions?: BridgeMention[];
      attachments?: Array<string | { data: Buffer; filename: `${string}.${string}`; metadata: { totalSize: number } }>;
    },
    threadId: string,
    type: number,
  ): Promise<unknown>;
};

type MinimalMessage = {
  type: number;
  threadId: string;
  isSelf: boolean;
  data: {
    msgId?: string;
    cliMsgId?: string;
    uidFrom?: string;
    dName?: string;
    ts?: string;
    content?: unknown;
    mentions?: Array<{ uid?: string; pos?: number; len?: number }>;
  };
};

type MinimalGroupEvent = {
  type: string;
  threadId: string;
  isSelf: boolean;
  data?: {
    sourceId?: string;
    creatorId?: string;
    time?: string | number;
    groupTopic?: Record<string, unknown> | null;
    extraData?: Record<string, unknown> | null;
  };
};

type MinimalQrEvent = {
  type: number;
  data: Record<string, unknown> | null;
};

const outgoingIdempotency = new Map<string, { expiresAt: number; result: Promise<{ sent: boolean; mock: boolean }> }>();
const pendingBoardEvents = new Map<string, ReturnType<typeof setTimeout>>();

type MinimalZaloClient = {
  login(credentials: ZaloCredentials): Promise<MinimalZaloApi>;
  loginQR(options: Record<string, unknown>, callback: (event: MinimalQrEvent) => void): Promise<MinimalZaloApi>;
};

const runtime = ZaloRuntime as unknown as {
  Zalo: new (options: Record<string, unknown>) => MinimalZaloClient;
  BoardType: { Poll: number };
  LoginQRCallbackEventType: {
    QRCodeGenerated: number;
    QRCodeExpired: number;
    QRCodeScanned: number;
    QRCodeDeclined: number;
    GotLoginInfo: number;
  };
  ThreadType: { Group: number };
};
const { Zalo, BoardType, LoginQRCallbackEventType, ThreadType } = runtime;

export type QrLoginSession = {
  id: string;
  status: QrLoginStatus;
  qrImageBase64: string | null;
  displayName: string | null;
  avatarUrl: string | null;
  accountZaloId: string | null;
  credentials: ZaloCredentials | null;
  error: string | null;
  expiresAt: string;
};

const qrSessions = new Map<string, QrLoginSession>();
const apiCache = new Map<string, { api: MinimalZaloApi; lastUsed: number }>();
const activeListeners = new Map<string, ActiveListener>();
const processedMessageIds = new Map<string, number>();
const mockMode = process.env.ZALO_BRIDGE_MOCK === "true";

type ActiveListener = {
  api: MinimalZaloApi | null;
  credentialFingerprint: string;
  groupIds: Set<string>;
  webhookUrl: string;
  webhookKey: string;
  botId: string;
  startedAt: number;
};

function publicError(error: unknown): string {
  return error instanceof Error ? error.message : "Zalo request failed";
}

function fingerprint(credentials: ZaloCredentials): string {
  return createHash("sha256").update(JSON.stringify(credentials)).digest("hex");
}

async function getApi(credentials: ZaloCredentials): Promise<MinimalZaloApi> {
  const key = fingerprint(credentials);
  const cached = apiCache.get(key);
  if (cached && Date.now() - cached.lastUsed < 10 * 60_000) {
    cached.lastUsed = Date.now();
    return cached.api;
  }

  const zalo = new Zalo({ logging: false, checkUpdate: false });
  const api = await zalo.login(credentials);
  apiCache.set(key, { api, lastUsed: Date.now() });
  return api;
}

function normalizeMentions(value: MinimalMessage["data"]["mentions"]): BridgeMention[] {
  if (!Array.isArray(value)) return [];
  return value
    .map((mention) => ({
      uid: normalizeMemberId(String(mention.uid ?? "")),
      pos: Number(mention.pos ?? 0),
      len: Number(mention.len ?? 0),
    }))
    .filter((mention) => mention.uid.length > 0 && mention.pos >= 0 && mention.len > 0);
}

function rememberMessage(accountId: string, messageId: string): boolean {
  const now = Date.now();
  for (const [key, seenAt] of processedMessageIds) {
    if (now - seenAt > 24 * 60 * 60_000) processedMessageIds.delete(key);
  }
  const key = `${accountId}:${messageId}`;
  if (processedMessageIds.has(key)) return false;
  processedMessageIds.set(key, now);
  return true;
}

async function postWebhook(
  listener: ActiveListener,
  event: IncomingGroupMessageEvent | PollBoardChangedEvent,
  webhookUrl = listener.webhookUrl,
) {
  let lastError: unknown;
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    try {
      const response = await fetch(webhookUrl, {
        method: "POST",
        headers: {
          "content-type": "application/json",
          "x-zalo-bridge-key": listener.webhookKey,
        },
        body: JSON.stringify(event),
        signal: AbortSignal.timeout(45_000),
      });
      if (response.ok) return;
      const body = await response.text();
      throw new Error(`VolleyDraft webhook returned ${response.status}: ${body.slice(0, 300)}`);
    } catch (error) {
      lastError = error;
      if (attempt < 3) await new Promise((resolve) => setTimeout(resolve, attempt * 1000));
    }
  }
  throw lastError;
}

function pollWebhookUrl(messageWebhookUrl: string): string {
  const parsed = new URL(messageWebhookUrl);
  parsed.pathname = /\/events\/?$/.test(parsed.pathname)
    ? parsed.pathname.replace(/\/events\/?$/, "/poll-events")
    : `${parsed.pathname.replace(/\/$/, "")}/poll-events`;
  return parsed.toString();
}

function readTopicValue(topic: Record<string, unknown> | null | undefined, ...keys: string[]): string | null {
  if (!topic) return null;
  for (const key of keys) {
    const value = topic[key];
    if (typeof value === "string" || typeof value === "number") return String(value);
  }
  return null;
}

function handleBoardEvent(accountId: string, listener: ActiveListener, event: MinimalGroupEvent) {
  if (event.type !== "update_board" && event.type !== "remove_board") return;
  const eventType: PollBoardChangedEvent["eventType"] = event.type;
  const groupId = normalizeId(event.threadId);
  if (!groupId || !listener.groupIds.has(groupId)) return;
  const key = `${accountId}:${groupId}`;
  const pending = pendingBoardEvents.get(key);
  if (pending) clearTimeout(pending);
  pendingBoardEvents.set(key, setTimeout(() => {
    pendingBoardEvents.delete(key);
    const topic = event.data?.groupTopic;
    const rawTimestamp = Number(event.data?.time ?? Date.now());
    const occurredAtUnixMs = Number.isFinite(rawTimestamp)
      ? rawTimestamp < 10_000_000_000 ? rawTimestamp * 1000 : rawTimestamp
      : Date.now();
    void postWebhook(listener, {
      accountId,
      groupId,
      eventType,
      actorId: normalizeMemberId(String(event.data?.sourceId ?? event.data?.creatorId ?? "")) || null,
      boardType: readTopicValue(topic, "type", "topicType", "boardType"),
      boardId: readTopicValue(topic, "topicId", "id", "pollId") ?? readTopicValue(event.data?.extraData, "topicId", "id", "pollId"),
      occurredAtUnixMs,
    }, pollWebhookUrl(listener.webhookUrl)).catch((error) =>
      console.error(`[Zalo listener ${accountId}] Failed to forward poll board event:`, error),
    );
  }, 1_500));
}

async function handleIncomingMessage(accountId: string, listener: ActiveListener, message: MinimalMessage) {
  if (message.type !== ThreadType.Group || message.isSelf || typeof message.data.content !== "string") return;
  const groupId = normalizeId(message.threadId);
  if (!listener.groupIds.has(groupId)) return;

  const messageId = normalizeId(String(message.data.msgId || message.data.cliMsgId || ""));
  if (!messageId || !rememberMessage(accountId, messageId)) return;
  const mentions = normalizeMentions(message.data.mentions);
  const senderId = normalizeMemberId(String(message.data.uidFrom ?? ""));
  if (!senderId || senderId === listener.botId) return;

  const rawTimestamp = Number(message.data.ts ?? Date.now());
  const sentAtUnixMs = rawTimestamp < 10_000_000_000 ? rawTimestamp * 1000 : rawTimestamp;
  await postWebhook(listener, {
    accountId,
    botId: listener.botId,
    groupId,
    messageId,
    senderId,
    senderName: String(message.data.dName || `Zalo ${senderId}`),
    content: message.data.content,
    mentions,
    mentionedBot: mentions.some((mention) => mention.uid === listener.botId),
    sentAtUnixMs,
  });
}

export async function startListener(request: StartListenerRequest) {
  const accountId = normalizeMemberId(request.accountId);
  const groupIds = new Set(request.groupIds.map(normalizeId).filter(Boolean));
  const credentialFingerprint = fingerprint(request.credentials);
  const current = activeListeners.get(accountId);

  if (current && current.credentialFingerprint === credentialFingerprint) {
    current.groupIds = groupIds;
    current.webhookUrl = request.webhookUrl;
    current.webhookKey = request.webhookKey;
    return { accountId, botId: current.botId, startedAt: current.startedAt, groupCount: groupIds.size };
  }

  current?.api?.listener.stop();
  if (mockMode) {
    const startedAt = Date.now();
    activeListeners.set(accountId, {
      api: null,
      credentialFingerprint,
      groupIds,
      webhookUrl: request.webhookUrl,
      webhookKey: request.webhookKey,
      botId: accountId,
      startedAt,
    });
    return { accountId, botId: accountId, startedAt, groupCount: groupIds.size };
  }

  let api: MinimalZaloApi;
  try {
    api = await getApi(request.credentials);
  } catch (error) {
    apiCache.delete(credentialFingerprint);
    console.error(`[Zalo listener ${accountId}] Login failed while starting listener:`, error);
    throw error;
  }
  const listener: ActiveListener = {
    api,
    credentialFingerprint,
    groupIds,
    webhookUrl: request.webhookUrl,
    webhookKey: request.webhookKey,
    botId: normalizeMemberId(api.getOwnId()),
    startedAt: Date.now(),
  };
  activeListeners.set(accountId, listener);
  api.listener.on("message", (message) => {
    void handleIncomingMessage(accountId, listener, message).catch((error) =>
      console.error(`[Zalo listener ${accountId}] Failed to forward message:`, error),
    );
  });
  api.listener.on("group_event", (event) => handleBoardEvent(accountId, listener, event));
  api.listener.on("error", (error) => console.error(`[Zalo listener ${accountId}]`, error));
  api.listener.on("closed", (code, reason) => {
    if (activeListeners.get(accountId) === listener) activeListeners.delete(accountId);
    console.warn(`[Zalo listener ${accountId}] closed (${code}): ${reason}`);
  });
  api.listener.start({ retryOnClose: true });
  return { accountId, botId: listener.botId, startedAt: listener.startedAt, groupCount: groupIds.size };
}

export function stopListener(accountIdValue: string) {
  const accountId = normalizeMemberId(accountIdValue);
  const listener = activeListeners.get(accountId);
  listener?.api?.listener.stop();
  activeListeners.delete(accountId);
  return { stopped: Boolean(listener) };
}

export function getListenerStatuses() {
  return [...activeListeners.entries()].map(([accountId, listener]) => ({
    accountId,
    botId: listener.botId,
    startedAt: listener.startedAt,
    groupIds: [...listener.groupIds],
  }));
}

export function getActiveListenerWebhookUrls(): string[] {
  return [...new Set([...activeListeners.values()].map((listener) => listener.webhookUrl))];
}

async function downloadImage(url: string) {
  const response = await fetch(url, { signal: AbortSignal.timeout(20_000) });
  if (!response.ok) throw new Error(`Cannot download configured image (${response.status})`);
  const data = Buffer.from(await response.arrayBuffer());
  if (data.length > 10 * 1024 * 1024) throw new Error("Configured image exceeds 10 MB");
  const contentType = response.headers.get("content-type") ?? "image/jpeg";
  const extension = contentType.includes("png") ? "png" : contentType.includes("webp") ? "webp" : "jpg";
  return { data, filename: `location.${extension}` as `${string}.${string}`, metadata: { totalSize: data.length } };
}

export async function sendGroupMessage(request: SendGroupMessageRequest) {
  const idempotencyKey = request.idempotencyKey?.trim();
  const now = Date.now();
  for (const [key, entry] of outgoingIdempotency) {
    if (entry.expiresAt <= now) outgoingIdempotency.delete(key);
  }
  if (idempotencyKey) {
    const existing = outgoingIdempotency.get(idempotencyKey);
    if (existing && existing.expiresAt > now) return existing.result;
    const result = sendGroupMessageCore(request).catch((error) => {
      outgoingIdempotency.delete(idempotencyKey);
      throw error;
    });
    outgoingIdempotency.set(idempotencyKey, { expiresAt: now + 24 * 60 * 60 * 1000, result });
    return result;
  }
  return sendGroupMessageCore(request);
}

async function sendGroupMessageCore(request: SendGroupMessageRequest) {
  const accountId = normalizeMemberId(request.accountId);
  const listener = activeListeners.get(accountId);
  if (!listener) throw new Error("Zalo listener is not active for this account");
  if (mockMode || !listener.api) {
    console.log(`[Zalo mock -> ${request.groupId}] ${request.message}`);
    return { sent: true, mock: true };
  }
  let attachments: Array<Awaited<ReturnType<typeof downloadImage>>> | undefined;
  if (request.imageUrl) {
    try {
      attachments = [await downloadImage(request.imageUrl)];
    } catch (error) {
      console.warn(`[Zalo message ${accountId}] Could not attach configured image:`, error);
    }
  }
  await listener.api.sendMessage(
    { msg: request.message, mentions: request.mentions, attachments },
    normalizeId(request.groupId),
    ThreadType.Group,
  );
  return { sent: true, mock: false };
}

function onQrEvent(session: QrLoginSession, event: MinimalQrEvent) {
  switch (event.type) {
    case LoginQRCallbackEventType.QRCodeGenerated:
      session.qrImageBase64 = String(event.data?.image ?? "");
      session.status = "waiting_scan";
      break;
    case LoginQRCallbackEventType.QRCodeScanned:
      session.displayName = String(event.data?.display_name ?? "");
      session.avatarUrl = String(event.data?.avatar ?? "");
      session.status = "waiting_confirm";
      break;
    case LoginQRCallbackEventType.QRCodeExpired:
      session.status = "expired";
      break;
    case LoginQRCallbackEventType.QRCodeDeclined:
      session.status = "declined";
      break;
    case LoginQRCallbackEventType.GotLoginInfo:
      session.credentials = {
        cookie: Array.isArray(event.data?.cookie) ? event.data.cookie : [],
        imei: String(event.data?.imei ?? ""),
        userAgent: String(event.data?.userAgent ?? ""),
        language: "vi",
      };
      break;
  }
}

async function runQrLogin(session: QrLoginSession) {
  if (mockMode) {
    session.status = "completed";
    session.qrImageBase64 = null;
    session.displayName = "Zalo Mock";
    session.accountZaloId = "mock-account";
    session.credentials = mockCredentials;
    return;
  }

  try {
    const zalo = new Zalo({ logging: true, checkUpdate: false });
    const api = await zalo.loginQR({}, (event: MinimalQrEvent) => onQrEvent(session, event));
    session.accountZaloId = normalizeMemberId(api.getOwnId());
    try {
      const account = await api.fetchAccountInfo();
      session.displayName = account.displayName || account.zaloName || session.displayName;
      session.avatarUrl = account.avatar || session.avatarUrl;
    } catch {
      // Login is still valid when optional profile enrichment fails.

    }
    session.status = "completed";
  } catch (error) {
    console.error("[Zalo QR] Login failed:", error);
    if (session.status !== "expired" && session.status !== "declined") {
      session.status = "failed";
      session.error = publicError(error);
    }

  }
}

export function createQrLogin(): QrLoginSession {
  const id = randomUUID();
  const session: QrLoginSession = {
    id,
    status: "waiting_qr",
    qrImageBase64: null,
    displayName: null,
    avatarUrl: null,
    accountZaloId: null,
    credentials: null,
    error: null,
    expiresAt: new Date(Date.now() + 120_000).toISOString(),
  };
  qrSessions.set(id, session);
  void runQrLogin(session);
  return session;
}

export function getQrLogin(id: string): QrLoginSession | null {
  return qrSessions.get(id) ?? null;
}

export async function getGroups(credentials: ZaloCredentials): Promise<BridgeGroup[]> {
  if (mockMode) return mockGroups;
  const api = await getApi(credentials);
  const list = await api.getAllGroups();
  const ids = Object.keys(list.gridVerMap ?? {});
  const groups: BridgeGroup[] = [];

  for (let offset = 0; offset < ids.length; offset += 50) {
    const batch = ids.slice(offset, offset + 50);
    const response = await api.getGroupInfo(batch);
    for (const groupId of batch) {
      const group = response.gridInfoMap?.[groupId];
      if (!group) continue;
      groups.push({
        id: normalizeId(group.groupId || groupId),
        name: String(group.name ?? ""),
        avatarUrl: group.fullAvt ? String(group.fullAvt) : group.avt ? String(group.avt) : null,
        totalMembers: Number(group.totalMember || 0),
      });
    }
  }

  return groups.sort((left, right) => left.name.localeCompare(right.name, "vi"));
}

export async function getGroupMemberDirectory(
  credentials: ZaloCredentials,
  groupId: string,
): Promise<BridgeGroupMemberDirectory> {
  const normalizedGroupId = normalizeId(groupId);
  if (mockMode) {
    const group = mockGroups.find((item) => item.id === normalizedGroupId);
    if (!group) throw new Error("Group information is unavailable");
    return {
      groupId: normalizedGroupId,
      groupName: group.name,
      groupCreatedAtUnixMs: Date.now() - 365 * 86_400_000,
      expectedMemberCount: mockMembers.length,
      isComplete: true,
      members: mockMembers,
    };
  }

  const api = await getApi(credentials);
  const response = await api.getGroupInfo([normalizedGroupId]);
  const info = response.gridInfoMap?.[normalizedGroupId];
  if (!info) throw new Error("Group information is unavailable");

  const memberIds = Array.isArray(info.memberIds)
    ? [...new Set(info.memberIds.map(normalizeMemberId).filter(Boolean))]
    : [];
  const members = await resolveMembers(api, memberIds);
  const expectedMemberCount = Number(info.totalMember ?? memberIds.length);
  const hasMoreMember = Number(info.hasMoreMember ?? 0);
  return {
    groupId: normalizedGroupId,
    groupName: String(info.name ?? ""),
    groupCreatedAtUnixMs: normalizeUnixMs(info.createdTime),
    expectedMemberCount,
    isComplete: hasMoreMember === 0 && members.length >= expectedMemberCount,
    members,
  };
}

export async function getBoardPage(
  credentials: ZaloCredentials,
  groupId: string,
  pageValue: number,
  pageSizeValue: number,
): Promise<BridgeBoardPage> {
  const normalizedGroupId = normalizeId(groupId);
  const page = Math.max(1, Math.trunc(pageValue || 1));
  const pageSize = Math.min(100, Math.max(1, Math.trunc(pageSizeValue || 50)));
  if (mockMode) {
    const allItems = normalizedGroupId === mockGroups[0]?.id
      ? mockPolls.map((poll) => ({
          stableId: `poll:${poll.id}`,
          boardType: Number(BoardType.Poll),
          isPoll: true,
          pollId: poll.id,
          poll,
        }))
      : [];
    const start = (page - 1) * pageSize;
    return {
      groupId: normalizedGroupId,
      page,
      pageSize,
      totalCount: allItems.length,
      items: allItems.slice(start, start + pageSize),
    };
  }

  const api = await getApi(credentials);
  const response = await api.getListBoard({ page, count: pageSize }, normalizedGroupId);
  const items = (response.items ?? []).map((item) => {
    const raw = item.data as Record<string, unknown>;
    const poll = item.boardType === BoardType.Poll ? normalizePoll(raw) : null;
    const rawStableId = normalizeId(
      raw.poll_id ?? raw.note_id ?? raw.pin_id ?? raw.topic_id ?? raw.id ?? "",
    );
    const fallback = createHash("sha256")
      .update(`${item.boardType}:${JSON.stringify(raw)}`)
      .digest("hex")
      .slice(0, 24);
    return {
      stableId: `${item.boardType}:${rawStableId || fallback}`,
      boardType: Number(item.boardType),
      isPoll: item.boardType === BoardType.Poll,
      pollId: poll?.id || null,
      poll,
    };
  });
  return {
    groupId: normalizedGroupId,
    page,
    pageSize,
    totalCount: Number(response.count ?? items.length),
    items,
  };
}

export async function getPolls(credentials: ZaloCredentials, groupId: string): Promise<BridgePoll[]> {
  if (mockMode) return groupId === mockGroups[0]?.id ? mockPolls : [];
  const api = await getApi(credentials);
  const polls = new Map<string, BridgePoll>();
  const pageSize = 50;

  for (let page = 1; page <= 10; page += 1) {
    const response = await api.getListBoard({ page, count: pageSize }, groupId);
    for (const item of response.items ?? []) {
      if (item.boardType !== BoardType.Poll) continue;
      const poll = normalizePoll(item.data as unknown as Record<string, unknown>);
      if (poll.id) polls.set(poll.id, poll);
    }
    if ((response.items?.length ?? 0) < pageSize) break;
  }

  return [...polls.values()].sort((left, right) => right.createdAtUnixMs - left.createdAtUnixMs);
}

export async function getPoll(credentials: ZaloCredentials, pollId: string): Promise<BridgePoll> {
  if (mockMode) {
    const poll = mockPolls.find((item) => item.id === pollId);
    if (!poll) throw new Error("Poll not found");
    return poll;
  }
  const api = await getApi(credentials);
  const detail = await api.getPollDetail(normalizeId(pollId));
  return normalizePoll(detail as unknown as Record<string, unknown>);
}

export async function getMembers(credentials: ZaloCredentials, memberIds: string[]): Promise<BridgeMember[]> {
  const uniqueIds = [...new Set(memberIds.map(normalizeMemberId).filter(Boolean))];
  if (mockMode) return mockMembers.filter((member) => uniqueIds.includes(member.zaloUserId));
  const api = await getApi(credentials);
  return resolveMembers(api, uniqueIds);
}

async function resolveMembers(api: MinimalZaloApi, uniqueIds: string[]): Promise<BridgeMember[]> {
  const members = new Map<string, BridgeMember>();

  for (let offset = 0; offset < uniqueIds.length; offset += 50) {
    const batch = uniqueIds.slice(offset, offset + 50);
    const response = await api.getGroupMembersInfo(batch);
    for (const [profileKey, value] of Object.entries(response.profiles ?? {})) {
      const member = normalizeMember(profileKey, value as unknown as Record<string, unknown>);
      if (member.zaloUserId) members.set(member.zaloUserId, member);
    }
  }

  for (const id of uniqueIds) {
    if (!members.has(id)) {
      members.set(id, { zaloUserId: id, displayName: `Zalo ${id}`, zaloName: null, avatarUrl: null });
    }
  }

  return uniqueIds.map((id) => members.get(id)!);
}

export async function getGroupMessageHistory(
  credentials: ZaloCredentials,
  groupId: string,
  requestedCountValue: number,
): Promise<BridgeMessageHistoryProbe> {
  const normalizedGroupId = normalizeId(groupId);
  const requestedCount = Math.min(5_000, Math.max(1, Math.trunc(requestedCountValue || 500)));
  if (mockMode) {
    const messages = mockHistoricalMessages.slice(-requestedCount);
    return buildMessageHistoryProbe(
      normalizedGroupId,
      requestedCount,
      messages,
      0,
      "mock-last",
      null,
    );
  }

  const api = await getApi(credentials);
  if (typeof api.getGroupChatHistory !== "function") {
    throw new Error("Installed Zalo library does not expose group chat history");
  }
  const response = await api.getGroupChatHistory(normalizedGroupId, requestedCount);
  const messages = (response.groupMsgs ?? [])
    .map(normalizeHistoricalMessage)
    .filter((message): message is BridgeHistoricalMessage => message !== null);
  return buildMessageHistoryProbe(
    normalizedGroupId,
    requestedCount,
    messages,
    Number(response.more ?? 0),
    response.lastActionId ? String(response.lastActionId) : null,
    response.lastActionIdOther ? String(response.lastActionIdOther) : null,
  );
}

export async function getGroupRoles(credentials: ZaloCredentials, groupId: string): Promise<BridgeGroupRoles> {
  const normalizedGroupId = normalizeId(groupId);
  if (mockMode) {
    return {
      groupId: normalizedGroupId,
      creatorId: mockMembers[0]?.zaloUserId ?? "",
      adminIds: mockMembers.slice(1, 3).map((member) => member.zaloUserId),
    };
  }

  const api = await getApi(credentials);
  const response = await api.getGroupInfo([normalizedGroupId]);
  const info = response.gridInfoMap?.[normalizedGroupId];
  if (!info) throw new Error("Group information is unavailable");

  const creatorId = normalizeMemberId(String(info.creatorId ?? ""));
  const adminIds = Array.isArray(info.adminIds)
    ? [...new Set(info.adminIds.map((id) => normalizeMemberId(String(id))).filter(Boolean))]
    : [];
  return { groupId: normalizedGroupId, creatorId, adminIds };
}
