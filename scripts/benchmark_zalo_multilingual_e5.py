#!/usr/bin/env python3
"""Offline, read-only E5-small ONNX benchmark for VolleyDraft.

Dependencies (install in an ignored project obj folder, never into API image):
    pip install --target server/VolleyDraft.Api.Tests/obj/bench-cache/pkgs numpy onnxruntime psutil sentencepiece
    # Install tokenizers only for the high-memory tokenizer.json comparison.

Run with PYTHONPATH pointing to pkgs and --model / --tokenizer pointing to local
files. Does not download weights, mutate DB, use API secrets, or access network.

Nearest-neighbor classification is a deliberately weak *baseline*, not a trained
intent model or a safe operational mutation policy.
"""

import argparse
import json
import math
import statistics
import threading
import time
import hashlib
from pathlib import Path

import numpy as np
import onnxruntime as ort
import psutil


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    index = (len(ordered) - 1) * fraction
    left = math.floor(index)
    right = math.ceil(index)
    return float(ordered[left] * (right - index) + ordered[right] * (index - left))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--model", required=True, type=Path)
    group = p.add_mutually_exclusive_group(required=True)
    group.add_argument("--tokenizer", type=Path, help="HF tokenizer.json (high-memory baseline)")
    group.add_argument("--spm-model", type=Path, help="sentencepiece.bpe.model (lightweight XLM-R tokenizer)")
    p.add_argument("--eval", required=True, type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--max-tokens", type=int, default=64)
    p.add_argument("--rounds", type=int, default=2)
    p.add_argument("--disable-arena", action="store_true", help="Disable ONNX Runtime native CPU arena")
    p.add_argument("--disable-mem-pattern", action="store_true", help="Disable ONNX Runtime memory pattern caching")
    p.add_argument("--disable-opt", action="store_true", help="Disable ONNX Runtime graph optimizations")
    args = p.parse_args()
    tokenizer_file = args.tokenizer or args.spm_model
    if not args.model.is_file() or not tokenizer_file.is_file():
        p.error("model/tokenizer files are required")
    records = [json.loads(line) for line in args.eval.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not records or args.rounds < 1:
        p.error("eval must have records and rounds must be positive")

    process = psutil.Process()
    samples = []
    keep_sampling = threading.Event()
    keep_sampling.set()

    def sample_rss():
        while keep_sampling.is_set():
            samples.append(process.memory_info().rss)
            time.sleep(0.01)

    threading.Thread(target=sample_rss, daemon=True).start()
    baseline_rss = process.memory_info().rss
    startup_start = time.perf_counter()
    if args.spm_model:
        import sentencepiece as spm
        sp = spm.SentencePieceProcessor(model_file=str(args.spm_model))
        # Xenova E5-small's XLM-R BPE vocabulary offsets SentencePiece pieces
        # by 1: HF tokenizer uses <s>=0, <pad>=1, </s>=2, <unk>=3.
        # Compare with tokenizer.json IDs before trusting a new model revision.
        def tokenize(text):
            pieces = [token + 1 for token in sp.encode("query: " + text, out_type=int)]
            return [0] + pieces[:args.max_tokens - 2] + [2]
        token_ids = [tokenize(row["text"]) for row in records]
    else:
        from tokenizers import Tokenizer
        tokenizer = Tokenizer.from_file(str(args.tokenizer))
        tokenizer.enable_truncation(max_length=args.max_tokens)
        tokenizer.enable_padding()
        token_ids = [enc.ids for enc in tokenizer.encode_batch(["query: " + row["text"] for row in records])]
    tokenizer_rss = process.memory_info().rss
    session_options = ort.SessionOptions()
    session_options.intra_op_num_threads = 1
    session_options.inter_op_num_threads = 1
    session_options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    session_options.graph_optimization_level = (
        ort.GraphOptimizationLevel.ORT_DISABLE_ALL if args.disable_opt
        else ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    )
    if args.disable_arena:
        session_options.enable_cpu_mem_arena = False
    if args.disable_mem_pattern:
        session_options.enable_mem_pattern = False
    session = ort.InferenceSession(str(args.model), sess_options=session_options, providers=["CPUExecutionProvider"])
    initialized = time.perf_counter()
    model_rss = process.memory_info().rss

    inputs = {inp.name: inp for inp in session.get_inputs()}
    model_inputs = sorted(inputs)
    longest = max(map(len, token_ids))
    token_ids = [ids + [1] * (longest - len(ids)) for ids in token_ids]
    embeddings = []
    timings = []
    first_ms = None

    def embed(ids):
        nonlocal first_ms
        t0 = time.perf_counter()
        input_ids = np.asarray([ids], dtype=np.int64)
        mask = np.asarray([[0 if token == 1 else 1 for token in ids]], dtype=np.int64)
        values = {"input_ids": input_ids, "attention_mask": mask, "token_type_ids": np.zeros_like(input_ids)}
        provided = {key: values[key] for key in inputs}
        outputs = session.run(None, provided)
        last = outputs[0]
        if last.ndim == 2:
            vector = last[0]
        elif last.ndim == 3:
            weights = mask[:, :, None].astype(np.float32)
            vector = (last * weights).sum(axis=1)[0] / np.maximum(weights.sum(axis=1)[0], 1)
        else:
            raise ValueError("Unexpected model output shape: " + str(last.shape))
        vector = np.asarray(vector, dtype=np.float32)
        vector /= max(np.linalg.norm(vector), 1e-12)
        elapsed = (time.perf_counter() - t0) * 1000
        if first_ms is None:
            first_ms = elapsed
        return vector, elapsed

    for round_index in range(args.rounds):
        for ids in token_ids:
            emb, elapsed = embed(ids)
            if round_index == 0:
                embeddings.append(emb)
            else:
                timings.append(elapsed)
    finished = time.perf_counter()

    matrix = np.stack(embeddings)
    similarity = matrix @ matrix.T
    np.fill_diagonal(similarity, -np.inf)
    predicted = [records[int(np.argmax(row))] for row in similarity]

    def scores_for(label_key):
        overall = sum(row[label_key] == pred[label_key] for row, pred in zip(records, predicted))
        per_language = {}
        for lang in sorted(set(row["language"] for row in records)):
            selected = [i for i, row in enumerate(records) if row["language"] == lang]
            per_language[lang] = {
                "total": len(selected),
                "correct": sum(records[i][label_key] == predicted[i][label_key] for i in selected),
            }
        return {"total": len(records), "correct": overall, "per_language": per_language}

    mistakes = [
        {
            "id": row["id"],
            "lang": row["language"],
            "actual_intent": row["intent"],
            "predicted_intent": pred["intent"],
            "actual_speech_act": row["speechAct"],
            "predicted_speech_act": pred["speechAct"],
            "neighbor": pred["id"],
            "cosine": round(float(similarity[i].max()), 4)
        }
        for i, (row, pred) in enumerate(zip(records, predicted))
        if row["intent"] != pred["intent"] or row["speechAct"] != pred["speechAct"]
    ]
    # Requests with uncertain/question ground truth that a naive classifier
    # labels as an actionable SET. In production ALL actions still require
    # downstream grounding, explicit user confirmation and authorization.
    false_mutation_risk = [
        row["id"] for row, pred in zip(records, predicted)
        if row["speechAct"] != "REQUEST" and pred["speechAct"] == "REQUEST"
        and pred["operation"] == "SET"
    ]

    keep_sampling.clear()
    with args.model.open("rb") as model_stream:
        model_sha256 = hashlib.file_digest(model_stream, "sha256").hexdigest()
    report = {
        "model": str(args.model),
        "model_bytes": args.model.stat().st_size,
        "model_sha256": model_sha256,
        "tokenizer_bytes": tokenizer_file.stat().st_size,
        "tokenizer_kind": "sentencepiece" if args.spm_model else "tokenizers-json",
        "model_inputs": model_inputs,
        "record_count": len(records),
        "threads": 1,
        "max_tokens": args.max_tokens,
        "encoded_length": longest,
        "rounds": args.rounds,
        "disable_arena": args.disable_arena,
        "disable_mem_pattern": args.disable_mem_pattern,
        "disable_opt": args.disable_opt,
        "benchmark_kind": "CPU single-thread offline Windows; not Render performance",
        "memory_mib": {
            "baseline": round(baseline_rss / 1048576, 2),
            "after_tokenizer": round(tokenizer_rss / 1048576, 2),
            "after_session": round(model_rss / 1048576, 2),
            "peak": round(max(samples + [process.memory_info().rss]) / 1048576, 2),
            "additional_peak_over_baseline": round((max(samples + [process.memory_info().rss]) - baseline_rss) / 1048576, 2)
        },
        "timing_ms": {
            "session_init": round((initialized - startup_start) * 1000, 1),
            "first_inference": round(first_ms, 1),
            "warm_p50": round(percentile(timings, 0.5), 1) if timings else None,
            "warm_p95": round(percentile(timings, 0.95), 1) if timings else None,
            "warm_max": round(max(timings), 1) if timings else None
        },
        "evaluation": {
            "warning": "45 manually seeded examples: LOOCV nearest neighbor, not trained, not held-out, NOT a production quality score.",
            "intent": scores_for("intent"),
            "speech_act": scores_for("speechAct"),
            "relation": scores_for("relation"),
            "false_mutation_risk": false_mutation_risk,
            "mistakes": mistakes
        }
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ["model_bytes", "record_count", "memory_mib", "timing_ms"]}, ensure_ascii=False, indent=2))
    print("intent", scores_for("intent")["correct"], "/", len(records))
    print("speechAct", scores_for("speechAct")["correct"], "/", len(records))
    print("false_mutation_risk", false_mutation_risk)
    print("report:", args.output)


if __name__ == "__main__":
    main()
