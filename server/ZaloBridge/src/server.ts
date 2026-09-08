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
import { ScopedOutboundIdempotency } from "./outboundIdempotency.js";
import { isStickerReaction } from "./stickerLogic.js";
import { sendGroupSticker } from "./stickerGateway.js";
import { zaloProviderTrafficGovernor } from "./zaloProviderTraffic.js";
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

app.disable("x-powered-by");
app.use(express.json({ limit: "2mb" }));

app.get("/health", (_request, response) => {
  response.json({
    status: "ok",
    mockMode: process.env.ZALO_BRIDGE_MOCK === "true",
    activeListenerCount: getListenerStatuses().length,
    apiKeepAlive: {
      ...apiKeepAliveConfiguration,
      ...getApiKeepAliveRuntimeStatus(),
    },
    revision: process.env.RENDER_GIT_COMMIT?.slice(0, 7) ?? null,
  });
});

// Mark every response that actually entered the bridge process. If the API receives
// a 429/5xx without this marker, the response came from a proxy/host/front door before
// Express reached this middleware. Keep a request id so production screenshots can be
// correlated with bridge logs without exposing credentials or provider response bodies.
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
    groups: await zaloProviderTrafficGovernor.runWithCredentials(
      credentials,
      () => getGroups(credentials),
    ),
  });
});

app.post("/v1/groups/:groupId/polls", async (request, response) => {
  const credentials = credentialsFrom(request);
  response.json({
    polls: await zaloProviderTrafficGovernor.runWithCredentials(
      credentials,
      () => getPolls(credentials, request.params.groupId),
    ),
  });
});

app.post("/v1/groups/:groupId/members", async (request, response) => {
  const credentials = credentialsFrom(request);
  response.json(await zaloProviderTrafficGovernor.runWithCredentials(
    credentials,
    () => getGroupMemberDirectory(credentials, request.params.groupId),
  ));
});

app.post("/v1/groups/:groupId/board-pages", async (request, response) => {
  const credentials = credentialsFrom(request);
  const page = Number(request.body?.page ?? 1);
  const pageSize = Number(request.body?.pageSize ?? 50);
  response.json(await zaloProviderTrafficGovernor.runWithCredentials(
    credentials,
    () => getBoardPage(credentials, request.params.groupId, page, pageSize),
  ));
});

app.post("/v1/groups/:groupId/message-history", async (request, response) => {
  const credentials = credentialsFrom(request);
  const count = Number(request.body?.count ?? 500);
  response.json(await zaloProviderTrafficGovernor.runWithCredentials(
    credentials,
    () => getGroupMessageHistory(credentials, request.params.groupId, count),
  ));
});

app.post("/v1/groups/:groupId/roles", async (request, response) => {
  const credentials = credentialsFrom(request);
  response.json(await zaloProviderTrafficGovernor.runWithCredentials(
    credentials,
    () => getGroupRoles(credentials, request.params.groupId),
  ));
});

app.post("/v1/polls/:pollId", async (request, response) => {
  const credentials = credentialsFrom(request);
  response.json(await zaloProviderTrafficGovernor.runWithCredentials(
    credentials,
    () => getPoll(credentials, request.params.pollId),
  ));
});

app.post("/v1/group-members", async (request, response) => {
  const credentials = credentialsFrom(request);
  const memberIds = Array.isArray(request.body?.memberIds) ? request.body.memberIds.map(String) : [];
  if (memberIds.length > 500) {
    response.status(400).json({ error: "A maximum of 500 member IDs is allowed" });
    return;
  }
  response.json({
    members: await zaloProviderTrafficGovernor.runWithCredentials(
      credentials,
      () => getMembers(credentials, memberIds),
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
  zaloProviderTrafficGovernor.bindAccount(accountId, credentials);
  response.json(await zaloProviderTrafficGovernor.runWithCredentials(
    credentials,
    () => startListener({
      accountId,
      credentials,
      groupIds: body.groupIds!.map(String),
      webhookUrl: String(body.webhookUrl),
      webhookKey: String(body.webhookKey),
    }),
  ));
});

app.delete("/v1/listeners/:accountId", (request, response) => {
  response.json(stopListener(request.params.accountId));
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

function shutdown() {
  stopApiKeepAlive();
  server.close();
}

process.once("SIGTERM", shutdown);
process.once("SIGINT", shutdown);
