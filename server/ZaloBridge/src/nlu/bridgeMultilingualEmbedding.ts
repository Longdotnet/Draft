/**
 * Experimental, read-only multilingual encoder using E5-small int8.
 *
 * Must NEVER decide/mutate team preferences. The backend owns semantic intent,
 * grounding, authorization and confirmation. Uses local model files only.
 *
 * Lazy import/load ensures the bridge's normal Zalo operations do not consume
 * model memory when ZALO_BRIDGE_NLU_ENABLED is not explicitly true.
 */
import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import { resolve } from "node:path";
import { performance } from "node:perf_hooks";

export interface NluEmbedding {
  vector: number[];
  tokenCount: number;
  dimensions: number;
  inferenceMs: number;
}

export class NluEmbeddingError extends Error {
  constructor(readonly status: number, readonly code: string) {
    super(code);
    this.name = "NluEmbeddingError";
  }
}

type EncoderRuntime = {
  tokenize: (text: string) => number[];
  run: (ids: number[]) => Promise<NluEmbedding>;
};

const MODEL_SHA256 = "4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c";
const MAX_TEXT_CODEPOINTS = 500;
const MAX_MODEL_TOKENS = 64;
const MAX_RSS_MIB = 410;
const MAX_PRELOAD_RSS_MIB = 180;
let encoderPromise: Promise<EncoderRuntime> | undefined;
let queued = 0;

export function validateNluText(input: unknown): string {
  if (typeof input !== "string") throw new NluEmbeddingError(400, "text_required");
  const text = input.trim();
  if (!text || Array.from(text).length > MAX_TEXT_CODEPOINTS) {
    throw new NluEmbeddingError(400, "text_length_out_of_bounds");
  }
  return text;
}

function rssMiB(): number {
  return process.memoryUsage().rss / (1024 * 1024);
}

async function sha256(path: string): Promise<string> {
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest("hex");
}

async function createRuntime(): Promise<EncoderRuntime> {
  const dir = process.env.ZALO_BRIDGE_NLU_MODEL_DIR?.trim() || resolve("dist/nlu-model");
  if (rssMiB() > MAX_PRELOAD_RSS_MIB) {
    throw new NluEmbeddingError(503, "insufficient_memory_to_load_model");
  }
  const model = resolve(dir, "model_int8.onnx");
  const sentencepieceModel = resolve(dir, "sentencepiece.bpe.model");
  let checksum: string;
  try {
    checksum = await sha256(model);
  } catch {
    throw new NluEmbeddingError(503, "model_file_unavailable");
  }
  if (checksum !== MODEL_SHA256) throw new NluEmbeddingError(503, "model_integrity_mismatch");
  const [{ InferenceSession, Tensor }, { SentencePieceProcessor }] = await Promise.all([
    import("onnxruntime-node"),
    import("@sctg/sentencepiece-js"),
  ]);
  const sp = new SentencePieceProcessor();
  try {
    await sp.load(sentencepieceModel);
  } catch {
    throw new NluEmbeddingError(503, "tokenizer_file_unavailable");
  }
  const session = await InferenceSession.create(model, {
    executionProviders: ["cpu"],
    intraOpNumThreads: 1,
    interOpNumThreads: 1,
    enableCpuMemArena: false,
    enableMemPattern: false,
    graphOptimizationLevel: "all",
  });
  if (rssMiB() > MAX_RSS_MIB) {
    throw new NluEmbeddingError(503, "model_exceeds_memory_guard");
  }

  return {
    tokenize: (text) => [0, ...sp.encodeIds("query: " + text).slice(0, MAX_MODEL_TOKENS - 2).map(id => id + 1), 2],
    run: async (ids) => {
      const int64 = (values: number[]) => BigInt64Array.from(values, BigInt);
      const dims = [1, ids.length];
      const feeds = {
        input_ids: new Tensor("int64", int64(ids), dims),
        attention_mask: new Tensor("int64", int64(ids.map(() => 1)), dims),
        token_type_ids: new Tensor("int64", int64(ids.map(() => 0)), dims),
      };
      const start = performance.now();
      const output = await session.run(feeds);
      try {
        const outputName = session.outputNames[0];
        if (!outputName) throw new NluEmbeddingError(503, "missing_embedding_output");
        const values = output[outputName];
        if (!values) throw new NluEmbeddingError(503, "missing_embedding_output");
        const shape = values.dims;
        if (shape.length !== 3 || shape[0] !== 1 || shape[1] !== ids.length || shape[2] !== 384) {
          throw new NluEmbeddingError(503, "unexpected_embedding_shape");
        }
        const data = values.data as Float32Array;
        const sum = new Float64Array(384);
        // Mean pooling, same attention mask behavior as E5 sentence embeddings.
        for (let token = 0; token < ids.length; token++) {
          for (let dim = 0; dim < 384; dim++) {
            sum[dim] = (sum[dim] ?? 0) + (data[token * 384 + dim] ?? 0);
          }
        }
        let norm = 0;
        for (let dim = 0; dim < 384; dim++) {
          const pooled = (sum[dim] ?? 0) / ids.length;
          sum[dim] = pooled;
          norm += pooled * pooled;
        }
        norm = Math.sqrt(norm) || 1;
        return {
          vector: Array.from(sum, n => Math.round((n / norm) * 1_000_000) / 1_000_000),
          tokenCount: ids.length,
          dimensions: 384,
          inferenceMs: Math.round((performance.now() - start) * 10) / 10,
        };
      } finally {
        for (const value of Object.values(output)) value.dispose?.();
      }
    },
  };
}

export async function embedNluText(input: unknown): Promise<NluEmbedding> {
  if (process.env.ZALO_BRIDGE_NLU_ENABLED !== "true") {
    throw new NluEmbeddingError(404, "nlu_disabled");
  }
  const text = validateNluText(input);
  // No parallel ONNX runs on the 0.15 CPU / 512 MiB free instance.
  if (queued >= 1) throw new NluEmbeddingError(429, "nlu_queue_full");
  if (rssMiB() > MAX_RSS_MIB) throw new NluEmbeddingError(503, "memory_guard_exceeded");
  queued++;
  try {
    encoderPromise ??= createRuntime().catch(error => {
      encoderPromise = undefined;
      throw error;
    });
    const encoder = await encoderPromise;
    const ids = encoder.tokenize(text);
    if (rssMiB() > MAX_RSS_MIB) throw new NluEmbeddingError(503, "memory_guard_exceeded");
    return await encoder.run(ids);
  } finally {
    queued--;
  }
}
