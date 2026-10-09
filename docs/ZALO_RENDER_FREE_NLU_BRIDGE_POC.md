# PoC NPC đa ngôn ngữ trên Zalo Bridge Render Free — 09/10/2026

**Trạng thái: đã chạy local, chưa deploy, chưa quyết định nghiệp vụ.** Mục tiêu là dùng service Zalo Bridge hiện có, không tạo thêm server/không gọi API AI trả phí.

## Tài nguyên từ Render Metrics

| Chỉ số | Draft API | Zalo Bridge |
|---|---:|---:|
| RAM limit | 512 MiB | 512 MiB |
| RAM thực đo | 330–340 MiB | 78–86 MiB |
| CPU limit | 0.15 | 0.15 |
| CPU thực đo | 0.05–0.063 | 0.0001–0.0028 |

E5-small int8 **không đủ headroom trong Draft API**, nhưng Bridge có triển vọng.

## Benchmark Node.js trên Windows (5 câu VI/EN/KO/ZH/FR)

| Cấu hình | Baseline RSS | Peak RSS | RAM tăng thêm |
|---|---:|---:|---:|
| transformers.js + tokenizer JSON | 70.27 MiB | 464.91 MiB | 394.64 MiB |
| Native ONNX + tokenizer JSON | 70.18 MiB | 457.29 MiB | 387.10 MiB |
| **Native ONNX + SentencePiece WASM** | **66.64 MiB** | **259.20 MiB** | **192.55 MiB** |

Native ONNX + SentencePiece WASM trên máy dev: 10–13 ms/câu; init khoảng 1.5 giây. Module TypeScript tích hợp local (5 câu) dùng khoảng 263 MiB RSS với 384 chiều embedding, norm ~1, inference ~19–23 ms.

**Ngoại suy Render Bridge**: ~80 MiB đang dùng + ~193 MiB RAM tăng thêm ≈ 273 MiB, còn khoảng 239 MiB đến giới hạn 512 MiB. Đây là **ước lượng**, không phải số đo cgroup Linux/CPU 0.15 trên Render; chỉ deploy shadow sau khi review. Không thể bảo đảm chi phí 0 nếu tài khoản vượt hạn mức giờ Free, outbound hoặc build minutes.

## Model được xác minh

- Xenova/multilingual-e5-small ONNX int8: 118,054,593 bytes; SHA256 4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c.
- sentencepiece.bpe.model: 5,069,051 bytes; SHA256 cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865.
- Hai file chỉ nằm trong thư mục Git-ignore. Script chuẩn bị tải có xác minh checksum, khi tự gọi npm run nlu:prepare, hoặc khi bật flag chuẩn bị model lúc build.

## Tích hợp an toàn

- Source: server/ZaloBridge/src/nlu/bridgeMultilingualEmbedding.ts. Lazy loader; chỉ ONNX CPU một luồng; giới hạn 500 ký tự và 64 tokens; memory guard 410 MiB; 1 inference một thời điểm.
- Endpoint POST /v1/nlu/embed: sau middleware x-internal-key, **disabled by default**; chỉ trả embedding 384 floats, tokenCount, inferenceMs. Không ghi database/không đổi trạng thái Zalo/không sử dụng AI provider.
- Build: npm run build vẫn compile như cũ; bước model preparation tự bỏ qua nếu chưa bật ZALO_BRIDGE_NLU_ENABLED=true. Có thể chuẩn bị thủ công bằng npm run nlu:prepare; thư mục mặc định dist/nlu-model.
- Dockerfile build mặc định không tải weights: chỉ khi build với `--build-arg ZALO_BRIDGE_NLU_PREPARE=true` mới tải, xác minh checksum và copy model sang image runtime. Để thử NLU cần **cả** image có model **và** biến runtime `ZALO_BRIDGE_NLU_ENABLED=true`; chỉ đặt biến runtime không đủ. Nếu Render không cho truyền build arg qua cấu hình đang dùng thì cần thiết kế cơ chế chuẩn bị model trước khi bật, không cố bật flag.
- Unit tests: server/ZaloBridge/test/nluEmbedding.test.ts (disabled, kiểm tra input, thiếu model fail-closed). Bridge test suite kiểm tra toàn bộ ranh giới hiện hữu.

## Bảo vệ nghiệp vụ / những việc **chưa** làm

1. E5-small chỉ trả vector. Nó chưa trích xuất người chơi, ngày trận, phủ định, speech act thành JSON an toàn.
2. Baseline 45 câu có ba false-request candidates (zh-03, zh-07, fr-03); FastText model nhỏ cũng chưa đạt mức an toàn. Tuyệt đối không gắn cosine trực tiếp vào mutation/preview.
3. Production Bridge đang auto-deploy từ main. **Không push lên main hoặc bật flag** trước khi kiểm tra kịch bản rollout/rollback và đo CPU trên Render thật.
4. Khi thử Render: bật shadow, kiểm tra memory/CPU/latency và chức năng nhận gửi Zalo; rollback tức thì bằng tắt flag nếu có ảnh hưởng. Giao tiếp API -> Bridge cần đi qua internal key hiện hữu; chưa nối đường API này.
5. Phải xây và đánh giá bộ phân loại supervised với corpus đa dạng, các ca question/uncertain/double negation, rồi mới cân nhắc dùng trong bot. Sau đó vẫn cần backend ground UID/permission/session/state và confirmation trước lưu.

## Scripts tái lập (máy dev)

- scripts/benchmark_zalo_bridge_e5_node.mjs
- scripts/benchmark_zalo_bridge_ort_direct.mjs
- scripts/benchmark_zalo_bridge_ort_spm.mjs
- scripts/benchmark_zalo_multilingual_e5.py
- scripts/benchmark_zalo_multilingual_fasttext.py
- scripts/generate_zalo_multilingual_train_seed.py

Các file weights và report runtime trong server/VolleyDraft.Api.Tests/obj/bench-cache hoặc server/ZaloBridge/node_modules/.cache đều bị Git-ignore. Không nên commit binary model lớn.
