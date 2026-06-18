import { useEffect, useState } from "react";

export type RevealPerformanceMode = "full" | "lite" | "reduced";

export const revealPerformanceStorageKey = "volleyRevealPerformance";

type NavigatorWithOptionalHints = Navigator & {
  deviceMemory?: number;
  connection?: {
    saveData?: boolean;
  };
};

function getStoredPerformanceMode() {
  try {
    const stored = window.localStorage.getItem(revealPerformanceStorageKey);
    return stored === "full" || stored === "lite" || stored === "reduced" ? stored : null;
  } catch {
    return null;
  }
}

export function detectRevealPerformanceMode(prefersReducedMotion: boolean): RevealPerformanceMode {
  if (prefersReducedMotion) {
    return "reduced";
  }

  const storedMode = getStoredPerformanceMode();
  if (storedMode) {
    return storedMode;
  }

  const nav = window.navigator as NavigatorWithOptionalHints;
  const userAgent = nav.userAgent;
  const platform = nav.platform;
  const coreCount = nav.hardwareConcurrency ?? 4;
  const memoryGb = nav.deviceMemory;
  const isSaveData = nav.connection?.saveData === true;
  const isNarrowScreen = window.innerWidth <= 640;
  const isDesktopPlatform = /Win|MacIntel|MacPPC|Mac68K|Linux x86_64|Linux i686/i.test(platform);
  const isMobileUserAgent = /Android|iPhone|iPad|iPod/i.test(userAgent);
  const isRealMobileDevice = isMobileUserAgent && !isDesktopPlatform;
  const hasVeryLowCpuBudget = coreCount <= 4;
  const hasMobileLowCpuBudget = isRealMobileDevice && coreCount <= 6;
  const hasLowMemoryBudget = typeof memoryGb === "number" && memoryGb <= 4;

  if (
    isSaveData ||
    hasVeryLowCpuBudget ||
    hasMobileLowCpuBudget ||
    hasLowMemoryBudget ||
    (isRealMobileDevice && isNarrowScreen)
  ) {
    return "lite";
  }

  return "full";
}

export function useRevealPerformanceMode(
  isOpen: boolean,
  prefersReducedMotion: boolean,
): RevealPerformanceMode {
  const [mode, setMode] = useState<RevealPerformanceMode>(() =>
    typeof window === "undefined" ? "full" : detectRevealPerformanceMode(prefersReducedMotion),
  );

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const updateMode = () => setMode(detectRevealPerformanceMode(prefersReducedMotion));

    updateMode();
    window.addEventListener("resize", updateMode);
    return () => window.removeEventListener("resize", updateMode);
  }, [isOpen, prefersReducedMotion]);

  return mode;
}
