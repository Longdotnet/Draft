#!/usr/bin/env node
// Read-only alternative: ONNX Runtime native binding, avoiding Pipeline/model abstractions.
import { resolve, join } from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { writeFileSync } from 'node:fs';
import { performance } from 'node:perf_hooks';

const root = resolve('server/ZaloBridge/node_modules/.cache/nlu-bench');
const require = createRequire(join(root, 'package.json'));
const ort = require('onnxruntime-node');
const { AutoTokenizer, env } = await import(pathToFileURL(join(root,'node_modules/@huggingface/transformers/dist/transformers.node.mjs')).href);
env.allowRemoteModels=false;
env.allowLocalModels=true;
env.localModelPath=join(root,'models');
const mem=()=>process.memoryUsage().rss/1048576;
const baseline=mem();
const samples=[];
const sampler=setInterval(()=>samples.push(mem()),5);
try {
  const start=performance.now();
  const tokenizer=await AutoTokenizer.from_pretrained('Xenova/multilingual-e5-small',{local_files_only:true});
  const afterTokenizer=mem();
  const session=await ort.InferenceSession.create(
    join(root,'models/Xenova/multilingual-e5-small/onnx/model_quantized.onnx'),
    {executionProviders:['cpu'],intraOpNumThreads:1,interOpNumThreads:1,
     enableCpuMemArena:false,enableMemPattern:false,graphOptimizationLevel:'all'}
  );
  const afterSession=mem();
  const timings=[];
  for(const question of [
    'query: Tôi muốn chung team với To An hôm nay',
    'query: I would like to play on the same side as To An today',
    'query: 오늘 To An 님하고 같은 팀으로 해 주세요',
    'query: 我想和 To An 在同一个队',
    'query: Je voudrais faire équipe avec To An ce soir',
  ]) {
    const tokens=await tokenizer(question,{padding:true,truncation:true,max_length:64});
    const feeds={};
    for(const name of session.inputNames){
      if(name==='token_type_ids') feeds[name]=new ort.Tensor('int64',new BigInt64Array(tokens.input_ids.data.length),tokens.input_ids.dims);
      else {
        const t=tokens[name];
        feeds[name]=new ort.Tensor('int64',BigInt64Array.from(t.data,BigInt),t.dims);
      }
    }
    const t0=performance.now();
    const output=await session.run(feeds);
    timings.push({question,ms:Math.round((performance.now()-t0)*100)/100,shape:output[session.outputNames[0]].dims});
    for(const t of Object.values(output))t.dispose?.();
  }
  const report={
    baselineMiB:baseline,afterTokenizerMiB:afterTokenizer,afterSessionMiB:afterSession,
    peakMiB:Math.max(mem(),...samples),addedPeakMiB:Math.max(mem(),...samples)-baseline,
    initMs:performance.now()-start,timings
  };
  writeFileSync(join(root,'node-ort-direct-report.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify(report,null,2));
}finally{clearInterval(sampler);}
