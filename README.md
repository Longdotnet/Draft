# Volley Draft MVP

Volley Draft MVP là app hỗ trợ chia đội bóng chuyền bằng hình thức bóc túi mù trên một điện thoại.

App có 2 cách dùng chính:

- Admin dùng website để tạo buổi chơi, nhập danh sách người chơi, cấu hình slot thay phiên và chọn captain.
- Hình ảnh:
  <img width="901" height="791" alt="image" src="https://github.com/user-attachments/assets/643faaf3-b970-48c9-b0fe-88072d89929f" />
  <img width="791" height="794" alt="image" src="https://github.com/user-attachments/assets/20e5a434-6dc6-4fe4-9f22-abd79cfd8873" />
  <img width="777" height="306" alt="image" src="https://github.com/user-attachments/assets/8efb81c8-0b6d-456f-9032-12cc15393610" />
  <img width="800" height="860" alt="image" src="https://github.com/user-attachments/assets/dff25253-ed6a-4e03-89d0-6b8fcc30bd2e" />
  <img width="749" height="277" alt="image" src="https://github.com/user-attachments/assets/63bfef5c-63cf-4418-b238-795a743ac459" />
- Tại sân, mọi người dùng một điện thoại để chọn captain và bóc túi mù theo lượt.
  <img width="373" height="786" alt="image" src="https://github.com/user-attachments/assets/b9c7dd82-c798-4842-abb4-7b1d1eb5d2b0" />

## Link sử dụng

Nếu app đang được deploy trên Render, mở:

```text
https://volley-draft.onrender.com/
```

Nếu link thay đổi, hãy hỏi người quản lý app để lấy link mới.

## Vai trò trong app

Admin:

- Đăng nhập trên website.
- Tạo buổi thi đấu.
- Nhập danh sách người chơi.
- Tạo slot thay phiên nếu có.
- Tạo nhóm muốn chung team nếu cần.
- Chọn captain tự động hoặc thủ công.
- Theo dõi/xử lý khi draft bị nhầm.

Người chơi/captain:

- Không cần đăng nhập.
- Không cần dùng điện thoại riêng.
- Khi tới lượt, admin đưa cùng một điện thoại cho captain.
- Captain chọn 1 trong 3 túi mù.
- App chạy animation, hiện kết quả, rồi chuyển sang captain tiếp theo.

## Cách dùng cho admin

1. Mở app bằng máy tính hoặc laptop.
2. Vào trang `/app`.
3. Đăng nhập tài khoản admin.
4. Tạo một buổi thi đấu mới.
5. Nhập danh sách người chơi.

Khi nhập người chơi, cần chọn:

- Tên người chơi.
- Vai trò.
- Trình độ.
- Giới tính.

App dùng các thông tin này để tính điểm và chia team cân hơn.

## Nhập người chơi từ bình chọn Zalo

Tính năng Zalo chạy qua ba lớp: website React gọi Volley Draft API, API gọi `server/ZaloBridge`, còn credential Zalo được mã hóa trước khi lưu vào database. Cookie, IMEI và user-agent không bao giờ được trả về trình duyệt.

Flow sử dụng:

1. Admin tạo hoặc mở một buổi đấu.
2. Trong panel `Nhập người chơi từ bình chọn Zalo`, bấm `Kết nối bằng QR`.
3. Quét QR và xác nhận đăng nhập trên điện thoại.
4. Bấm lấy danh sách nhóm, chọn nhóm rồi liên kết với buổi đấu.
5. Bấm `Lấy thông tin bình chọn từ Zalo` và chọn một poll.
6. Chọn một hoặc nhiều option cần import.
7. Kiểm tra preview, bỏ chọn người không tham gia và bổ sung giới tính/role/level.
8. Bấm xác nhận import.

Người vote nhiều option chỉ xuất hiện một lần. Tên và avatar được đồng bộ lại từ Zalo, còn giới tính chỉ lấy từ hồ sơ đã lưu hoặc do admin xác nhận. `null` có nghĩa là chưa từng xác nhận; `Unknown` có nghĩa admin đã chọn `Chưa xác định`. Import lại cùng người trong cùng buổi sẽ cập nhật hồ sơ nhưng không tạo bản ghi trùng.

Poll ẩn danh hoặc poll không trả danh sách voter sẽ bị từ chối. Nếu poll thay đổi sau màn hình preview, API yêu cầu tải preview lại. Sau import, giao diện cảnh báo nếu tổng người chưa đủ hoặc chưa chia hết cho ba.

### Chạy local với Zalo mock

Terminal 1:

