# Zalo Member Intelligence

## Mục tiêu và nguyên tắc

Member Intelligence trả lời các câu hỏi về mức độ hoạt động của thành viên từ dữ liệu Zalo đã đồng bộ vào PostgreSQL. AI chỉ nhận diện ý định, khoảng thời gian, người được hỏi và diễn đạt kết quả. Tên, UID, ngày, số poll, số tin và tỷ lệ đều do C# tính từ database.

Hệ thống không xem AI là database, không bịa message/poll/voter/timestamp và không gọi hàng chục API Zalo trong lúc người dùng đang chờ câu trả lời. Danh tính được khóa bằng `ZaloUserId`; đổi display name không tạo người mới.

Ứng dụng tự dùng mọi dữ liệu lịch sử mà tài khoản Zalo đang kết nối thực sự truy cập được. Coverage không bắt đầu từ ngày cài VolleyDraft hay ngày listener chạy.

## Flow đồng bộ

Khi một nhóm được liên kết và bật bot:

1. API tạo một `ZaloActivityBackfillJob` durable rồi trả HTTP ngay.
2. Background worker lấy lease trong database để chỉ một worker xử lý job.
3. Đồng bộ danh bạ thành viên hiện tại.
4. Quét tuần tự tất cả trang board mà Zalo trả về.
5. Với từng board poll, lấy poll detail và lưu option/voter UID.
6. Probe API lịch sử chat của thư viện Zalo hiện tại.
7. Nhập các message truy xuất được và gắn capability chính xác.
8. Tính lại coverage, hoàn tất job và lưu checkpoint.
9. Listener realtime tiếp tục lưu message mới.
10. Worker tự chạy incremental sync theo chu kỳ; event `update_board` cũng queue một lượt incremental.

Những group đã liên kết trước khi nâng cấp được worker phát hiện và tự tạo initial backfill. Admin không phải import từng poll để analytics hoạt động.

## Checkpoint, retry và concurrency

`ZaloActivityBackfillJobs` lưu stage/status, board page, fingerprint, message cursor/evidence, progress, coverage, lỗi/retry, lease và các timestamp chạy. Các stage gồm:

- `Queued`;
- `SyncingMembers`;
- `ScanningBoard`;
- `SyncingPollDetails`;
- `ProbingMessageHistory`;
- `ImportingMessages`;
- `RebuildingMetrics`;
- `Completed`.

Transient HTTP/timeout được retry theo exponential backoff có giới hạn. Poll lỗi không làm mất các poll đã lưu. Checkpoint được ghi sau mỗi page; restart tiếp tục từ page đã lưu. Unique index, transaction và database lease là lớp chống trùng/concurrency cuối cùng, không dựa riêng vào RAM.

## Đồng bộ thành viên

`ZaloGroupMembers` có unique key:

```text
ZaloConnectionId + GroupId + ZaloUserId
```

Mỗi lượt sync:

- upsert tên/avatar theo UID;
- giữ `FirstSeenAt`;
- cập nhật `LastSeenAt`, `LastSyncedAt`;
- người đang trả về có `IsCurrentMember=true`;
- chỉ đánh dấu người vắng mặt là former khi Zalo xác nhận directory trả về đầy đủ;
- không xóa lịch sử vote/message của người đã rời nhóm.

Báo cáo inactive mặc định chỉ dùng thành viên hiện tại. Người mới được gắn trạng thái `New`, không bị coi là inactive chỉ vì chưa kịp vote/chat.

## Đồng bộ board và poll

ZaloBridge dùng `getListBoard({ page, count }, groupId)` và tiếp tục tới khi đã lấy đủ reported total, page rỗng/ngắn/lặp hoặc chạm safety ceiling.

Mỗi poll được đọc lại bằng `getPollDetail`. Storage chuẩn hóa:

- `ZaloPollSnapshots`;
- `ZaloPollOptionSnapshots`;
- `ZaloPollVoteActivities`.

Poll ẩn danh, thiếu voter identities hoặc thiếu poll-created timestamp không được dùng cho analytics và có `ExclusionReason`. Một người chọn nhiều option vẫn chỉ tính là tham gia một poll. Từng option vẫn được lưu để thống kê `TotalSelectedOptions`. Khi voter đổi/bỏ lựa chọn, record cũ được giữ nhưng chuyển `IsCurrentlySelected=false` và ghi `RemovedObservedAt`.

`PollImport.ImportedAt` và `PlayerProfile.LastSyncedAt` không phải thời điểm người dùng vote.

## Timestamp và nghĩa của “vote gần nhất”

Các mốc khác nhau:

