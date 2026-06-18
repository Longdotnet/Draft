import { useEffect, useState } from "react";
import { Volleyball } from "lucide-react";
import { DbDraftFlow } from "../components/DbDraftFlow";
import { MobilePublicDraftFlow } from "../components/MobilePublicDraftFlow";

function getIsMobileViewport() {
  return typeof window !== "undefined" && window.matchMedia("(max-width: 640px)").matches;
}

export function AppHome() {
  const [isMobileViewport, setIsMobileViewport] = useState(getIsMobileViewport);

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
          <span>Chúc mọi người chơi vui vẻ</span>
        </div>
      </header>

      {isMobileViewport ? <MobilePublicDraftFlow /> : <DbDraftFlow />}
    </main>
  );
}
