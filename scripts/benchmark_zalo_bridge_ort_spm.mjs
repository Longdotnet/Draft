#!/usr/bin/env node
// Isolated, offline E5 CPU benchmark using a lightweight SentencePiece WASM
// tokenizer and native onnxruntime-node. NOT an operational chat endpoint.
import { resolve, join } from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { writeFileSync } from 'node:fs';
import { performance } from 'node:perf_hooks';

const root = resolve('server/ZaloBridge/node_modules/.cache/nlu-bench');
const require = createRequire(join(root, 'package.json'));
const ort = require('onnxruntime-node');
const { SentencePieceProcessor } = await import(
  pathToFileURL(join(root, 'node_modules/@sctg/sentencepiece-js/dist/index.js')).href
);
const mib=()=>process.memoryUsage().rss/1048576;
const sampled=[];
const timer=setInterval(()=>sampled.push(mib()),5);
const start=performance.now(), baseline=mib();
try{
  const spm=new SentencePieceProcessor();
  await spm.load(join(root,'models/Xenova/multilingual-e5-small/sentencepiece.bpe.model'));
  const afterTokenizer=mib();
  const session=await ort.InferenceSession.create(
    join(root,'models/Xenova/multilingual-e5-small/onnx/model_quantized.onnx'),
    {executionProviders:['cpu'],intraOpNumThreads:1,interOpNumThreads:1,
     enableCpuMemArena:false,enableMemPattern:false,graphOptimizationLevel:'all'}
  );
  const afterSession=mib(), timings=[];
  for(const question of [
    'query: Tôi muốn chung team với To An hôm nay',
    'query: I would like to play on the same side as To An today',
    'query: 오늘 To An 님하고 같은 팀으로 해 주세요',
    'query: 我想和 To An 在同一个队',
    'query: Je voudrais faire équipe avec To An ce soir',
  ]){
    const ids=[0,...spm.encodeIds(question).slice(0,62).map(id=>id+1),2];
    const dims=[1,ids.length];
    const int64=(arr)=>new BigInt64Array(arr.map(BigInt));
    const feeds={
      input_ids:new ort.Tensor('int64',int64(ids),dims),
      attention_mask:new ort.Tensor('int64',int64(ids.map(()=>1)),dims),
      token_type_ids:new ort.Tensor('int64',int64(ids.map(()=>0)),dims),
    };
    const t0=performance.now();
    const output=await session.run(feeds);
    const tensor=output[session.outputNames[0]];
    timings.push({question,ms:Math.round((performance.now()-t0)*100)/100,shape:tensor.dims,tokens:ids.length});
    for(const item of Object.values(output))item.dispose?.();
  }
  const peak=Math.max(mib(),...sampled);
  const result={runtime:'Native ONNX CPU + SentencePiece WASM; isolated Node benchmark',
    baselineMiB:baseline,afterTokenizerMiB:afterTokenizer,afterSessionMiB:afterSession,
    peakMiB:peak,addedPeakMiB:peak-baseline,initMs:performance.now()-start,timings};
  writeFileSync(join(root,'node-ort-spm-report.json'),JSON.stringify(result,null,2));
  console.log(JSON.stringify(result,null,2));
}finally{clearInterval(timer)}
