import { randomUUID } from "node:crypto";
import express, { type NextFunction, type Request, type Response } from "express";
import {
  BridgeHttpError,
  bridgeErrorLogFields,
  classifyBridgeError,
} from "./bridgeErrors.js";
import {
  getApiKeepAliveConfiguration,
  getApiKeepAliveRuntimeStatus,
  startApiKeepAlive,
} from "./apiKeepAlive.js";
import type {
  SendGroupMessageRequest,
  SendGroupStickerRequest,
  StartListenerRequest,
  ZaloCredentials,
} from "./contracts.js";
import { quiesceListenersAndDrain } from "./gracefulShutdown.js";
import { KeyedSerialExecutor } from "./keyedSerialExecutor.js";
import { ScopedOutboundIdempotency } from "./outboundIdempotency.js";
import { isStickerReaction } from "./stickerLogic.js";
import { sendGroupSticker } from "./stickerGateway.js";
import { zaloProviderTrafficGovernor } from "./zaloProviderTraffic.js";
import {
  drainWebhookDeliveries,
  getWebhookDeliveryStats,
} from "./webhookFetchReliability.js";
import {
  createQrLogin,
  getActiveListenerWebhookUrls,
  getBoardPage,
  getGroups,
  getGroupMemberDirectory,
  getGroupMessageHistory,
  getGroupRoles,
  getListenerStatuses,
  getMembers,
  getPoll,
  getPolls,
  getQrLogin,
  sendGroupMessage,
  startListener,
  stopListener,
} from "./zaloGateway.js";

const app = express();
const port = Number(process.env.PORT || 3000);
const configuredInternalKey = process.env.ZALO_BRIDGE_INTERNAL_KEY;
if (!configuredInternalKey && process.env.NODE_ENV === "production") {
  throw new Error("ZALO_BRIDGE_INTERNAL_KEY is required in production.");
}
const internalKey = configuredInternalKey || "development-zalo-bridge-key";
const apiKeepAliveConfiguration = getApiKeepAliveConfiguration();
const outboundMessageIdempotency = new ScopedOutboundIdempotency<Awaited<ReturnType<typeof sendGroupMessage>>>();
const outboundStickerIdempotency = new ScopedOutboundIdempotency<Awaited<ReturnType<typeof sendGroupSticker>>>();
const listenerLifecycle = new KeyedSerialExecutor();
const configuredShutdownDrainMs = Number(process.env.ZALO_BRIDGE_SHUTDOWN_DRAIN_MS ?? 20_000);
const shutdownDrainMs = Math.min(25_000, Math.max(1_000,
  Number.isFinite(configuredShutdownDrainMs) ? configuredShutdownDrainMs : 20_000));
const boardEventQuiesceMs = 1_750;

app.disable("x-powered-by");
app.use(express.json({ limit: "2mb" }));

app.get("/health", (_request, response) => {
  response.json({
    status: "ok",
    mockMode: process.env.ZALO_BRIDGE_MOCK === "true",
    activeListenerCount: getListenerStatuses().length,
    providerTraffic: zaloProviderTrafficGovernor.getHealthSnapshot(),
    webhookDelivery: getWebhookDeliveryStats(),
    apiKeepAlive: {
      ...apiKeepAliveConfiguration,
      ...getApiKeepAliveRuntimeStatus(),
    },
    revision: process.env.RENDER_GIT_COMMIT?.slice(0, 7) ?? null,
  });
});

app.use("/v1", (request, response, next) => {
  const requestId = request.header("x-request-id")?.trim() || randomUUID();
  response.setHeader("x-volley-bridge-response", "1");
  response.setHeader("x-volley-bridge-request-id", requestId);
  next();
});

app.use("/v1", (request, response, next) => {
  if (request.header("x-internal-key") !== internalKey) {
    response.status(401).json({ error: "Unauthorized bridge request" });
    return;
  }
  next();
});

app.get("/v1/provider-traffic", (_request, response) => {
  response.json(zaloProviderTrafficGovernor.getDiagnostics());
});

app.post("/v1/qr-logins", (_request, response) => {
  const session = createQrLogin();
  response.status(202).json({ id: session.id, status: session.status, expiresAt: session.expiresAt });
});

app.get("/v1/qr-logins/:id", (request, response) => {
  const session = getQrLogin(request.params.id);
  if (!session) {
    response.status(404).json({ error: "QR login session not found" });
    return;
  }
  response.json(session);
});

function credentialsFrom(request: Request): ZaloCredentials {
  const credentials = request.body?.credentials as ZaloCredentials | undefined;
  if (!credentials?.imei || !credentials?.userAgent || !Array.isArray(credentials.cookie)) {
    throw new BridgeHttpError(
      400,
      "bridge-validation",
      "invalid_credentials",
      false,
      "Valid Zalo credentials are required.",
    );
  }
  return credentials;
}

app.post("/v1/groups", async (request, response) => {
  const credentials = credentialsFrom(request);
  response.json({
    groups: await zaloProviderTrafficGovernor.runReadWithCredentials(
      credentials,
      "groups",
      () => getGroups(credentials),
      "groups.list",
    ),
  });
});