- `CreatedAtFromZalo`: lúc poll được tạo theo dữ liệu Zalo;
- `UpdatedAtFromZalo`: lúc poll được cập nhật;
- `FirstObservedAt`: lần đầu VolleyDraft quan sát poll/lựa chọn;
- `ExactUserVoteAt`: chỉ tồn tại nếu Zalo thật sự cung cấp timestamp theo voter.

`zca-js` hiện không cung cấp exact timestamp theo voter. Vì vậy bot nói:

> Poll gần nhất hệ thống tìm thấy A có tham gia là “...”, được tạo ngày ...

Bot không được nói “A bấm vote lúc ...”.

Với câu “ai 4 tháng rồi chưa vote?”, backend lấy:

```text
CurrentGroupMembers
MINUS
distinct ZaloUserId có trong poll hợp lệ được tạo trong khoảng 4 tháng lịch
```

“4 tháng” dùng `DateOnly.AddMonths(-4)`, không đổi thành 120 ngày.

## Lịch sử message và capability

Đã kiểm tra source/type declaration của `zca-js` 2.1.2 đang cài:

- có `getGroupChatHistory(groupId, count)`;
- response có `lastActionId`, `lastActionIdOther`, `more`, `groupMsgs`;
- public method không nhận cursor để yêu cầu trang cũ tiếp theo;
- không tìm thấy API search message theo group/sender/date trong public surface của phiên bản này.

ZaloBridge normalize message ID, sender UID, sent timestamp, message type và `isFromBot`. Capability được ghi bảo thủ:

- `FullHistoricalBackfill`: Zalo báo hết và message cũ nhất chạm thời điểm tạo group;
- `PartialHistoricalBackfill`: lấy được lịch sử nhưng chưa chứng minh đủ, hoặc `more > 0` mà thư viện không hỗ trợ cursor;
- `RealtimeOnly`: history call lỗi/không khả dụng, chỉ còn listener;
- `Unsupported`: chưa probe/chưa hỗ trợ;
- `SearchOnlyBackfill`: dành cho provider sau này nếu chỉ có search.

Nút search trong Zalo Web không tự động trở thành API. Không dùng browser automation hay đoán internal endpoint trong production. Nếu nâng `zca-js`, phải chạy capability probe và parsing test trước khi nâng capability.

Member Intelligence không trả nội dung chat; nó chỉ đọc sender UID, timestamp, count, active days và `IsFromBot`.

## Chỉ số hoạt động

Backend hỗ trợ:

- thành viên không vote/nhắn gần đây;
- hoạt động, poll và message gần nhất;
- tỷ lệ poll tham gia và tổng option;
- số poll bỏ lỡ liên tiếp;
- tỷ lệ nửa kỳ trước/nửa kỳ gần đây;
- trend deterministic;
- tổng quan group;
- phân trang.

Trạng thái gồm `New`, `Active`, `Regular`, `Occasional`, `AtRisk`, `Inactive`, `InsufficientData`. Các ngưỡng nằm trong `ZaloActivityRules`. AI có thể giải thích nhưng không tự tính trạng thái.

## Bot

Các intent mới:

- `ListMembersWithoutRecentVote`;
- `ListMembersWithoutRecentMessage`;
- `GetMemberLastActivity`;
- `GetMemberLastVote`;
- `GetMemberLastMessage`;
- `AnalyzeMemberVoteActivity`;
- `AnalyzeMemberMessageActivity`;
- `AnalyzeGroupEngagement`;
- `ListMostInactiveMembers`;
- `ListAtRiskMembers`;
- `SyncMemberActivity`;
- `GetActivitySyncStatus`.

`@bot 12` hỏi số lượng từ 1–30 rồi hiển thị danh sách kèm bằng chứng gần nhất. Danh sách mặc định 10 người/trang và hỗ trợ `tiếp`, `xem thêm`, `trang 2`, `trang trước`, `đầu`, `cuối`. State được scope theo connection + group + sender UID.

Tên được resolve theo UID mention, exact normalized name, tên không dấu, alias đã lưu, rồi mới fuzzy match bảo thủ. Nếu trùng, bot đưa lựa chọn đánh số và tiếp tục intent cũ sau câu trả lời `@bot 2`.

Khi backfill chưa xong, bot trả tiến độ thay vì trả danh sách có coverage sai.

## Authorization và privacy

Danh sách toàn nhóm, ranking và dữ liệu chi tiết của người khác chỉ dành cho UID operator, creator group hoặc admin/deputy group do Zalo trả về. Thành viên thường chỉ được hỏi hoạt động của chính UID gửi tin.

Quyền được xác minh bằng UID, không dùng display name. Nếu bridge không xác minh được role, bot từ chối an toàn và không rò tên. Module không tự kick, public shame, mass private message hoặc tự mention campaign.

## AI

