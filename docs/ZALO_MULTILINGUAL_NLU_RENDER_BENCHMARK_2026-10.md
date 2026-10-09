# Kiểm thử NLU đa ngôn ngữ cho NPC trên Render Free — 09/10/2026

## Kết luận

**NO-GO: không nhúng E5-small ONNX int8 cùng tiến trình `Draft` API Render Free hiện tại.** Đây là kết luận thiết kế dựa trên ngân sách RAM thực và benchmark cô lập trên Windows; **chưa phải** phép đo trên cùng container/cgroup Linux của Render. Không có code runtime mới, không chỉnh service hoặc database production.

Render service `Draft` đang `autoDeploy=off`. Cần benchmark và đạt các gate dưới đây trước khi triển khai một mô hình khác.

## Render thực tế, đo từ Render Metrics

- Resource: `srv-d8pr2frtqb8s738ficl0` (service `Draft`), khoảng thời gian 2026-10-09 13:26–14:26 UTC, điểm cách nhau 5 phút.
- Memory limit: **536,870,900 bytes ≈ 512 MiB**.
- Memory usage: **~346–356 MB** thập phân (khoảng **330–340 MiB**); lớn nhất **356,057,100 bytes ≈ 339.56 MiB**.
- Headroom tại điểm cao nhất: **180,813,800 bytes ≈ 172.44 MiB** **trước** dự phòng cho request spikes / GC / background workers.
- CPU limit: **0.15 CPU**. CPU usage thường **~0.050–0.063 CPU**; không thể suy trực tiếp p95 latency từ máy dev nhiều lõi.

## Mô hình và dữ liệu thử

