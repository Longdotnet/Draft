import assert from "node:assert/strict";
import test from "node:test";
import type { ZaloCredentials } from "../src/contracts.js";
import { wrapProviderReadApi, wrapProviderStickerApi } from "../src/providerApiBoundary.js";
import { ZaloProviderTrafficGovernor } from "../src/zaloProviderTraffic.js";

function credentials(seed: string): ZaloCredentials {
  return {
    cookie: [{ name: `cookie-${seed}`, value: `value-${seed}` }],
    imei: `imei-${seed}`,
    userAgent: `ua-${seed}`,
    language: "vi",
  };
}

test("one logical paginated read records and spaces every concrete SDK call", async () => {
  let now = 10_000;
  const starts: number[] = [];
  const sleeps: number[] = [];
  const accountCredentials = credentials("pages");
  const governor = new ZaloProviderTrafficGovernor({
    now: () => now,
    minGapMs: 750,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });
  governor.bindAccount("account-pages", accountCredentials);

  const rawApi = {
    async getListBoard(page: number) {
      starts.push(now);
      return { page };
    },
    async sendMessage() {
      throw new Error("sendMessage must not be wrapped by the read boundary");
    },
  };
  const api = wrapProviderReadApi(rawApi, (operation, action) =>
    governor.runWithCredentials(accountCredentials, action, operation));

  const result = await governor.coalesceReadWithCredentials(
    accountCredentials,
    "group:123:polls",
    async () => [
      await api.getListBoard(1),
      await api.getListBoard(2),
      await api.getListBoard(3),
    ],
    "polls.list",
  );

  assert.deepEqual(result, [{ page: 1 }, { page: 2 }, { page: 3 }]);
  assert.deepEqual(starts, [10_000, 10_750, 11_500]);
  assert.deepEqual(sleeps, [750, 750]);

  const diagnostics = governor.getDiagnostics();
  assert.equal(diagnostics.aggregate.providerAttempts, 3);
  assert.equal(diagnostics.aggregate.providerSuccesses, 3);
  assert.equal(diagnostics.operations.find((item) => item.operation === "sdk.getListBoard")?.providerAttempts, 3);
  assert.equal(diagnostics.operations.some((item) => item.operation === "polls.list" && item.providerAttempts > 0), false);
});

test("429 from one SDK page opens cooldown and later pages never touch Zalo", async () => {
  let now = 50_000;
  let providerCalls = 0;
  const accountCredentials = credentials("429-page");
  const governor = new ZaloProviderTrafficGovernor({
    now: () => now,
    minGapMs: 0,
    defaultCooldownMs: 60_000,
  });
  governor.bindAccount("account-429", accountCredentials);

  const api = wrapProviderReadApi({
    async getListBoard(page: number) {
      providerCalls += 1;
      if (page === 2) throw { response: { status: 429, headers: { "retry-after": "120" } } };
      return { page };
    },
  }, (operation, action) => governor.runWithCredentials(accountCredentials, action, operation));

  await assert.rejects(governor.coalesceReadWithCredentials(
    accountCredentials,
    "group:429:polls",
    async () => {
      await api.getListBoard(1);
      await api.getListBoard(2);
      await api.getListBoard(3);
    },
    "polls.list",
  ));

  assert.equal(providerCalls, 2, "the page after a real 429 must never be attempted");
  const account = governor.getDiagnostics().accounts[0];
  assert.equal(account?.coolingDown, true);
  assert.equal(account?.providerAttempts, 2);
  assert.equal(account?.providerFailures, 1);

  await assert.rejects(api.getListBoard(3));
  assert.equal(providerCalls, 2, "local cooldown rejection must not touch the provider");
  assert.equal(governor.getDiagnostics().accounts[0]?.localCooldownRejects, 1);

  now += 120_000;
  assert.deepEqual(await api.getListBoard(3), { page: 3 });
  assert.equal(providerCalls, 3);
});

test("identical logical reads coalesce while underlying SDK calls remain individually governed", async () => {
  const accountCredentials = credentials("coalesce-boundary");
  const governor = new ZaloProviderTrafficGovernor({ minGapMs: 0 });
  let providerCalls = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });

  const api = wrapProviderReadApi({
    async getListBoard(page: number) {
      providerCalls += 1;
      if (page === 1) await gate;
      return { page };
    },
  }, (operation, action) => governor.runWithCredentials(accountCredentials, action, operation));

  const load = () => governor.coalesceReadWithCredentials(
    accountCredentials,
    "group:7:polls",
    async () => [await api.getListBoard(1), await api.getListBoard(2)],
    "polls.list",
  );
  const first = load();
  const duplicate = load();

  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(providerCalls, 1);
  release();
  assert.deepEqual(await Promise.all([first, duplicate]), [
    [{ page: 1 }, { page: 2 }],
    [{ page: 1 }, { page: 2 }],
  ]);
  assert.equal(providerCalls, 2, "coalescing must save the duplicate logical read, not collapse real pagination");

  const diagnostics = governor.getDiagnostics();
  assert.equal(diagnostics.aggregate.providerAttempts, 2);
  assert.equal(diagnostics.aggregate.coalescedReads, 1);
});

test("non-read API members are passed through without an inner governor", async () => {
  const operations: string[] = [];
  const rawApi = {
    getOwnId: () => "account",
    listener: { start: () => undefined },
    sendMessage: async () => "sent",
    getGroupInfo: async () => "groups",
  };
  const api = wrapProviderReadApi(rawApi, async (operation, action) => {
    operations.push(operation);
    return action();
  });

  assert.equal(api.getOwnId(), "account");
  api.listener.start();
  assert.equal(await api.sendMessage(), "sent");
  assert.equal(await api.getGroupInfo(), "groups");
  assert.deepEqual(operations, ["sdk.getGroupInfo"]);
});

test("sticker lookup fallback, detail lookup, and final send are separate paced attempts", async () => {
  let now = 80_000;
  const starts: Array<{ operation: string; at: number }> = [];
  const sleeps: number[] = [];
  const accountCredentials = credentials("sticker");
  const governor = new ZaloProviderTrafficGovernor({
    now: () => now,
    minGapMs: 750,
    sleep: async (milliseconds) => {
      sleeps.push(milliseconds);
      now += milliseconds;
    },
  });
  governor.bindAccount("account-sticker", accountCredentials);

  const rawApi = {
    async getStickers(keyword: string) {
      return keyword === "first" ? [] : [42];
    },
    async getStickersDetail() {
      return [{ id: 42, cateId: 7, type: 1 }];
    },
    async sendSticker() {
      return { msgId: "sent-42" };
    },
  };
  const api = wrapProviderStickerApi(rawApi, (operation, action) =>
    governor.runWithCredentials(accountCredentials, async () => {
      starts.push({ operation, at: now });
      return action();
    }, operation));

  assert.deepEqual(await api.getStickers("first"), []);
  assert.deepEqual(await api.getStickers("second"), [42]);
  assert.deepEqual(await api.getStickersDetail(), [{ id: 42, cateId: 7, type: 1 }]);
  assert.deepEqual(await api.sendSticker(), { msgId: "sent-42" });

  assert.deepEqual(starts, [
    { operation: "sdk.getStickers", at: 80_000 },
    { operation: "sdk.getStickers", at: 80_750 },
    { operation: "sdk.getStickersDetail", at: 81_500 },
    { operation: "sdk.sendSticker", at: 82_250 },
  ]);
  assert.deepEqual(sleeps, [750, 750, 750]);
  assert.equal(governor.getDiagnostics().aggregate.providerAttempts, 4);
});
