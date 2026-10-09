#!/usr/bin/env python3
"""Tiny CPU-only supervised multilingual NLU baseline, offline experiment.

Input is a human-reviewed JSONL corpus. A 5-fold split is applied to *examples*,
not to paraphrase families, so the results are exploratory, not production
certification. Training and inference touch local files only.

Runtime memory and model size are monitored with psutil. All outputs belong in
the ignored obj/bench-cache folder. Never route direct DB mutations from this
classifier: backend must validate/clarify/confirm business actions separately.
"""
import argparse
import json
import random
import statistics
import tempfile
import time
from pathlib import Path

import fasttext
import psutil


def train_and_evaluate(samples, output_dir, folds, epochs, nonrequest_repeat):
    outcome = []
    process = psutil.Process()
    before = process.memory_info().rss
    model_sizes = []
    max_rss = before
    latencies = []
    for k in range(folds):
        train = [x for i,x in enumerate(samples) if i % folds != k]
        test = [x for i,x in enumerate(samples) if i % folds == k]
        for task in ("intent", "speechAct"):
            path = output_dir / f"train-{k}-{task}.txt"
            path.write_text("".join(
                ("__label__" + row[task] + " " + row["text"].replace("\n", " ").strip() + "\n") *
                (nonrequest_repeat if task=="speechAct" and row["speechAct"]!="REQUEST" else 1)
                for row in train
            ), encoding="utf-8")
            model = fasttext.train_supervised(
                input=str(path), lr=0.8, epoch=epochs, wordNgrams=2,
                dim=16, bucket=20000, minn=2, maxn=5,
                thread=1, verbose=0, loss="softmax")
            model_path = output_dir / f"{k}-{task}.bin"
            model.save_model(str(model_path))
            model_sizes.append(model_path.stat().st_size)
            max_rss = max(max_rss, process.memory_info().rss)
            for row in test:
                start = time.perf_counter()
                labels, scores = model.predict(row["text"].replace("\n", " "), k=2)
                elapsed = (time.perf_counter()-start)*1000
                latencies.append(elapsed)
                outcome.append({
                    "id": row["id"], "language": row["language"], "task": task,
                    "expected": row[task], "predicted": labels[0].removeprefix("__label__"),
                    "confidence": float(scores[0]),
                    "runner_up": labels[1].removeprefix("__label__") if len(labels)>1 else None,
                    "margin": float(scores[0]-scores[1]) if len(scores)>1 else None,
                })
            del model
    max_rss = max(max_rss, process.memory_info().rss)
    results = {}
    for task in ("intent", "speechAct"):
        items=[x for x in outcome if x["task"]==task]
        total=len(items)
        correct=sum(x["expected"]==x["predicted"] for x in items)
        wrong=[x for x in items if x["expected"]!=x["predicted"]]
        results[task]={"correct":correct,"total":total,"accuracy":correct/total,"wrong":wrong}
    original = {x["id"]:x for x in samples}
    pred_speech={x["id"]:x["predicted"] for x in outcome if x["task"]=="speechAct"}
    false_requests=[id for id,v in pred_speech.items() if original[id]["speechAct"] != "REQUEST" and v=="REQUEST"]
    reject_eval={}
    for threshold in (0.60,0.70,0.80,0.90,0.95):
        accepted=[x for x in outcome if x["task"]=="speechAct" and
            x["predicted"]=="REQUEST" and x["confidence"]>=threshold]
        dangerous=[x["id"] for x in accepted if original[x["id"]]["speechAct"]!="REQUEST"]
        true_accepted=[x["id"] for x in accepted if original[x["id"]]["speechAct"]=="REQUEST"]
        reject_eval[str(threshold)]={"accepted_requests":len(accepted),"true_requests":len(true_accepted),"false_requests":dangerous}
    return {
        "warning": "5-fold on 45 seed examples, NOT independent language robustness testing.",
        "folds":folds, "epochs":epochs, "data_count":len(samples),"nonrequest_repeat":nonrequest_repeat,
        "ram_mib":{"baseline":round(before/1048576,2),"peak_training_inference":round(max_rss/1048576,2),
                   "additional_peak":round((max_rss-before)/1048576,2)},
        "model_bytes":{"min":min(model_sizes),"max":max(model_sizes)},
        "inference_ms":{"p50":statistics.median(latencies),"max":max(latencies)},
        "scores":results, "false_request_ids":false_requests, "thresholds":reject_eval,
    }