app.post("/v1/groups/:groupId/polls", async (request, response) => {
  const credentials = credentialsFrom(request);
  const groupId = request.params.groupId;
  response.json({
    polls: await zaloProviderTrafficGovernor.runReadWithCredentials(
      credentials,
      `group:${groupId}:polls`,
      () => getPolls(credentials, groupId),
      "polls.list",
    ),
  });
});

app.post("/v1/groups/:groupId/members", async (request, response) => {
  const credentials = credentialsFrom(request);
  const groupId = request.params.groupId;
  response.json(await zaloProviderTrafficGovernor.runReadWithCredentials(
    credentials,
    `group:${groupId}:members`,
    () => getGroupMemberDirectory(credentials, groupId),
    "members.directory",
  ));
});

app.post("/v1/groups/:groupId/board-pages", async (request, response) => {
  const credentials = credentialsFrom(request);
  const groupId = request.params.groupId;
  const page = Number(request.body?.page ?? 1);
  const pageSize = Number(request.body?.pageSize ?? 50);
  response.json(await zaloProviderTrafficGovernor.runReadWithCredentials(
    credentials,
    `group:${groupId}:board:${page}:${pageSize}`,
    () => getBoardPage(credentials, groupId, page, pageSize),
    "board.page",
  ));
});

app.post("/v1/groups/:groupId/message-history", async (request, response) => {
  const credentials = credentialsFrom(request);
  const groupId = request.params.groupId;
  const count = Number(request.body?.count ?? 500);
  response.json(await zaloProviderTrafficGovernor.runReadWithCredentials(
    credentials,
    `group:${groupId}:history:${count}`,
    () => getGroupMessageHistory(credentials, groupId, count),
    "history.read",
  ));
});

app.post("/v1/groups/:groupId/roles", async (request, response) => {
  const credentials = credentialsFrom(request);
  const groupId = request.params.groupId;
  response.json(await zaloProviderTrafficGovernor.runReadWithCredentials(
    credentials,
    `group:${groupId}:roles`,
    () => getGroupRoles(credentials, groupId),
    "roles.read",
  ));
});

app.post("/v1/polls/:pollId", async (request, response) => {
  const credentials = credentialsFrom(request);
  const pollId = request.params.pollId;
  response.json(await zaloProviderTrafficGovernor.runReadWithCredentials(
    credentials,
    `poll:${pollId}`,
    () => getPoll(credentials, pollId),
    "poll.detail",
  ));
});

app.post("/v1/group-members", async (request, response) => {
  const credentials = credentialsFrom(request);
  const memberIds = Array.isArray(request.body?.memberIds) ? request.body.memberIds.map(String) : [];
  if (memberIds.length > 500) {
    response.status(400).json({ error: "A maximum of 500 member IDs is allowed" });
    return;
  }
  const normalizedMemberKey = [...new Set(memberIds)].sort().join(",");
  response.json({
    members: await zaloProviderTrafficGovernor.runReadWithCredentials(
      credentials,
      `members:${normalizedMemberKey}`,
      () => getMembers(credentials, memberIds),
      "members.resolve",
    ),
  });
});

app.put("/v1/listeners/:accountId", async (request, response) => {
  const body = request.body as Partial<StartListenerRequest>;
  if (!body.credentials || !Array.isArray(body.groupIds) || !body.webhookUrl || !body.webhookKey) {
    response.status(400).json({ error: "credentials, groupIds, webhookUrl and webhookKey are required" });
    return;
  }
  const accountId = request.params.accountId;
  const credentials = body.credentials;
  const result = await listenerLifecycle.run(accountId, async () => {
    zaloProviderTrafficGovernor.bindAccount(accountId, credentials);
    return zaloProviderTrafficGovernor.runWithCredentials(
      credentials,
      () => startListener({
        accountId,
        credentials,
        groupIds: body.groupIds!.map(String),
        webhookUrl: String(body.webhookUrl),
        webhookKey: String(body.webhookKey),
      }),
      "listener.start",
    );
  });
  response.json(result);
});

app.delete("/v1/listeners/:accountId", async (request, response) => {
  response.json(await listenerLifecycle.run(
    request.params.accountId,
    async () => stopListener(request.params.accountId),
  ));
});

app.get("/v1/listeners", (_request, response) => {
  response.json({ listeners: getListenerStatuses() });
});

app.post("/v1/group-messages", async (request, response) => {
  const body = request.body as Partial<SendGroupMessageRequest>;
  if (!body.accountId || !body.groupId || !body.message) {
    response.status(400).json({ error: "accountId, groupId and message are required" });
    return;
  }
  const accountId = String(body.accountId);
  const groupId = String(body.groupId);
  const idempotencyKey = body.idempotencyKey ? String(body.idempotencyKey) : null;
  const outbound = {
    accountId,
    groupId,
    message: String(body.message),
    mentions: Array.isArray(body.mentions) ? body.mentions : [],
    imageUrl: body.imageUrl ? String(body.imageUrl) : null,
    idempotencyKey: null,
  };
  response.json(await outboundMessageIdempotency.run(
    { accountId, groupId, idempotencyKey },
    { message: outbound.message, mentions: outbound.mentions, imageUrl: outbound.imageUrl },
    () => zaloProviderTrafficGovernor.runWithAccount(
      accountId,
      () => sendGroupMessage(outbound),
      "message.send",
    ),
  ));
});

