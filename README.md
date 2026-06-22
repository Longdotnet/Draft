# Volley Draft MVP

Volley Draft MVP là app hỗ trợ chia đội bóng chuyền bằng hình thức bóc túi mù trên một điện thoại.

App có 2 cách dùng chính:

- Admin dùng website để tạo buổi chơi, nhập danh sách người chơi, cấu hình slot thay phiên và chọn captain.
- Tại sân, mọi người dùng một điện thoại để chọn captain và bóc túi mù theo lượt.

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