def independent_holdout(train, test, output_dir, epochs, nonrequest_repeat):
    """Train once using a separate corpus; never mix evaluation examples into training."""
    process = psutil.Process()
    baseline = process.memory_info().rss
    predictions = {}
    model_bytes = {}
    latencies = []
    max_memory = baseline
    for task in ("intent", "speechAct"):
        path = output_dir / f"train-holdout-{task}.txt"
        path.write_text("".join(
            ("__label__" + row[task] + " " + row["text"].replace("\n", " ").strip() + "\n") *
            (nonrequest_repeat if task=="speechAct" and row["speechAct"]!="REQUEST" else 1)
            for row in train
        ), encoding="utf-8")
        model = fasttext.train_supervised(input=str(path),lr=0.5,epoch=epochs,
            wordNgrams=2,dim=16,bucket=20000,minn=2,maxn=5,thread=1,verbose=0)
        output = output_dir / f"trained-{task}.bin"
        model.save_model(str(output))
        model_bytes[task] = output.stat().st_size
        max_memory = max(max_memory,process.memory_info().rss)
        for row in test:
            start=time.perf_counter()
            labels,scores=model.predict(row["text"],k=2)
            latencies.append((time.perf_counter()-start)*1000)
            predictions[(row["id"],task)]=(labels[0].removeprefix("__label__"),float(scores[0]))
        del model
    scores = {}
    for task in ("intent","speechAct"):
        wrong=[{"id":row["id"],"language":row["language"],"expected":row[task],
                "predicted":predictions[(row["id"],task)][0],"confidence":predictions[(row["id"],task)][1]}
               for row in test if row[task]!=predictions[(row["id"],task)][0]]
        scores[task]={"correct":len(test)-len(wrong),"total":len(test),"wrong":wrong}
    risky=[row["id"] for row in test if row["speechAct"]!="REQUEST"
           and predictions[(row["id"],"speechAct")][0]=="REQUEST"]
    accepted={}
    for threshold in (0.6,0.7,0.8,0.9,0.95):
        ids=[row["id"] for row in test if predictions[(row["id"],"speechAct")][0]=="REQUEST"
             and predictions[(row["id"],"speechAct")][1]>=threshold]
        accepted[str(threshold)]={"accepted":len(ids),"true":sum(row["id"] in ids and row["speechAct"]=="REQUEST" for row in test),
                                 "false": [row["id"] for row in test if row["id"] in ids and row["speechAct"]!="REQUEST"]}
    return {"evaluation_type":"independent seed holdout, separate manually authored train templates",
            "train_count":len(train),"test_count":len(test),"epochs":epochs,"nonrequest_repeat":nonrequest_repeat,
            "model_bytes":model_bytes,
            "peak_train_mib":round(max_memory/1048576,2),
            "peak_extra_over_baseline_mib":round((max_memory-baseline)/1048576,2),
            "inference_ms":{"p50":statistics.median(latencies),"max":max(latencies)},
            "scores":scores,"false_request_ids":risky,"thresholds":accepted}


def main():
    p=argparse.ArgumentParser()
    p.add_argument("--eval",type=Path,required=True)
    p.add_argument("--output",type=Path,required=True)
    p.add_argument("--folds",type=int,default=5)
    p.add_argument("--epochs",type=int,default=35)
    p.add_argument("--nonrequest-repeat",type=int,default=1)
    p.add_argument("--train",type=Path,help="Separate supervised training data; 45 golden eval lines stay held out")
    args=p.parse_args()
    samples=[json.loads(s) for s in args.eval.read_text(encoding="utf-8").splitlines() if s.strip()]
    if not args.train: random.Random(42).shuffle(samples)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="fasttext-",dir=args.output.parent) as tmp:
        if args.train:
            training=[json.loads(s) for s in args.train.read_text(encoding="utf-8").splitlines() if s.strip()]
            if set(x["text"] for x in training) & set(x["text"] for x in samples):
                p.error("Train and eval contain an identical sentence")
            report=independent_holdout(training,samples,Path(tmp),args.epochs,args.nonrequest_repeat)
        else:
            report=train_and_evaluate(samples,Path(tmp),args.folds,args.epochs,args.nonrequest_repeat)
    args.output.write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding="utf-8")
    print(json.dumps({k:report.get(k) for k in ("ram_mib","model_bytes","inference_ms","false_request_ids")},ensure_ascii=False,indent=2))
    for task, result in report["scores"].items():
        print(task,result["correct"],"/",result["total"])
    print("thresholds:",json.dumps(report["thresholds"],ensure_ascii=False))
    print("report",args.output)

if __name__=="__main__":
    main()
