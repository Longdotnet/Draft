import { useEffect, useState } from "react";
import { Volleyball } from "lucide-react";
import { AdminExceptionFocus } from "../components/AdminExceptionFocus";
import { DbDraftFlow } from "../components/DbDraftFlow";
import { MatchAutopilotCenter } from "../components/MatchAutopilotCenter";
import { MobilePublicDraftFlow } from "../components/MobilePublicDraftFlow";
import { ZaloAutoSessionAdminPanel } from "../components/ZaloAutoSessionAdminPanel";
import { ZaloAutoSessionAuditPanel } from "../components/ZaloAutoSessionAuditPanel";
import { ZaloAutoSessionOperationsPanel } from "../components/ZaloAutoSessionOperationsPanel";
import { ZaloCommunityTipSettingsPanel } from "../components/ZaloCommunityTipSettingsPanel";
import { ZaloGreetingTestPanel } from "../components/ZaloGreetingTestPanel";
import { ZaloOverbookAdminPanel } from "../components/ZaloOverbookAdminPanel";

type ExceptionFocus = "bot-overbook-control" | "auto-session-control" | "draft-workspace";

const exceptionFocusValues = new Set<ExceptionFocus>([
  "bot-overbook-control",
  "auto-session-control",
  "draft-workspace",
]);

function getIsMobileViewport() {
  return typeof window !== "undefined" && window.matchMedia("(max-width: 640px)").matches;
}

function getExceptionTarget() {
  if (typeof window === "undefined") return null;
  const params = new URLSearchParams(window.location.search);
  const focus = params.get("focus") as ExceptionFocus | null;
  const sessionId = params.get("sessionId")?.trim() ?? "";
  if (!focus || !exceptionFocusValues.has(focus) || !sessionId) return null;
  return { focus, sessionId };
}

export function AppHome() {
  const [isMobileViewport, setIsMobileViewport] = useState(getIsMobileViewport);
  const [exceptionTarget] = useState(getExceptionTarget);

  useEffect(() => {
    const mediaQuery = window.matchMedia("(max-width: 640px)");
    const handleChange = () => setIsMobileViewport(mediaQuery.matches);

    handleChange();
    mediaQuery.addEventListener("change", handleChange);
    return () => mediaQuery.removeEventListener("change", handleChange);
  }, []);

  return (
    <main className="app-shell">
      <header className="app-header">
        <div className="brand-lockup">
          <div className="brand-mark">
            <Volleyball size={26} aria-hidden="true" />
          </div>
          <div>
            <p>Nhóm của Nick</p>
            <h1>Bóng chuyền hàng tuần UTE</h1>
          </div>
        </div>
        <div className="header-meta">
          <span>{exceptionTarget ? "Exception từ Zalo" : "Chúc mọi người chơi vui vẻ"}</span>
        </div>
      </header>

      {exceptionTarget ? (
        <AdminExceptionFocus focus={exceptionTarget.focus} sessionId={exceptionTarget.sessionId} />
      ) : isMobileViewport ? (
        <>
          <MobilePublicDraftFlow />
          <ZaloCommunityTipSettingsPanel />
        </>
      ) : (
        <>
          <MatchAutopilotCenter />
          <div id="draft-workspace">
            <DbDraftFlow />
          </div>
          <ZaloGreetingTestPanel />
          <div id="auto-session-control">
            <ZaloAutoSessionAdminPanel />
          </div>
          <ZaloCommunityTipSettingsPanel />
          <ZaloAutoSessionOperationsPanel />
          <ZaloAutoSessionAuditPanel />
          <div id="bot-overbook-control">
            <ZaloOverbookAdminPanel />
          </div>
        </>
      )}
    </main>
  );
}
