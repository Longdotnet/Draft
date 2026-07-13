import assert from "node:assert/strict";
import test from "node:test";
import { apiHealthUrlsFromWebhooks, pingApiHealthUrls } from "../src/apiKeepAlive.js";

test("derives and deduplicates API health URLs from listener webhooks", () => {
  assert.deepEqual(
    apiHealthUrlsFromWebhooks([
      "https://draft-6ml1.onrender.com/api/internal/zalo/events",
      "https://draft-6ml1.onrender.com/another/path?x=1",
      "javascript:alert(1)",
      "not-a-url",
    ]),
    ["https://draft-6ml1.onrender.com/health"],
  );
});

test("pings every unique API origin and reports failures without throwing", async () => {
  const requested: string[] = [];
  const fakeFetch = async (url: string) => {
    requested.push(url);
    return { ok: !url.includes("offline"), status: url.includes("offline") ? 502 : 200 };
  };

  const results = await pingApiHealthUrls(
    [
      "https://api.example.com/api/internal/zalo/events",
      "https://api.example.com/api/internal/zalo/events",
      "https://offline.example.com/api/internal/zalo/events",
    ],
    fakeFetch,
  );

  assert.deepEqual(requested, ["https://api.example.com/health", "https://offline.example.com/health"]);
  assert.equal(results[0]?.ok, true);
  assert.equal(results[1]?.status, 502);
  assert.equal(results[1]?.error, "HTTP 502");
});
