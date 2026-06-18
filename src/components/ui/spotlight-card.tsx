import { CSSProperties, HTMLAttributes, useEffect, useRef } from "react";
import { cn } from "../../lib/utils";

type GlowCardProps = HTMLAttributes<HTMLDivElement> & {
  glowColor?: string;
};

export function GlowCard({
  children,
  className,
  glowColor = "rgba(56, 189, 248, 0.42)",
  style,
  ...props
}: GlowCardProps) {
  const cardRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const handlePointerMove = (event: PointerEvent) => {
      const card = cardRef.current;
      if (!card) {
        return;
      }

      const rect = card.getBoundingClientRect();
      card.style.setProperty("--spotlight-x", `${event.clientX - rect.left}px`);
      card.style.setProperty("--spotlight-y", `${event.clientY - rect.top}px`);
    };

    document.addEventListener("pointermove", handlePointerMove);
    return () => document.removeEventListener("pointermove", handlePointerMove);
  }, []);

  return (
    <div
      ref={cardRef}
      className={cn("glow-card", className)}
      style={
        {
          "--glow-color": glowColor,
          ...style,
        } as CSSProperties
      }
      {...props}
    >
      <div className="relative z-10 h-full">{children}</div>
    </div>
  );
}
