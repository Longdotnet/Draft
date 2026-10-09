#!/usr/bin/env node
// Read-only offline benchmark for using EXISTING Render ZaloBridge instead of Draft API.
// Install transformers.js into ignored server/ZaloBridge/node_modules/.cache/nlu-bench,
// and prepare cached local E5 files. Do not mount in the running bridge unless
// resource/accuracy/security gates have been reviewed.
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { writeFileSync } from 'node:fs';
import { performance } from 'node:perf_hooks';

const root = resolve('server/ZaloBridge/node_modules/.cache/nlu-bench');
const { pipeline, env } = await import(pathToFileURL(join(root, 'node_modules/@huggingface/transformers/dist/transformers.node.mjs')).href);
env.allowRemoteModels = false;
env.allowLocalModels = true;
env.localModelPath = join(root, 'models');
const mib = x => Math.round(x / 1048576 * 100) / 100;
const sample = [];
const ticker = setInterval(() => sample.push(process.memoryUsage().rss), 5);
const before = process.memoryUsage().rss;
const start = performance.now();
try {
  const extractor = await pipeline('feature-extraction', 'Xenova/multilingual-e5-small', { device:'cpu', dtype:'q8' });
  const modelLoadMs = performance.now() - start;
  const afterLoad = process.memoryUsage().rss;
  const times = [];
  for (const text of [
    'query: Tôi muốn chung team với To An hôm nay',
    'query: I would like to play on the same side as To An today',
    'query: 오늘 To An 님하고 같은 팀으로 해 주세요',
    'query: 我想和 To An 在同一个队',
    'query: Je voudrais faire équipe avec To An ce soir',
  ]) {
    const t0 = performance.now();
    const tensor = await extractor(text, { pooling: 'mean', normalize: true });
    times.push({text, ms:Math.round((performance.now()-t0)*100)/100, shape:tensor.dims});
    tensor.dispose?.();
  }
  const report = {
    runtime:'Node.js @huggingface/transformers CPU q8 single request',
    baselineMiB:mib(before),
    afterModelLoadMiB:mib(afterLoad),
    peakMiB:mib(Math.max(process.memoryUsage().rss, ...sample)),
    peakExtraMiB:mib(Math.max(process.memoryUsage().rss, ...sample)-before),
    modelLoadMs:Math.round(modelLoadMs),
    samples:times
  };
  const output = join(root, 'node-e5-report.json');
  writeFileSync(output, JSON.stringify(report, null, 2), 'utf8');
  console.log(JSON.stringify(report, null, 2));
} finally {
  clearInterval(ticker);
}