app.post("/v1/group-stickers", async (request, response) => {
  const body = request.body as Partial<SendGroupStickerRequest>;
  if (!body.accountId || !body.groupId || !isStickerReaction(body.reaction)) {
    response.status(400).json({ error: "accountId, groupId and a supported reaction are required" });
    return;
  }
  const accountId = String(body.accountId);
  const groupId = String(body.groupId);
  const idempotencyKey = body.idempotencyKey ? String(body.idempotencyKey) : null;
  const credentials = credentialsFrom(request);
  zaloProviderTrafficGovernor.bindAccount(accountId, credentials);
  const outbound = {
    accountId,
    groupId,
    credentials,
    reaction: body.reaction,
    idempotencyKey: null,
  };
  response.json(await outboundStickerIdempotency.run(
    { accountId, groupId, idempotencyKey },
    { reaction: outbound.reaction },
    () => zaloProviderTrafficGovernor.runWithCredentials(
      credentials,
      () => sendGroupSticker(outbound),
      "sticker.send",
    ),
  ));
});

app.use((error: unknown, request: Request, response: Response, _next: NextFunction) => {
  const descriptor = classifyBridgeError(error);
  response.setHeader("x-volley-bridge-error-source", descriptor.source);
  response.setHeader("x-volley-bridge-error-kind", descriptor.kind);
  response.setHeader("x-volley-bridge-retryable", String(descriptor.retryable));
  if (error instanceof BridgeHttpError && error.retryAfterSeconds !== null) {
    response.setHeader("Retry-After", String(Math.max(1, Math.ceil(error.retryAfterSeconds))));
  }
  console.error("[Zalo bridge] request failed", {
    method: request.method,
    path: request.path,
    requestId: response.getHeader("x-volley-bridge-request-id") ?? null,
    ...bridgeErrorLogFields(error, descriptor),
  });
  response.status(descriptor.status).json({
    error: descriptor.publicMessage,
    kind: descriptor.kind,
    retryable: descriptor.retryable,
  });
});

const stopApiKeepAlive = startApiKeepAlive(getActiveListenerWebhookUrls, apiKeepAliveConfiguration);
const server = app.listen(port, "0.0.0.0", () => {
  console.log(`Zalo bridge listening on port ${port}`);
});

let shutdownPromise: Promise<void> | null = null;

function closeHttpServer(): Promise<void> {
  return new Promise((resolve) => {
    server.close(() => resolve());
  });
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "unknown shutdown failure";
}

async function shutdown(signal: string): Promise<void> {
  if (shutdownPromise) return shutdownPromise;
  shutdownPromise = (async () => {
    console.info("[Zalo bridge] graceful shutdown started", {
      signal,
      activeListenerCount: getListenerStatuses().length,
      webhookDelivery: getWebhookDeliveryStats(),
    });

    stopApiKeepAlive();
    await closeHttpServer();

    const listeners = getListenerStatuses();
    const result = await quiesceListenersAndDrain({
      listeners,
      runListenerLifecycle: (accountId, action) => listenerLifecycle.run(accountId, action),
      stopListener,
      quiesce: () => new Promise<void>((resolve) => setTimeout(resolve, boardEventQuiesceMs)),
      drainWebhookDeliveries,
      drainBudgetMs: shutdownDrainMs,
    });

    console.info("[Zalo bridge] graceful shutdown completed", {
      drained: result.drained,
      drainBudgetMs: shutdownDrainMs,
      listenerStopFailures: result.listenerStopFailures.map((failure) => ({
        accountId: failure.accountId,
        error: errorMessage(failure.error),
      })),
      quiesceError: result.quiesceError ? errorMessage(result.quiesceError) : null,
      drainError: result.drainError ? errorMessage(result.drainError) : null,
      webhookDelivery: getWebhookDeliveryStats(),
    });

    if (result.listenerStopFailures.length > 0 || result.quiesceError || result.drainError) {
      throw new Error("Graceful shutdown completed with cleanup failures after webhook drain attempt");
    }
  })();
  return shutdownPromise;
}

function handleShutdownSignal(signal: string): void {
  void shutdown(signal).then(
    () => process.exit(0),
    (error) => {
      console.error("[Zalo bridge] graceful shutdown failed", {
        signal,
        error: errorMessage(error),
      });
      process.exit(1);
    },
  );
}

process.once("SIGTERM", () => handleShutdownSignal("SIGTERM"));
process.once("SIGINT", () => handleShutdownSignal("SIGINT"));
