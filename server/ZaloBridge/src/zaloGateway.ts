import { createHash, randomUUID } from "node:crypto";
// The published 2.1.0 package points TypeScript at an invalid declaration entrypoint.
// Keep all untyped interaction isolated in this adapter while runtime exports remain valid.
import * as ZaloRuntime from "zca-js";
import type { BridgeGroup, BridgeMember, BridgePoll, ZaloCredentials } from "./contracts.js";
import { mockCredentials, mockGroups, mockMembers, mockPolls } from "./mockData.js";
import { normalizeId, normalizeMember, normalizeMemberId, normalizePoll } from "./pollLogic.js";

type QrLoginStatus = "waiting_qr" | "waiting_scan" | "waiting_confirm" | "completed" | "expired" | "declined" | "failed";

type MinimalZaloApi = {
  getOwnId(): string;
  fetchAccountInfo(): Promise<{ displayName?: string; zaloName?: string; avatar?: string }>;
  getAllGroups(): Promise<{ gridVerMap?: Record<string, string> }>;
  getGroupInfo(ids: string[]): Promise<{ gridInfoMap?: Record<string, Record<string, unknown>> }>;
  getListBoard(options: { page: number; count: number }, groupId: string): Promise<{
    items?: Array<{ boardType: number; data: Record<string, unknown> }>;
  }>;
  getPollDetail(pollId: string): Promise<Record<string, unknown>>;
  getGroupMembersInfo(ids: string[]): Promise<{ profiles?: Record<string, Record<string, unknown>> }>;
};

type MinimalQrEvent = {
  type: number;
  data: Record<string, unknown> | null;
};

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
};
const { Zalo, BoardType, LoginQRCallbackEventType } = runtime;

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
const mockMode = process.env.ZALO_BRIDGE_MOCK === "true";

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
