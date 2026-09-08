import assert from "node:assert/strict";
import test from "node:test";
import { KeyedSerialExecutor } from "../src/keyedSerialExecutor.js";

test("serializes concurrent lifecycle work for the same account", async () => {
  const executor = new KeyedSerialExecutor();
  const events: string[] = [];
  let releaseFirst!: () => void;
  let markFirstStarted!: () => void;
  const firstBlock = new Promise<void>((resolve) => { releaseFirst = resolve; });
  const firstStarted = new Promise<void>((resolve) => { markFirstStarted = resolve; });

  const first = executor.run("account-a", async () => {
    events.push("first:start");
    markFirstStarted();
    await firstBlock;
    events.push("first:end");
    return "first";
  });
  const second = executor.run("account-a", async () => {
    events.push("second:start");
    events.push("second:end");
    return "second";
  });

  await firstStarted;
  assert.deepEqual(events, ["first:start"]);
  releaseFirst();
  assert.deepEqual(await Promise.all([first, second]), ["first", "second"]);
  assert.deepEqual(events, ["first:start", "first:end", "second:start", "second:end"]);
});

test("allows different accounts to progress independently", async () => {
  const executor = new KeyedSerialExecutor();
  let releaseA!: () => void;
  const blockA = new Promise<void>((resolve) => { releaseA = resolve; });
  const events: string[] = [];

  const accountA = executor.run("account-a", async () => {
    events.push("a:start");
    await blockA;
    events.push("a:end");
  });
  const accountB = executor.run("account-b", async () => {
    events.push("b:start");
    events.push("b:end");
  });

  await accountB;
  assert.deepEqual(events, ["a:start", "b:start", "b:end"]);
  releaseA();
  await accountA;
});

test("continues queued lifecycle work after a failed predecessor", async () => {
  const executor = new KeyedSerialExecutor();
  const first = executor.run("account-a", async () => {
    throw new Error("login failed");
  });
  const second = executor.run("account-a", async () => "recovered");

  await assert.rejects(first, /login failed/);
  assert.equal(await second, "recovered");
});

test("orders stop behind an in-flight start so stop wins deterministically", async () => {
  const executor = new KeyedSerialExecutor();
  let active = false;
  let releaseStart!: () => void;
  const startBlock = new Promise<void>((resolve) => { releaseStart = resolve; });

  const start = executor.run("account-a", async () => {
    await startBlock;
    active = true;
  });
  const stop = executor.run("account-a", async () => {
    active = false;
  });

  releaseStart();
  await Promise.all([start, stop]);
  assert.equal(active, false);
});
