#!/usr/bin/env node
/**
 * Opt-in build-time preparation of the read-only E5 int8 model.
 * Usage: node scripts/prepare-nlu-model.mjs [dest-dir]
 *
 * Downloads happen only when this script is called explicitly, NOT during
 * npm ci / npm run build / ordinary Bridge startup.
 * Files are hash-verified and replaced only after a complete download.
 */
import { createHash } from "node:crypto";
import { createReadStream, createWriteStream } from "node:fs";
import { mkdir, rename, rm, stat } from "node:fs/promises";
import { join, resolve } from "node:path";
import { pipeline } from "node:stream/promises";
import { Readable } from "node:stream";

const files = [
  {
    name: "model_int8.onnx",
    url: "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model_int8.onnx",
    bytes: 118054593,
    sha256: "4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c",
  },
  {
    name: "sentencepiece.bpe.model",
    url: "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/sentencepiece.bpe.model",
    bytes: 5069051,
    sha256: "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865",
  },
];
const skipWhenDisabled = process.argv.includes("--if-enabled");
const destination = resolve(process.argv.find(arg => arg !== "--if-enabled" && arg !== process.argv[0] && arg !== process.argv[1]) || "dist/nlu-model");

if (skipWhenDisabled && process.env.ZALO_BRIDGE_NLU_ENABLED !== "true") {
  console.log("Bridge NLU model download skipped (feature disabled by default)");
  process.exit(0);
}

async function checksum(path) {
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest("hex");
}

await mkdir(destination, { recursive: true });
for (const file of files) {
  const target = join(destination, file.name);
  const previous = await stat(target).catch(() => null);
  if (previous?.size === file.bytes && (!file.sha256 || await checksum(target) === file.sha256)) {
    console.log("Already verified:", file.name);
    continue;
  }
  const response = await fetch(file.url, { redirect: "follow", signal: AbortSignal.timeout(180_000) });
  if (!response.ok || !response.body) throw new Error("Failed to download " + file.name + ": HTTP " + response.status);
  const tmp = join(destination, file.name + ".partial");
  try {
    await pipeline(Readable.fromWeb(response.body), createWriteStream(tmp));
    const downloaded = await stat(tmp);
    if (downloaded.size !== file.bytes) throw new Error("Unexpected size: " + file.name);
    if (file.sha256 && await checksum(tmp) !== file.sha256) throw new Error("SHA-256 mismatch: " + file.name);
    // Existing target can only be replaced after downloading and validating.
    await rename(tmp, target);
    console.log("Prepared:", file.name, downloaded.size, "bytes");
  } finally {
    await rm(tmp, { force: true }).catch(() => {});
  }
}
console.log("Bridge NLU model prepared at:", destination);