```powershell
cd server/ZaloBridge
npm install
$env:ZALO_BRIDGE_MOCK="true"
$env:ZALO_BRIDGE_INTERNAL_KEY="development-zalo-bridge-key"
npm run dev
```

Terminal 2:

```powershell
cd server/VolleyDraft.Api
$env:Database__Provider="Sqlite"
$env:ConnectionStrings__Default="Data Source=volley-draft.db"
dotnet run
```

Terminal 3:

```powershell
npm install
npm run dev
```

Để dùng Zalo thật, đặt `ZALO_BRIDGE_MOCK=false`. Khi deploy Render, cấu hình cùng một giá trị bí mật cho `ZALO_BRIDGE_INTERNAL_KEY` và `Zalo__BridgeInternalKey`; đặt URL bridge vào `Zalo__BridgeBaseUrl`; đặt khóa mã hóa ổn định vào `Zalo__CredentialEncryptionKey`. Không đổi khóa mã hóa sau khi đã lưu connection, nếu không credential cũ sẽ không giải mã được.

## Slot thay phiên

Slot thay phiên dùng khi 2 hoặc nhiều người dùng chung 1 vị trí trong team.

Ví dụ:

```text
Bảo / Bình
```

Slot này chỉ tính là 1 slot trong team.

Nếu Bảo hoặc Bình được chọn làm captain:

- Slot `Bảo / Bình` sẽ được gán sẵn vào team của captain đó.
- Slot này không xuất hiện trong túi mù nữa.
- Người còn lại không bị tách ra thành người lẻ.

## Nhóm muốn chung team

Dùng khi admin muốn vài người có xu hướng vào cùng team.

Ví dụ:

```text
Longg / Hồ Quang Tùng
```

Nếu một người trong nhóm được bóc vào team nào, những người còn lại trong nhóm sẽ được ưu tiên kéo vào cùng team đó.

Lưu ý:

- Một người chỉ nên nằm trong một nhóm chung team.
- Nếu trong cùng một nhóm có nhiều captain ở nhiều team khác nhau, app sẽ báo lỗi vì không thể vừa chung team vừa làm captain khác team.

## Chọn captain

Admin có 2 cách chọn captain:

- Auto balanced captains: app tự chọn 3 captain dựa trên điểm và giới tính.
- Manual captain override: admin tự chọn 3 captain.

Người trong slot thay phiên hoặc nhóm muốn chung team vẫn có thể làm captain.

## Logic khi bóc túi mù

App không chia từng người một cách hoàn toàn độc lập. App chia theo `slot`.

Một slot có thể là:

- Một người bình thường.
- Một slot thay phiên gồm 2 hoặc nhiều người.
- Một captain slot đã được gán sẵn cho team.

Khi bắt đầu draft:

- Captain được gán sẵn vào team của mình.
- Người chơi bình thường còn lại được đưa vào túi mù.
- Slot thay phiên còn lại được đưa vào túi mù như 1 slot.
- Slot thay phiên có captain sẽ được gán sẵn vào team của captain đó, không đưa vào túi mù.

Ví dụ:

```text
Shared slot: Bảo / Bình
Captain: Bảo
```

Kết quả:

```text
Team của Bảo có slot Bảo / Bình
Túi mù không còn hiện Bảo / Bình
Bình không bị tách ra thành người lẻ
```

Khi captain chọn 1 túi mù:

- App chọn slot ẩn bên trong.
- App ưu tiên slot giúp team cân hơn về tổng điểm.
- App ưu tiên chia nữ đều hơn giữa các team.
- Trong nhóm slot phù hợp, app vẫn random để giữ cảm giác bóc túi.
- Sau animation, app mới hiện kết quả.

## Cẩn thận khi cấu hình

Trước khi bấm bắt đầu draft, admin nên kiểm tra kỹ:

- Tổng số slot phải vừa đủ với số team.
- App chia đều theo 3 team. Tổng số slot phải từ 6 trở lên và chia hết cho 3, nên các mốc như 12, 15 hoặc 18 người đều có thể bốc túi; số slot mỗi team sẽ tự tính theo danh sách thực tế.
- Captain tính là 1 slot trong team.
- Shared slot tính là 1 slot, dù bên trong có 2 hoặc nhiều người.
- Nếu shared slot có captain, cả shared slot được tính sẵn vào team của captain.
- Nhóm muốn chung team có thể làm team bị đầy nhanh hơn, nên đừng tạo nhóm quá lớn.
- Một người không nên nằm trong nhiều nhóm muốn chung team.
- Không nên đưa cùng một người vào nhiều shared slot.
- Nếu một shared slot có 2 captain thuộc 2 team khác nhau, app sẽ báo lỗi.
- Nếu một nhóm muốn chung team có 2 captain thuộc 2 team khác nhau, app sẽ báo lỗi.

