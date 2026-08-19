from pathlib import Path

path = Path('src/components/ZaloAutoSessionOperationsPanel.tsx')
text = path.read_text()

text = text.replace(
    'import { AlertTriangle, Brain, CheckCircle2, Power, RefreshCw, ShieldCheck, TestTube2, XCircle } from "lucide-react";',
    'import { AlertTriangle, Brain, CheckCircle2, Power, RefreshCw, ShieldCheck, TestTube2, Users, XCircle } from "lucide-react";',
    1,
)

if 'type OrganizerCandidate = {' not in text:
    marker = 'type AutoSessionGroup = {'
    block = '''type OrganizerCandidate = {
  zaloUserId: string;
  displayName: string;
  zaloRole: string;
  isCurrentOrganizer: boolean;
  trustedBackup: boolean;
  isFallbackByDefault: boolean;
};

'''
    if marker not in text:
        raise SystemExit('AutoSessionGroup marker not found')
    text = text.replace(marker, block + marker, 1)

if 'organizerCandidates: OrganizerCandidate[] | null;' not in text:
    marker = '  pendingLearningCount: number;\n};'
    replacement = '  pendingLearningCount: number;\n  organizerCandidates: OrganizerCandidate[] | null;\n};'
    if marker not in text:
        raise SystemExit('AutoSessionGroup end marker not found')
    text = text.replace(marker, replacement, 1)

if 'trustedOrganizerZaloUserId?: string;' not in text:
    marker = '  learningReviewNote?: string | null;\n};'
    replacement = '''  learningReviewNote?: string | null;
  trustedOrganizerZaloUserId?: string;
  trustedOrganizerDisplayName?: string;
  trustedOrganizerEnabled?: boolean;
};'''
    if marker not in text:
        raise SystemExit('UpdateExtras end marker not found')
    text = text.replace(marker, replacement, 1)

if '<strong>Trusted Auto Session operators</strong>' not in text:
    marker = '''          <div style={{ marginTop: 14, padding: 14, borderRadius: 13, background: "rgba(30,41,59,.48)" }}>
            <div style={{ display: "flex", justifyContent: "space-between", gap: 10, flexWrap: "wrap" }}>
              <div style={{ display: "flex", gap: 7, alignItems: "center" }}>
                <Brain size={17} /> <strong>Controlled learning</strong>
              </div>'''
    section = '''          <div style={{ marginTop: 14, padding: 14, borderRadius: 13, background: "rgba(30,41,59,.48)" }}>
            <div style={{ display: "flex", gap: 7, alignItems: "center" }}>
              <Users size={17} /> <strong>Trusted Auto Session operators</strong>
            </div>
            <p style={{ color: "#94a3b8", fontSize: 13, margin: "7px 0 0" }}>
              Người tạo poll giữ quyền xử lý poll của họ. Trưởng nhóm là fallback mặc định. Phó/admin khác chỉ được takeover sau escalation khi bạn bật <strong>Trusted Backup</strong>; admin Zalo không được tick sẽ chỉ là bystander và bot bỏ qua chat linh tinh của họ.
            </p>

            <div style={{ display: "grid", gap: 9, marginTop: 12 }}>
              {(selected.organizerCandidates ?? []).length === 0 ? (
                <div style={{ color: "#64748b" }}>
                  Chưa đọc được danh sách trưởng/phó hiện tại. Kiểm tra Zalo connection rồi bấm Làm mới.
                </div>
              ) : (selected.organizerCandidates ?? []).map((candidate) => {
                const staleTrusted = candidate.trustedBackup && !candidate.isCurrentOrganizer;
                const canToggle = !candidate.isFallbackByDefault && (candidate.isCurrentOrganizer || staleTrusted);
                const roleLabel = candidate.isFallbackByDefault
                  ? "Trưởng nhóm · fallback mặc định"
                  : candidate.isCurrentOrganizer
                    ? "Phó/Admin Zalo"
                    : "Không còn là admin Zalo";

                return (
                  <div
                    key={candidate.zaloUserId}
                    style={{
                      display: "flex",
                      alignItems: "center",
                      justifyContent: "space-between",
                      gap: 12,
                      flexWrap: "wrap",
                      padding: 11,
                      borderRadius: 11,
                      border: staleTrusted
                        ? "1px solid rgba(245,158,11,.35)"
                        : "1px solid rgba(148,163,184,.16)",
                      background: "rgba(15,23,42,.5)",
                    }}
                  >
                    <div style={{ minWidth: 220 }}>
                      <strong>{candidate.displayName || candidate.zaloUserId}</strong>
                      <div style={{ color: staleTrusted ? "#fcd34d" : "#94a3b8", fontSize: 12, marginTop: 3 }}>
                        {roleLabel} · {candidate.zaloUserId}
                      </div>
                    </div>

                    {candidate.isFallbackByDefault ? (
                      <span style={{ color: "#86efac", fontSize: 12, fontWeight: 750 }}>
                        Fallback mặc định
                      </span>
                    ) : (
                      <button
                        type="button"
                        disabled={busy || !canToggle}
                        onClick={() => void update(
                          {
                            trustedOrganizerZaloUserId: candidate.zaloUserId,
                            trustedOrganizerDisplayName: candidate.displayName,
                            trustedOrganizerEnabled: !candidate.trustedBackup,
                          },
                          candidate.trustedBackup
                            ? `Đã bỏ Trusted Backup của ${candidate.displayName}.`
                            : `Đã bật Trusted Backup cho ${candidate.displayName}.`,
                        )}
                        style={{
                          ...buttonStyle,
                          background: candidate.trustedBackup ? "#7f1d1d" : "#166534",
                          color: "white",
                          opacity: canToggle ? 1 : 0.55,
                        }}
                      >
                        {candidate.trustedBackup ? "Tắt Trusted Backup" : "Bật Trusted Backup"}
                      </button>
                    )}
                  </div>
                );
              })}
            </div>

            <div style={{ marginTop: 9, color: "#64748b", fontSize: 12 }}>
              Safety: Trust chỉ được đổi từ web admin. AI/chat không thể tự cấp quyền. Khi tạo website, backend kiểm lại cả quyền Zalo hiện tại lẫn Trusted Backup một lần nữa.
            </div>
          </div>

'''
    if marker not in text:
        raise SystemExit('Controlled learning marker not found')
    text = text.replace(marker, section + marker, 1)

path.write_text(text)
print('Trusted Backup UI patch applied.')
