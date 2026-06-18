# SCREEN SKILLS

## 01-player-list.skill.md

### Goal

Create the screen where admin enters and reviews all players.

### UI sections

```text
Header: Danh sách người chơi
Summary cards:
- Tổng người chơi
- Tổng slot thi đấu
- Số team
- Số người mỗi team

Add player form:
- Tên người chơi
- Vai trò
- Trình độ
- Điểm tự động
- Button: Thêm người chơi

Player table/card list:
- Name
- Role badge
- Level badge
- Score
- Actions: Sửa, Xóa
```

### Taste

Clean and fast to scan.

Use badges:

```text
Công
Thủ
Chuyền
Full stack
Mới
Tốt
Trung bình
Mới
```

### Important

Show warning if player count does not fit team setup.

Example:

```text
18 người có thể chia 3 team x 6.
```

or

```text
17 slot chưa đủ để chia 3 team x 6.
```

---

## 02-shared-slot.skill.md

### Goal

Allow admin to create shared slots.

### UI sections

```text
Title: Slot thay phiên
Description:
"Ghép 2 hoặc nhiều người vào cùng 1 slot thi đấu. Họ sẽ thay phiên nhau theo từng set."

Form:
- Chọn người share slot
- Vai trò slot
- Cách tính điểm: Trung bình / Theo từng set
- Số set dự kiến
- Button: Tạo slot thay phiên

Shared slot list:
- Bảo / Bình 🔁
- Average score
- Role
- Rotation preview
```

### Example display

```text
Bảo / Bình 🔁
Vai trò: Công
Điểm trung bình: 2

Set 1: Bảo, 1 điểm
Set 2: Bình, 3 điểm
Set 3: Bảo, 1 điểm
Set 4: Bình, 3 điểm
```

### Important

A shared slot counts as one team slot.

Show:

```text
Người chơi thật: 18
Slot thi đấu: 17
```

---

## 03-captain-selection.skill.md

### Goal

Select 3 captains/representatives before blind bag draft.

### UI sections

```text
Title: Chọn đại diện / đội trưởng

Tabs:
1. Auto chọn cân bằng
2. Admin chọn thủ công
```

### Auto mode

Show button:

```text
Random 3 đại diện cân bằng
```

After random:

```text
Team A: Nick - 3 điểm
Team B: Bình - 3 điểm
Team C: Minh - 3 điểm
```

Buttons:

```text
Random lại
Xác nhận đại diện
```

### Manual mode

Show dropdowns:

```text
Team A captain
Team B captain
Team C captain
```

Show captain balance status:

```text
Cân bằng tốt
Hơi lệch
Lệch mạnh
```

### Important rules

```text
Captain must be a single player.
Captain cannot be inside shared slot.
No duplicate captain.
```

### Warning example

```text
3 đại diện đang lệch trình. Team A có lợi thế ngay từ đầu.
```

---

## 04-blind-bag-draft.skill.md

### Goal

Create fun blind bag draft screen.

### UI sections

Top status:

```text
Đại hội bốc thăm túi mù
Vòng bốc: 1 / 5
Nhóm hiện tại: Cầu tốt
Lượt hiện tại: Team A
Đại diện: Nick
```

Instruction:

```text
Nick, hãy chọn một túi mù cho Team A.
```

Blind bags:

```text
🎁 Túi 1
🎁 Túi 2
🎁 Túi 3
```

Reveal card:

```text
Nick đã khui túi và bốc được An cho Team A.
```

Next turn:

```text
Tiếp theo: Team B - Đại diện Bình
```

Team preview cards:

```text
Team A
Captain: Nick
Members: Nick, An

Team B
Captain: Bình

Team C
Captain: Minh
```

### Important wording

Use:

```text
khui túi
bốc được
được xếp vào
```

Do not use:

```text
chọn người
selected player
```

### Taste

This screen should feel fun and event-like.

Use dark modal, gift icons, glowing borders, reveal animation style.

---

## 05-team-result.skill.md

### Goal

Show final teams after draft.

### UI sections

```text
Title: Kết quả đội hình
Balance summary
3 team cards
```

Each team card:

```text
Team A
Captain: Nick 👑
Tổng điểm: 12
Điểm trung bình: 2.0 / slot

Members:
- Nick 👑 | Công | 3
- An | Công | 2
- Bảo / Bình 🔁 | Công | avg 2
- Long | Thủ | 2
- ...
```

Show badges:

```text
Captain
Shared slot
Role
Score
```

Balance status:

```text
Cân bằng tốt
Hơi lệch
Lệch mạnh
```

### Important

Shared slot should be displayed as one row:

```text
Bảo / Bình 🔁
```

Do not split them as two separate team members.

---

## 06-set-rotation.skill.md

### Goal

Show who plays each set for shared slots.

### UI sections

```text
Title: Lịch thay phiên theo set
Description:
"Slot thay phiên sẽ đổi người chơi theo từng set."
```

Table columns:

```text
Set
Team
Slot thay phiên
Người vào sân
Điểm
```

Example:

```text
Set 1 | Team A | Bảo / Bình 🔁 | Bảo | 1
Set 2 | Team A | Bảo / Bình 🔁 | Bình | 3
Set 3 | Team A | Bảo / Bình 🔁 | Bảo | 1
Set 4 | Team A | Bảo / Bình 🔁 | Bình | 3
```

### Important

This screen explains why one team may be stronger in some sets.

---

## 07-balance-check.skill.md

### Goal

Show fairness by average score and by set score.

### UI sections

```text
Title: Kiểm tra cân bằng
```

Average balance card:

```text
Team A: 12
Team B: 11.5
Team C: 12
Status: Cân bằng tốt
```

Set balance table:

```text
Set | Team A | Team B | Team C | Chênh lệch | Đánh giá
```

Example:

```text
Set 1 | 8 | 8.5 | 8.5 | 0.5 | Cân bằng tốt
Set 2 | 10 | 8.5 | 8.5 | 1.5 | Hơi lệch
```

Warning example:

```text
Set 2 Team A mạnh hơn vì Bình vào sân.
Gợi ý: đổi lịch xoay tua hoặc đổi slot share sang team khác.
```

### Taste

Make this screen look analytical but still simple.

Use:

```text
⚖️ Balance
Green for good
Amber for slight imbalance
Red for strong imbalance
```