Ví dụ nên tránh:

```text
Nhóm chung team: Longg / Hồ Quang Tùng
Longg là captain Team A
Hồ Quang Tùng là captain Team B
```

Trường hợp này không hợp lệ vì 2 người vừa muốn chung team, vừa là captain của 2 team khác nhau.

## Seed roster mẫu

Nút `Seed roster mẫu` dùng để tạo nhanh danh sách người chơi thử nghiệm.

Lưu ý: seed roster mẫu có thể tạo sẵn một số người và shared slot để test logic. Admin vẫn nên kiểm tra lại danh sách trước khi bấm chọn captain hoặc bắt đầu draft.

## Cách dùng trên mobile tại sân

1. Admin mở app trên điện thoại.
2. Chọn buổi thi đấu gần nhất.
3. Nếu admin chưa nhập đủ người chơi, mobile sẽ hiện trạng thái đang chờ admin sắp xếp.
4. Khi buổi chơi đã sẵn sàng, app hiển thị captain/draft.
5. Admin đưa điện thoại cho captain hiện tại.
6. Captain bấm chọn 1 túi mù.
7. Chờ animation chạy xong.
8. Bấm `Tiếp tục`.
9. Admin đưa điện thoại cho captain tiếp theo.

Lặp lại tới khi bóc hết túi mù.

## Kết quả cuối

Sau khi bóc hết túi mù, app hiển thị đội hình cuối.

Admin có thể:

- Sao chép nội dung đội hình.
- Chia sẻ qua Zalo nếu điện thoại hỗ trợ.
- Bấm đọc kết quả bằng giọng nói nếu trình duyệt/điện thoại hỗ trợ đọc tiếng Việt.

## Khi admin xử lý nhầm

Nếu captain bấm nhầm hoặc bóc nhầm:

- Dùng nút quay lại lượt vừa khui để quay về lượt bóc gần nhất.
- Dùng reset draft từ đầu nếu muốn random lại toàn bộ draft.

Nên dùng các nút này từ giao diện admin.

## Lưu ý quan trọng

- MVP hiện tại là one-device draft: chỉ dùng một điện thoại tại sân.
- Captain không cần đăng nhập.
- Không có room code.
- Không có realtime nhiều thiết bị.
- Không nên để nhiều người cùng mở và bóc trên nhiều máy.
- Nên dùng mạng ổn định trong lúc bóc túi.
- Không nên tắt tab/trình duyệt khi animation reveal đang chạy.
- Nếu app chạy chậm trên điện thoại yếu, hãy bật hoặc dùng chế độ animation nhẹ nếu có.
- Giọng đọc trên laptop và điện thoại có thể khác nhau vì mỗi thiết bị có bộ giọng đọc riêng.

## Quy trình đề xuất tại sân

1. Admin chuẩn bị danh sách trước ở nhà hoặc trước trận.
2. Kiểm tra lại số người chơi, giới tính, trình độ.
3. Tạo shared slot và nhóm chung team nếu cần.
4. Auto chọn captain, sau đó chỉnh tay nếu muốn.
5. Mở mobile tại sân.
6. Cho từng captain bóc túi theo lượt.
7. Sau khi hoàn tất, copy hoặc chia sẻ đội hình lên nhóm Zalo.

## Zalo bot và reminder

Trong màn admin của từng session:

1. Kết nối tài khoản Zalo và liên kết group.
2. Cấu hình giờ đấu, địa điểm, chỗ gửi xe và chọn ảnh vị trí/QR từ máy hoặc thư viện ảnh đã lưu trong DB.
3. Bật bot; nếu cần thì bật reminder, chọn số giờ bắt đầu nhắc và chu kỳ lặp.

Bot chỉ trả lời khi được mention đúng UID. `@bot help` hiển thị menu. Lệnh nhanh `1`–`10` chỉ được nhận khi phần câu hỏi đúng một số; ví dụ `@bot 1 tuần đánh mấy lần` là câu tự nhiên `WeeklySessionCount`, không bị hiểu thành command 1. Dạng như `@bot 7 T6` chỉ được nhận khi phần sau là selector session hợp lệ.

Các lệnh mở rộng:

