import test from "node:test";
import assert from "node:assert/strict";
import { embedNluText, NluEmbeddingError, validateNluText } from "../src/nlu/bridgeMultilingualEmbedding.js";

test("experimental multilingual NLU is disabled unless explicitly enabled", async () => {
  const current = process.env.ZALO_BRIDGE_NLU_ENABLED;
  delete process.env.ZALO_BRIDGE_NLU_ENABLED;
  try {
    await assert.rejects(() => embedNluText("bonjour"), (error: unknown) =>
      error instanceof NluEmbeddingError && error.status === 404 && error.code === "nlu_disabled");
  } finally {
    if (current === undefined) delete process.env.ZALO_BRIDGE_NLU_ENABLED;
    else process.env.ZALO_BRIDGE_NLU_ENABLED = current;
  }
});

test("experimental multilingual input bounds enforce user-message safety", () => {
  assert.equal(validateNluText("  안녕하세요 bot  "), "안녕하세요 bot");
  assert.equal(validateNluText("中文测试"), "中文测试");
  assert.throws(() => validateNluText("  "), (error: unknown) =>
    error instanceof NluEmbeddingError && error.code === "text_length_out_of_bounds");
  assert.throws(() => validateNluText("a".repeat(501)), (error: unknown) =>
    error instanceof NluEmbeddingError && error.status === 400);
  assert.throws(() => validateNluText({ text: "oops" }), (error: unknown) =>
    error instanceof NluEmbeddingError && error.code === "text_required");
});

test("enabled NLU still fails closed when its verified model files are absent", async () => {
  const previousEnabled = process.env.ZALO_BRIDGE_NLU_ENABLED;
  const previousDir = process.env.ZALO_BRIDGE_NLU_MODEL_DIR;
  process.env.ZALO_BRIDGE_NLU_ENABLED = "true";
  process.env.ZALO_BRIDGE_NLU_MODEL_DIR = "./this-model-directory-does-not-exist";
  try {
    await assert.rejects(
      () => embedNluText("Je voudrais faire équipe avec To An"),
      (error: unknown) => error instanceof NluEmbeddingError && error.status === 503 && error.code === "model_file_unavailable",
    );
  } finally {
    if (previousEnabled === undefined) delete process.env.ZALO_BRIDGE_NLU_ENABLED;
    else process.env.ZALO_BRIDGE_NLU_ENABLED = previousEnabled;
    if (previousDir === undefined) delete process.env.ZALO_BRIDGE_NLU_MODEL_DIR;
    else process.env.ZALO_BRIDGE_NLU_MODEL_DIR = previousDir;
  }
});