AI được phép phân loại câu hỏi tiếng Việt, trích person/time/limit, diễn đạt factual answer và giải thích trend đã tính. AI không được sinh SQL/method/entity, sửa critical fact, bịa dữ liệu, bỏ qua quyền hoặc suy diễn tính cách/lý do một người ít tham gia.

Backend luôn tạo factual answer trước. Critical term được thay bằng placeholder bất biến khi gửi AI. Nếu AI đổi/bỏ placeholder, số hoặc ngày, rewrite bị loại và factual answer được gửi nguyên bản. AI lỗi/quota hết thì analytics vẫn chạy.

Đây là learned application knowledge và structured classification, không phải fine-tuning model.

## API và UI

Các endpoint đều yêu cầu JWT và kiểm tra session thuộc admin:

```text
POST /api/sessions/{sessionId}/member-intelligence/sync
GET  /api/sessions/{sessionId}/member-intelligence/sync
GET  /api/sessions/{sessionId}/member-intelligence/members
GET  /api/sessions/{sessionId}/member-intelligence/engagement
```

Panel Member Intelligence có nút sync/retry, stage/progress, coverage, filter no vote/no message 30/60/90 ngày/4 tháng, at-risk, bảng thành viên và phân trang/loading/error/empty state.

## Cấu hình

Các key .NET dùng format Render `__`:

```text
ZaloActivitySync__BoardPageSize=50
ZaloActivitySync__MaxBoardPages=100
ZaloActivitySync__IncrementalBoardPages=5
ZaloActivitySync__MessageHistoryCount=2000
ZaloActivitySync__RetryCount=4
ZaloActivitySync__RetryDelayMs=750
ZaloActivitySync__PauseBetweenRequestsMs=150
ZaloActivitySync__IncrementalMinutes=60
ZaloActivitySync__LeaseMinutes=10

ZaloActivityRules__NewMemberDays=14
ZaloActivityRules__ActiveDays=14
ZaloActivityRules__RegularDays=30
ZaloActivityRules__InactiveDays=90
ZaloActivityRules__AtRiskMissedPolls=3
```

Giữ nguyên `Zalo__BridgeBaseUrl`, `Zalo__BridgeInternalKey`, `Zalo__CredentialEncryptionKey`, `Zalo__WebhookUrl`, `Zalo__WebhookKey`. Không log cookie, IMEI, encryption key, API key hoặc database password.

## Chạy local và test

```powershell
dotnet build server/VolleyDraft.Api/VolleyDraft.Api.csproj
dotnet test server/VolleyDraft.Api.Tests/VolleyDraft.Api.Tests.csproj

cd server/ZaloBridge
npm ci
npm run build
npm test

cd ../..
npm ci
npm run build
```

## Render deployment

1. Deploy ZaloBridge mới trước và xác nhận `/health` trả 200.
2. Deploy API; startup schema patch tự tạo bảng/index và thêm cột message an toàn.
3. Không đổi `Zalo__CredentialEncryptionKey`.
4. Cấu hình env ở trên hoặc dùng default.
5. Deploy frontend.
6. Mở session đã liên kết, vào Member Intelligence và bấm đồng bộ.
7. Theo dõi log theo `JobId`, `ConnectionId`, `GroupId`, `Stage`.
8. Với group cũ, worker tự queue job trong tối đa khoảng 5 phút khi API đang chạy.

Render Free vẫn có thể ngủ. GitHub Actions scheduler hiện tại đánh thức API/bridge; job/checkpoint nằm trong PostgreSQL nên không mất tiến độ khi process restart.

## Checklist Zalo thật

1. Kết nối account và link group test.
2. Kiểm tra member count và vài UID/name/avatar.
3. Chạy full sync, theo dõi page tới completion.
4. So poll count/voter UID với Zalo.
5. Xác nhận poll multi-choice chỉ tính một lần/người.
6. Xem capability history; không tự nâng thành `FullHistoricalBackfill`.
7. Hỏi `@bot ai 4 tháng rồi chưa vote?`.
8. Hỏi `@bot tui vote gần nhất ở poll nào?`.
9. Dùng member thường hỏi ranking và xác nhận bị từ chối.
10. Dùng admin/deputy thử `@bot 12`, phân trang bằng `@bot tiếp`.
11. Restart API giữa backfill và xác nhận resume không duplicate.

## Giới hạn còn lại

- Capability message phụ thuộc account/group và cần thử bằng account thật.
- `zca-js` 2.1.2 chưa cho truyền cursor vào `getGroupChatHistory`, nên group dài thường chỉ đạt `PartialHistoricalBackfill`.
- Nếu `getGroupInfo` báo còn member nhưng không có API phân trang member tương ứng, hệ thống giữ directory partial và không đánh dấu nhầm người đã rời nhóm.
- Zalo là API không chính thức, contract/rate limit có thể đổi; deploy bridge + API cùng version và theo dõi structured logs.
