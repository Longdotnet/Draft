import type { Gender, Level, Player, Role } from "../types/player";
import type { SharedSlot } from "../types/slot";
import { calculatePlayerScore } from "../logic/scoring";

const makePlayer = (
  id: string,
  name: string,
  role: Role,
  level: Level,
  gender: Gender = "Male",
): Player => ({
  id,
  name,
  role,
  level,
  gender,
  score: calculatePlayerScore(role, level),
});

export const mockPlayers: Player[] = [
  makePlayer("p1", "Nick", "Attack", "Good"),
  makePlayer("p2", "Sin", "Full stack", "Good"),
  makePlayer("p3", "Duy", "Setter", "Average"),
  makePlayer("p4", "Long", "Defense", "Average"),
  makePlayer("p5", "Bảo", "New", "New"),
  makePlayer("p6", "Bình", "Attack", "Good"),
  makePlayer("p7", "Nam", "Defense", "Average"),
  makePlayer("p8", "Huy", "New", "New"),
  makePlayer("p9", "An", "Attack", "Average"),
  makePlayer("p10", "Cường", "Full stack", "Good"),
  makePlayer("p11", "Minh", "Setter", "Good"),
  makePlayer("p12", "Khoa", "Defense", "Average"),
  makePlayer("p13", "Linh", "Setter", "Average"),
  makePlayer("p14", "Tú", "Full stack", "Average"),
  makePlayer("p15", "Sơn", "New", "New"),
  makePlayer("p16", "Phúc", "Attack", "Good"),
  makePlayer("p17", "Quân", "Defense", "Average"),
  makePlayer("p18", "Vy", "Setter", "Average"),
  makePlayer("p19", "Hải", "Full stack", "New"),
];

export const defaultSharedSlots: SharedSlot[] = [
  {
    id: "shared-bao-binh",
    playerIds: ["p5", "p6"],
    role: "Attack",
    setCount: 4,
  },
];

export const roleLabels: Record<Role, string> = {
  Attack: "Tấn công",
  Defense: "Thủ",
  Setter: "Chuyền hai",
  "Full stack": "Toàn diện",
  New: "Mới",
};

export const levelLabels: Record<Level, string> = {
  Good: "Tốt",
  Average: "Trung bình",
  New: "Người mới",
};

export const appSteps = [
  { id: "players", label: "Người chơi" },
  { id: "shared", label: "Slot thay phiên" },
  { id: "captains", label: "Đại diện" },
  { id: "draft", label: "Bốc túi" },
  { id: "teams", label: "Đội hình" },
  { id: "rotation", label: "Lịch set" },
  { id: "balance", label: "Cân bằng" },
] as const;
