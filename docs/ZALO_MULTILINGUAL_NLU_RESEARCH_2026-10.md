# VolleyDraft NPC — nghiên cứu tầng NLU đa ngôn ngữ (09/10/2026)

## Mục tiêu / phạm vi

- Hiểu lệnh bằng tiếng Việt, Anh, Hàn, Trung, Pháp và câu code-switch; không yêu cầu phải khớp danh sách keyword nghiệp vụ để nhận diện intent.
- AI/model chỉ suy luận `intent`, `operation`, `relation`, `speechAct`, ngôn ngữ và tham chiếu thực thể; **backend** xác minh UID người chơi, trận, quyền, xung đột, state token, lưu pending confirmation. Không bao giờ ghi DB trực tiếp từ output AI.
- Khi semantic model không sẵn sàng, không chuyển yêu cầu nghi là lệnh thay đổi dữ liệu sang GeneralChat rồi coi nội dung AI nói là kết quả nghiệp vụ. Chỉ xử lý các lệnh deterministic chắc chắn; nếu không chắc thì hỏi lại, không mutate.
- Nghiên cứu + benchmark trước, chưa bật trên production hoặc thay thế router hiện tại.

## Hiện trạng có kiểm chứng

- `server/VolleyDraft.Api`: .NET 9, đã có `Microsoft.ML.OnnxRuntime` 1.28.0, nên có thể thử ONNX CPU mà không thay framework backend.
- `ZaloBotIntelligence.ClassifyDeterministically` dùng rule/regex cho một số intent; nhánh `Unknown` dùng `AiAssistantService.ClassifyAsync` và sau đó có thể rơi vào `GeneralChat`.
- `ZaloTeamPreferenceSemanticExtractor` đã có schema operation/relation/speechAct/players/session và quy tắc grounded, nhưng dùng provider AI ngoài; không hoạt động độc lập khi hết quota.
- Render `Draft` API là Web Service free. **Render Metrics lúc 14:26 UTC 09/10/2026 trả `cpu_limit=0.15 CPU` và `memory_limit≈512 MiB`**, trong đó API đang dùng khoảng 330–340 MiB. Auto deploy hiện tắt. Không đặt LLM >=0.6B lên cùng tiến trình API production này mà chưa kiểm tra RSS và latency.
- Log production 09/10/2026 10:29 UTC: intent classifier và general chat đi qua provider chính trả `402 insufficient_quota`; các fallback trả HTTP 200 nhưng completion rỗng/không đúng format -> `InvalidResponse`. Đây là vấn đề provider + route, không thể chỉ sửa mẫu trả lời.

## Các model được research

