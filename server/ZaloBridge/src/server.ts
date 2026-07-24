import express, { type NextFunction, type Request, type Response } from "express";
import {
  getApiKeepAliveConfiguration,
  getApiKeepAliveRuntimeStatus,
  startApiKeepAlive,
} from "./apiKeepAlive.js";
import type { SendGroupMessageRequest, StartListenerRequest, ZaloCredentials } from "./contracts.js";
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
    throw new Error("Valid Zalo credentials are required");
  }
  return credentials;
}

app.post("/v1/groups", async (request, response) => {
  response.json({ groups: await getGroups(credentialsFrom(request)) });
});

app.post("/v1/groups/:groupId/polls", async (request, response) => {
  response.json({ polls: await getPolls(credentialsFrom(request), request.params.groupId) });
});

app.post("/v1/groups/:groupId/members", async (request, response) => {
  response.json(await getGroupMemberDirectory(credentialsFrom(request), request.params.groupId));
});

app.post("/v1/groups/:groupId/board-pages", async (request, response) => {
  const page = Number(request.body?.page ?? 1);
  const pageSize = Number(request.body?.pageSize ?? 50);
  response.json(await getBoardPage(credentialsFrom(request), request.params.groupId, page, pageSize));
});

app.post("/v1/groups/:groupId/message-history", async (request, response) => {
  const count = Number(request.body?.count ?? 500);
  response.json(await getGroupMessageHistory(credentialsFrom(request), request.params.groupId, count));
});

app.post("/v1/groups/:groupId/roles", async (request, response) => {
  response.json(await getGroupRoles(credentialsFrom(request), request.params.groupId));
});

app.post("/v1/polls/:pollId", async (request, response) => {
  response.json(await getPoll(credentialsFrom(request), request.params.pollId));
});

app.post("/v1/group-members", async (request, response) => {
  const memberIds = Array.isArray(request.body?.memberIds) ? request.body.memberIds.map(String) : [];
  if (memberIds.length > 500) {
    response.status(400).json({ error: "A maximum of 500 member IDs is allowed" });
    return;
  }
  response.json({ members: await getMembers(credentialsFrom(request), memberIds) });
});

app.put("/v1/listeners/:accountId", async (request, response) => {
  const body = request.body as Partial<StartListenerRequest>;
  if (!body.credentials || !Array.isArray(body.groupIds) || !body.webhookUrl || !body.webhookKey) {
    response.status(400).json({ error: "credentials, groupIds, webhookUrl and webhookKey are required" });
    return;
  }
  response.json(await startListener({
    accountId: request.params.accountId,
    credentials: body.credentials,
    groupIds: body.groupIds.map(String),
    webhookUrl: String(body.webhookUrl),
    webhookKey: String(body.webhookKey),
  }));
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
  response.json(await sendGroupMessage({
    accountId: String(body.accountId),
    groupId: String(body.groupId),
    message: String(body.message),
    mentions: Array.isArray(body.mentions) ? body.mentions : [],
    imageUrl: body.imageUrl ? String(body.imageUrl) : null,
    idempotencyKey: body.idempotencyKey ? String(body.idempotencyKey) : null,
  }));
});

app.use((error: unknown, _request: Request, response: Response, _next: NextFunction) => {
  const message = error instanceof Error ? error.message : "Unexpected bridge error";
  console.error("[Zalo bridge] request failed:", error);
  response.status(502).json({ error: message });
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
