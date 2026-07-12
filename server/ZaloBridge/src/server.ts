import express, { type NextFunction, type Request, type Response } from "express";
import type { ZaloCredentials } from "./contracts.js";
import { createQrLogin, getGroups, getMembers, getPoll, getPolls, getQrLogin } from "./zaloGateway.js";

const app = express();
const port = Number(process.env.PORT || 3000);
const configuredInternalKey = process.env.ZALO_BRIDGE_INTERNAL_KEY;
if (!configuredInternalKey && process.env.NODE_ENV === "production") {
  throw new Error("ZALO_BRIDGE_INTERNAL_KEY is required in production.");
}
const internalKey = configuredInternalKey || "development-zalo-bridge-key";

app.disable("x-powered-by");
app.use(express.json({ limit: "2mb" }));

app.get("/health", (_request, response) => {
  response.json({ status: "ok", mockMode: process.env.ZALO_BRIDGE_MOCK === "true" });
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

app.use((error: unknown, _request: Request, response: Response, _next: NextFunction) => {
  const message = error instanceof Error ? error.message : "Unexpected bridge error";
  response.status(502).json({ error: message });
});

app.listen(port, "0.0.0.0", () => {
  console.log(`Zalo bridge listening on port ${port}`);
});
