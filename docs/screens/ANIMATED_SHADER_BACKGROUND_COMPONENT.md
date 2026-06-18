# ANIMATED_SHADER_BACKGROUND_COMPONENT.md

## Goal

Integrate the reusable animated shader background component into the codebase.

This component will be used as a cinematic background for the gacha-style blind bag reveal screen.

## Component name

Use this file path:

```text
src/components/ui/animated-shader-background.tsx
```

The original component name is `AnoAI`, but rename it to a clearer name:

```tsx
AnimatedShaderBackground
```

Export it as:

```tsx
export default AnimatedShaderBackground;
```

## Tech requirements

The codebase should support:

```text
shadcn project structure
Tailwind CSS
TypeScript
React
```

If the codebase does not support these, provide setup instructions first.

## Dependencies

Install:

```bash
npm install three lucide-react
```

If `lucide-react` icons are unused in the final component, remove the unused imports to avoid lint errors.

## Important fixes

The provided component should be adapted before use.

### 1. Rename component

Change:

```tsx
const AnoAI = () => {}
export default AnoAI;
```

to:

```tsx
const AnimatedShaderBackground = () => {}
export default AnimatedShaderBackground;
```

### 2. Type the ref

Use:

```tsx
const containerRef = useRef<HTMLDivElement | null>(null);
```

### 3. Guard container

Before appending the renderer:

```tsx
if (!container) return;
```

### 4. Make the component fill its parent

The root div should support full-screen usage:

```tsx
<div ref={containerRef} className="absolute inset-0 h-full w-full overflow-hidden" />
```

or allow `className` prop:

```tsx
type AnimatedShaderBackgroundProps = {
  className?: string;
};
```

### 5. Avoid unused icons

The original code imports:

```tsx
Infinity, Rocket, Shield, Brain, Play, ChevronDown
```

Remove these imports unless they are actually used.

### 6. Cleanup safely

Before removing the canvas, check:

```tsx
if (renderer.domElement.parentNode === container) {
  container.removeChild(renderer.domElement);
}
```

## Tailwind CSS

Add this animation if not already present:

```css
@keyframes float {
  0%, 100% {
    transform: translateY(0px);
  }
  50% {
    transform: translateY(-10px);
  }
}
```

If using Tailwind 4, add it to `index.css`.

If using Tailwind 3, add it to `globals.css` or extend `tailwind.config.js`.

## Usage example

```tsx
import AnimatedShaderBackground from "@/components/ui/animated-shader-background";

export function RevealDemo() {
  return (
    <div className="relative h-screen w-full overflow-hidden bg-black">
      <AnimatedShaderBackground />
      <div className="relative z-10 flex h-full items-center justify-center">
        <h1 className="text-4xl font-bold text-white">Gacha Reveal</h1>
      </div>
    </div>
  );
}
```

## Important

This file is only for integrating the reusable shader background.

Do not implement the blind bag reveal logic here.

The blind bag reveal behavior is defined in:

```text
docs/GACHA_STAR_REVEAL_SKILL.md
```

## Acceptance criteria

This task is complete when:

```text
1. AnimatedShaderBackground is added to src/components/ui/animated-shader-background.tsx.
2. The component renders a full-screen animated shader background.
3. The component works inside a modal or full-screen reveal screen.
4. There are no TypeScript errors.
5. There are no unused imports.
6. The renderer is cleaned up correctly when the component unmounts.
```
