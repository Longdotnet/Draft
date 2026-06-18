import { useEffect } from "react";
import { Gift, Volleyball } from "lucide-react";
import { useNavigate } from "react-router-dom";
import { DottedSurface } from "../components/ui/dotted-surface";

export function SplashScreen() {
  const navigate = useNavigate();

  useEffect(() => {
    const timer = window.setTimeout(() => {
      navigate("/app", { replace: true });
    }, 10000);

    return () => window.clearTimeout(timer);
  }, [navigate]);

  return (
    <main className="relative min-h-screen overflow-hidden bg-[#070b1a] text-white">
      <DottedSurface className="opacity-95" />
      <div className="absolute inset-0 bg-[radial-gradient(circle_at_50%_18%,rgba(56,189,248,0.20),transparent_34%),linear-gradient(180deg,rgba(7,11,26,0.20),rgba(7,11,26,0.94))]" />

      <section className="relative z-10 mx-auto flex min-h-screen w-full max-w-4xl flex-col items-center justify-center px-6 py-10 text-center">
        <div className="mb-7 grid h-20 w-20 place-items-center rounded-lg border border-white/15 bg-white/10 shadow-glow backdrop-blur">
          <Volleyball size={42} aria-hidden="true" />
        </div>

        <p className="eyebrow mb-3">Nhóm của Nick</p>
        <h1 className="text-5xl font-black tracking-normal text-white sm:text-7xl">Sân UTE</h1>
        <p className="mt-5 max-w-2xl text-lg font-semibold leading-8 text-sky-100 sm:text-xl">
          Chia đội vui nhưng công bằng
        </p>

        <div className="mt-10 w-full max-w-md rounded-lg border border-white/10 bg-white/10 p-4 shadow-glow backdrop-blur">
          <div className="flex items-center justify-center gap-2 text-sm font-black text-orange-100">
            <Gift size={18} aria-hidden="true" />
            <span>Đang chuẩn bị...</span>
          </div>
          <div className="mt-4 h-2 overflow-hidden rounded-md bg-white/15">
            <div className="splash-progress h-full rounded-md bg-orange-400" />
          </div>
        </div>

        <p className="mt-7 max-w-2xl text-sm font-semibold leading-6 text-slate-300">
          Random có kiểm soát · Bốc thăm theo đại diện · Cân bằng đội hình
        </p>
      </section>
    </main>
  );
}