- `7`: lấy danh sách ba team hiện tại dưới dạng text.
- `8`: đồng bộ voter của poll lên roster web. Bot ưu tiên poll/option đã import trước đó; nếu chưa có thì semantic-match tên session, ngày và option. Người rút vote được chuyển `IsPresent=false`.
- `9`: tự chọn captain nếu thiếu, bắt đầu draft và khui ngẫu nhiên toàn bộ túi. Đây là thao tác phá huỷ nên luôn cần tin nhắn xác nhận thứ hai.
- `10`: tạo team-card PNG từ dữ liệu đội hình và gửi vào Zalo. Lệnh 9 cũng gửi card sau khi hoàn tất.
- `@bot draft lại [ngày/tên trận]`: xoá kết quả bốc team hiện tại, giữ captain và khui lại từ đầu; luôn cần `@bot xác nhận draft lại`.
- `@bot đổi vị trí <người A> với <người B>`: sau khi draft hoàn tất, đổi hai slot thường giữa hai team, tính lại điểm và gửi danh sách/card mới. Bot không tự đổi captain hoặc tách slot ghép.

Các lệnh thay đổi dữ liệu cho phép trưởng nhóm (`creatorId`), phó nhóm (`adminIds`) hoặc Zalo operator được admin chọn trong panel `Bot chat & reminder`. Quyền trưởng/phó nhóm được đọc trực tiếp từ Zalo theo UID khi chạy lệnh; không so quyền bằng display name. API còn dùng lease trên session để các thao tác sync/draft/đổi người không chạy đồng thời.

AI chỉ phân loại câu tự nhiên thành intent JSON có kiểu (`SessionSchedule`, `Roster`, `WeeklySessionCount`...). Handler .NET sau đó đọc giờ, sân, người chơi, poll và slot từ database; model không phải nguồn dữ liệu nghiệp vụ. Nếu classifier lỗi JSON, confidence thấp hoặc provider lỗi, bot dùng routing xác định được hoặc fallback an toàn thay vì gọi một method tùy ý.

Khi câu hỏi có nhiều session, bot lưu conversation state theo bộ `(Zalo account, group, sender)` trong 15 phút. Ví dụ `@bot 6` → bot hỏi trận → `@bot T6` tiếp tục đúng intent QR. Gõ `huỷ`, `cancel` hoặc `không cần nữa` để bỏ câu đang chờ. State không bị dùng chéo giữa hai user/group và tự hết hạn.

Thành viên có thể đề xuất ghi nhớ bằng cách nói tự nhiên rõ ý áp dụng về sau, nhưng rule mới chỉ ở trạng thái `Pending`. Admin duyệt/từ chối/tắt trong panel Bot chat & reminder. Chỉ rule `Approved` mới được semantic-match; rule không được ghi đè dữ liệu trận, danh sách, sân, giờ hoặc slot từ hệ thống. Đây là retrieval theo group, không phải fine-tune model.

Mỗi incoming `messageId` là duy nhất theo connection. API dùng processing lease trước khi gọi AI/gửi Zalo để hai instance không cùng trả lời. Log có account/group/message/intent/có gọi AI hay không; request lỗi được mở lease để retry.

Reminder nhóm theo tài khoản Zalo + group, bỏ qua session đã đủ slot hoặc đã qua giờ và chỉ tag `@all` cho session sớm nhất còn thiếu người. Vì vậy sau khi trận thứ Tư qua, lần chạy tiếp theo mới ưu tiên trận thứ Sáu (hoặc session kế tiếp còn thiếu).

Các biến môi trường cần cấu hình cho API khi deploy:

```text
Zalo__BridgeBaseUrl
Zalo__BridgeInternalKey
Zalo__CredentialEncryptionKey
Zalo__WebhookUrl=https://<api-host>/api/internal/zalo/events
Zalo__WebhookKey=<shared-secret>
Public__BaseUrl=https://<api-host>
Ai__Endpoint=<OpenAI-compatible chat-completions endpoint>
Ai__ApiKey=<provider key>
Ai__Model=<provider model id>
ZaloBot__ConversationTtlMinutes=15
ZaloBot__ExactCommandCooldownSeconds=2
ZaloBot__AiCooldownSeconds=10
ZaloBot__AiPerUserPerMinute=4
ZaloBot__AiPerGroupPerMinute=20
ZaloBot__ClassifierConfidenceThreshold=0.72
ZaloBot__LearnedRuleSimilarityThreshold=0.82
```

`Zalo__WebhookKey` chỉ dùng giữa bridge và API. Không đưa khóa này ra frontend. Nếu chưa cấu hình AI, các lệnh dữ liệu cố định vẫn hoạt động; câu hỏi tự do sẽ nhận thông báo yêu cầu hỏi rõ hơn.

Kiểm tra trước khi deploy:

```powershell
dotnet test server/VolleyDraft.Api.Tests/VolleyDraft.Api.Tests.csproj
dotnet build server/VolleyDraft.Api/VolleyDraft.Api.csproj
npm run build
npm --prefix server/ZaloBridge test
npm --prefix server/ZaloBridge run build
```