- Repository: [Xenova/multilingual-e5-small](https://huggingface.co/Xenova/multilingual-e5-small), ONNX int8, file `onnx/model_int8.onnx` **118,054,593 bytes**, SHA-256 **`4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c`** (trùng HF `X-Linked-ETag`).
- So sánh tokenizer `tokenizer.json` 17,082,730 bytes với `sentencepiece.bpe.model` 5,069,051 bytes. Với SentencePiece, map token piece ID sang HF ID +1 và thêm `<s>=0`, `</s>=2`; không đưa tên người thực qua mạng.
- Chạy **onnxruntime CPUExecutionProvider, một thread**, input có prefix `query:`, attention-mask mean pooling và normalized embedding.
- Evaluation seed: `docs/evals/zalo-multilingual-nlu-v0.jsonl`: 45 câu tự tạo, VI/EN/KO/ZH/FR mỗi ngôn ngữ 8 và 5 code-switch. `leave-one-out` cosine nearest-neighbor **không phải supervised model đã huấn luyện**, không có train/test độc lập.
- Benchmark chạy trên Windows máy dev, `Python 3.12 + onnxruntime + numpy + sentencepiece`, không mô phỏng được chính xác CPU 0.15 hay Linux cgroup. RSS dưới đây là **toàn bộ process Python bench**, không phải bộ nhớ resident của API .NET và không thể cộng chính xác từng byte với process API mà chưa profile chung.

## Kết quả đo thực

| Cấu hình | RSS baseline | RSS peak | RAM peak tăng thêm | Warm p95 Windows | Intent đúng | SpeechAct đúng | False SET risk |
|---|---:|---:|---:|---:|---:|---:|---:|
| ONNX int8 + HF `tokenizer.json`, max 64 | 52.05 MiB | **515.86 MiB** | **463.80 MiB** | 20.6 ms | 38/45 | 37/45 | 3 |
| ONNX int8 + SentencePiece, max 64 | 51.92 MiB | **296.45 MiB** | **244.53 MiB** | 21.1 ms | 38/45 | 38/45 | 3 |
| ONNX int8 + SentencePiece, max 64, repeat | 52.10 MiB | **301.50 MiB** | **249.40 MiB** | 20.5 ms | 38/45 | 38/45 | 3 |
| ONNX int8 + SentencePiece, max 32 | 51.81 MiB | **305.76 MiB** | **253.95 MiB** | 28.1 ms | 38/45 | 38/45 | 3 |
| ONNX int8 + SentencePiece, max 16 | 51.74 MiB | **294.45 MiB** | **242.70 MiB** | 22.5 ms | 37/45 | 26/45 | 4 |

RSS memory sample mỗi 10 ms bằng psutil; p95 chỉ là kết quả máy dev, **không phải SLO Render**. Số đo bị tác động bởi warm-up, scheduler và allocator (32-token có thể có peak cao hơn 64-token). Nguồn JSON đầy đủ ở local ignored `server/VolleyDraft.Api.Tests/obj/bench-cache/report/*.json` (không đưa model weights vào Git).

**Tại sao NO-GO?** Headroom production khoảng **172 MiB**, trong khi benchmark E5-small tối ưu hiện còn tăng peak **243–254 MiB** so với process baseline độc lập. Dự tính nhúng chung đã có nguy cơ vượt giới hạn **ít nhất khoảng 70–82 MiB trước khi tính dự phòng**. Không có bằng chứng cgroup Linux và CPU production đủ an toàn để chấp nhận rủi ro crash/OOM. Ngay cả nếu ép inference bằng streaming/memory tricks, chất lượng vẫn chưa đạt gate.

### Các lỗi semantic nguy hiểm

Baseline cosine nearest-neighbor có false SET risk từ các ID:

- `zh-03`: người dùng hỏi *có thể cùng đội không?*, model gán speechAct REQUEST.
- `zh-07`: lời chào tiếng Trung bị gán TeamPreference/REQUEST.
- `fr-03`: người dùng Pháp hỏi *có thể cùng đội không?*, model gán REQUEST.

Đây là lỗi **phân loại** của thử nghiệm chứ chưa có backend action. Tuyệt đối **không** gắn trực tiếp output cosine vào mutation/preview; classifier supervised phải được huấn luyện và đánh giá trên corpus mới có nhãn, có tập holdout independent và reject/OOD thresholds.

## Lặp lại benchmark (máy dev Windows, từ repo root)

Không tải model vào thư mục production hoặc commit weight. Các lệnh sau thao tác trong `obj`, đã Git-ignore:

```powershell
$cache = "server/VolleyDraft.Api.Tests/obj/bench-cache"
New-Item -ItemType Directory -Force -Path "$cache/pkgs", "$cache/model" | Out-Null
python -m pip install --target "$cache/pkgs" numpy onnxruntime sentencepiece psutil
curl.exe -fL --retry 2 -o "$cache/model/model_int8.onnx" https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model_int8.onnx
curl.exe -fL --retry 2 -o "$cache/model/sentencepiece.bpe.model" https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/sentencepiece.bpe.model
Get-FileHash "$cache/model/model_int8.onnx" -Algorithm SHA256
$env:PYTHONPATH = (Resolve-Path "$cache/pkgs").Path
python scripts/benchmark_zalo_multilingual_e5.py --model "$cache/model/model_int8.onnx" --spm-model "$cache/model/sentencepiece.bpe.model" --eval docs/evals/zalo-multilingual-nlu-v0.jsonl --output "$cache/report/e5-int8-spm-local.json" --max-tokens 64 --rounds 3
```

## Quyết định/đường phát triển tiếp theo nếu giữ *đúng* Render Free hiện tại

1. **Không** dùng Qwen/Gemma hoặc E5-small cùng process API Draft; CPU 0.15 cũng không phù hợp generative model theo request.
2. Nghiên cứu và đo một **encoder đã distill <=~30–50 MiB RAM tăng thêm** (không chỉ kích thước file), inference p95 trong SLO trên Linux container có cgroup 512MiB/0.15 CPU. Không có model cụ thể nào đã chứng minh pass gate này ở thời điểm báo cáo; phải benchmark trước khi tuyên bố khả thi.
3. Nâng chất lượng dữ liệu trước khi train: tối thiểu 200 câu/ngôn ngữ held-out riêng và nhiều ví dụ request/query/double negation/code-switch; train calibrated supervised intent/speech-act head thay vì dùng cosine nearest-neighbor để điều khiển business logic.
4. Backend vẫn phải fail closed, chỉ parse mention UID và session có chứng cứ, preview+confirm; yêu cầu chưa chắc chắn không được rơi xuống GeneralChat rồi trình bày như đã đổi dữ liệu.
5. Nếu không tìm thấy encoder siêu nhỏ đạt memory+quality gates, **báo NO-GO cho yêu cầu "offline semantic NLP không phụ thuộc AI bên ngoài ngay trên Render Free hiện tại"** chứ không dùng regex/keyword vá vòng hoặc tự tăng plan/bật deploy.

**Trạng thái:** benchmark + tài liệu và script có thể tái lập, *không có* triển khai hoặc thay đổi runtime API/Bridge/production DB. Cần benchmark trên Linux cgroup và model siêu nhỏ sau khi lựa chọn ứng viên mới.