| Model | Tác vụ phù hợp | Kích thước/khả năng | License | Đánh giá cho NPC |
|---|---|---|---|---|
| [intfloat/multilingual-e5-small](https://huggingface.co/intfloat/multilingual-e5-small) | Embedding đa ngôn ngữ; encoder cho classifier intent | ~118M parameters; ONNX int8 ~118 MB (`Xenova/multilingual-e5-small`). Hỗ trợ `vi,en,ko,zh,fr` cùng nhiều tiếng khác | MIT | **Ứng viên V0 đầu tiên.** Train/calibrate đầu phân loại intent trên embeddings; KHÔNG tự trích xuất tên/ngày hoặc xử lý phủ định an toàn nếu chỉ dùng cosine. |
| [Qwen/Qwen3.5-0.8B](https://huggingface.co/Qwen/Qwen3.5-0.8B) | Semantic JSON extraction/đối thoại local | ~873M params; bản GGUF Q4_K_M tùy quantizer ~500–560 MB chỉ tính weights, chưa tính KV cache/runtime | Apache-2.0 | **Ứng viên V1 trên máy riêng có RAM dư** hoặc service >=2 GB (cần benchmark); không nhúng Render free 512 MB. |
| [Qwen/Qwen3-0.6B](https://huggingface.co/Qwen/Qwen3-0.6B) | Small generative NLU | ~752M params, Apache-2.0 | Apache-2.0 | Baseline so với Qwen3.5, kiểm thử Structured JSON và tiếng Hàn/Việt riêng trước khi chọn. |
| [google/gemma-3-270m-it](https://huggingface.co/google/gemma-3-270m-it) | Small generative classifier/extractor | 270M parameters; rất nhỏ nhưng chưa có benchmark NPC đa ngôn ngữ | Gemma license (đọc điều khoản trước khi triển khai) | Thử benchmark nếu cần giảm RAM; không mặc định tin JSON / negation. |
| [BAAI/bge-m3](https://huggingface.co/BAAI/bge-m3) | Embedding đa ngôn ngữ cho RAG / similarity | 1024-dim, >=100 languages, nặng hơn E5-small | MIT | Chưa phù hợp API RAM thấp; giữ làm baseline chất lượng trên máy dev. |
| [fastText lid.176.ftz](https://fasttext.cc/docs/en/language-identification) | Chỉ nhận diện ngôn ngữ | ~917 KB, 176 ngôn ngữ | CC BY-SA 3.0 (kiểm tra nghĩa vụ phân phối) | Có thể làm language-id độc lập; **không hiểu lệnh**. Không suy ngôn ngữ từ Unicode có dấu vì tiếng Pháp dễ nhầm tiếng Việt. |

Nguồn hạ tầng: [Render Compute Plans](https://render.com/docs/compute-plans), [Render Free limits](https://render.com/docs/free); nguồn dung lượng [ONNX int8 118 MB](https://huggingface.co/Xenova/multilingual-e5-small/tree/main/onnx), [Qwen GGUF Q4](https://huggingface.co/mradermacher/Qwen3.5-0.8B-GGUF). Dung lượng file != peak RSS lúc inference; phải đo trên container thực.

## Kiến trúc mục tiêu

1. **Preflight**: chỉ xử lý deterministic command rõ nghĩa/legacy controls; nếu không chắc, không mutate.
2. **Local multilingual encoder** (V0): E5-small ONNX -> đầu phân loại được supervised/calibrated: `GeneralChat | TeamPreference | ShareSlot | Other`, tiếp tục `SET_TOGETHER | SET_APART | CLEAR | QUERY | SUGGESTION/UNCERTAIN`, confidence+margin+OOD flag. Embedding similarities với ví dụ chỉ dùng làm candidate retrieval, không biến trực tiếp thành quyết định ghi DB.
3. **Entity & action extractor**: dùng LLM structured JSON khi provider hoạt động; fallback offline chỉ chấp nhận cặp SELF + UID @mention và session match duy nhất mà hệ thống xác minh chắc chắn. Nếu thiếu tên, ngày, phủ định hoặc mục đích không rõ -> `NeedsClarification`, không tạo pending mutation.
4. **Verifier**: schema strict; UID/mentioned user grounding; session resolve (chỉ đọc DB); scope quyền, team constraint, negation/double negation, confidence; freeze expected state trước confirm; confirmation mới thực thi.
5. **Reply language**: lưu BCP-47/ISO language code; không giới hạn enum 3 giá trị. Các câu nghiệp vụ cố định dùng catalog VI/EN/KO/ZH/FR ban đầu; câu ngoài catalog hoặc không chắc ngôn ngữ dùng lời hỏi lại an toàn, KHÔNG auto default tiếng Việt. Khi LLM khả dụng, có thể rewrite nhưng không cho phép đổi số liệu/tên người.
6. **Failure modes**: quota/external 200-empty -> circuit breaker/cooldown, skip known-unhealthy provider; không gọi cả intent+general chat rồi gửi lỗi vô nghĩa cho một lệnh nghiệp vụ. Giữ trace `route, language, confidence, provider, grounded, awaiting_confirm` (không log API key/private conversation đầy đủ).

## Cách phát triển model chứ không chỉ prompt

- Pha A: thu tập dữ liệu nhãn có review, >=200 câu mỗi ngôn ngữ, chia train/dev/test tách theo người viết/paraphrase; bao gồm Việt không dấu, ngôn ngữ lẫn, phủ định kép, lời dẫn trích, câu hỏi vs yêu cầu, nhiều mentions, tên chứa dấu, ngày/thứ; không dùng dữ liệu Zalo riêng tư ngoài consent.
- Pha B: chạy E5-small int8 qua ONNX, mean pooling + normalize đúng theo model card, tiền tố `query:`/`passage:` cho ứng dụng matching; train logistic regression/ML.NET classifier hoặc linear head trên vector. Đây mới là **model NLU domain được huấn luyện**. Báo model version, data hash, threshold và confusion matrix từng ngôn ngữ.
- Pha C: so với zero-shot prompt/Qwen3.5-0.8B GGUF trên máy dev. Benchmark end-to-end trên CPU thật (warm/cold latency, RSS, CPU, structured JSON pass rate, entity grounding, false mutation). Không khẳng định chất lượng khi chưa benchmark.
- Pha D: shadow mode trên server, chỉ ghi kết quả routing và so với classifier cũ, không thay đổi nghiệp vụ; feature flag + rollback. Chỉ promote khi gate bên dưới pass.

## Acceptance gates đề xuất (targets, KHÔNG phải kết quả đo)

- Multi-language intent macro-F1 >= 0.93 và mỗi ngôn ngữ F1 >= 0.90 trên held-out >=200 câu/ngôn ngữ.
- Phân biệt `Request` vs `Question/Suggestion/Uncertain` >= 0.98 recall trên tập phủ định/ambiguity; **0 false-positive mutation** trong 1.000 câu adversarial kiểm thử. Đây là gate bắt buộc để bật đường auto-preview.
- Entity grounding và session chọn đúng 100% với bộ test có UID/date labels; không chắc => clarify (không tự đoán nearest session).
- JSON schema 100% sau validator (invalid output -> fail closed, không repair thành mutation bằng regex suy đoán).
- RSS peak dưới hạn mức RAM container còn dư sau tải API thực và P95 warm latency trong SLO riêng; giới hạn cụ thể xác định sau đo, không lấy kích thước ONNX file thay RSS.
- Không gọi external provider khi đang circuit-open; mọi fallback an toàn bảo toàn câu ngôn ngữ gốc nếu nhận diện đủ chắc.

## Benchmark thực tế

Xem [ZALO_MULTILINGUAL_NLU_RENDER_BENCHMARK_2026-10.md](ZALO_MULTILINGUAL_NLU_RENDER_BENCHMARK_2026-10.md). Đã chạy benchmark local CPU Windows với E5-small ONNX int8, cả tokenizer JSON và SentencePiece; **chưa đủ ngân sách RAM để bật trên Render Draft Free hiện tại**. Lưu ý báo cáo này phân biệt rõ mô hình chưa huấn luyện và độ chính xác baseline.

## Trạng thái 09/10/2026

**Research/thiết kế, corpus ban đầu, và benchmark E5 trên máy dev**, chưa train classifier thực sự, chưa benchmark trên đúng container Linux Render, chưa tích hợp vào production, chưa deploy. Model weights được lưu trong thư mục `obj` bị Git ignore. File `docs/evals/zalo-multilingual-nlu-v0.jsonl` chỉ là seed dữ liệu cho eval, không được dùng làm chứng cứ độ chính xác model hoặc train/test cùng lúc.

**Next real experiment**: tìm hoặc distill một encoder đa ngôn ngữ có incremental peak RSS thực dưới khoảng 70 MiB; train supervised classifier trên dữ liệu lớn/đa dạng độc lập; chạy trên Linux có memory/cpu cgroup sát Render Free; chặn merge nếu không đạt gate. Mô hình E5-small int8 bản thử **không đạt gate RAM, không được nhúng lên API production**.
