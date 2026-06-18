# LOADING_FLOW.md

## Goal

Add a 5-second animated loading / splash screen using the `DottedSurface` background component from `backgrounds.md`.

When the user opens the app or clicks the entry link, show the animated DottedSurface interface first.

After 5 seconds, automatically navigate to the main Volley Draft interface.

## Expected flow

```text
User opens app link
↓
Show animated DottedSurface splash screen
↓
Wait 5 seconds
↓
Navigate to main app screen
```

Example:

```text
/ 
→ show SplashScreen for 5 seconds
→ redirect to /app
```

or:

```text
/start
→ show SplashScreen for 5 seconds
→ redirect to /dashboard
```

Use the route that best matches the existing project.

## Important

Do not block the app forever.

The splash screen must automatically redirect after 5 seconds.

The user should see:

```text
Volley Draft
Đang chuẩn bị buổi bốc thăm...
```

Then after 5 seconds:

```text
Go to main MVP screen
```

## Implementation requirements

Use React + Vite + TypeScript.

If the project already uses React Router, use:

```tsx
useNavigate()
```

If the project does not use React Router yet, install and configure it.

Recommended routes:

```tsx
/
  SplashScreen

/app
  Main Volley Draft MVP
```

## SplashScreen behavior

Create:

```text
src/pages/SplashScreen.tsx
```

The component should:

1. Render the `DottedSurface` background.
2. Show the app name: `Volley Draft`.
3. Show subtitle: `Chia đội vui nhưng công bằng`.
4. Show loading text: `Đang chuẩn bị túi mù...`.
5. Wait 5 seconds.
6. Navigate to `/app`.

Example logic:

```tsx
useEffect(() => {
  const timer = window.setTimeout(() => {
    navigate('/app');
  }, 5000);

  return () => window.clearTimeout(timer);
}, [navigate]);
```

## UI direction

The splash screen should feel sporty, premium, and fun.

Use:

```text
Dark background
Animated dotted surface
Large title
Small loading text
Subtle progress indicator
Volleyball / blind bag wording
```

Vietnamese copy:

```text
Volley Draft
Chia đội vui nhưng công bằng
Đang chuẩn bị túi mù...
```

Optional small text:

```text
Random có kiểm soát · Bốc thăm theo đại diện · Cân bằng đội hình
```

## DottedSurface integration

Follow `backgrounds.md` to add:

```text
src/components/ui/dotted-surface.tsx
```

Install dependencies:

```bash
npm install three next-themes
```

If this is a Vite project and `next-themes` causes issues, replace theme logic with a simple local dark mode fallback or use the existing theme provider in the codebase.

Make sure `cn` exists at:

```text
src/lib/utils.ts
```

If not, create it:

```ts
import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
```

Install missing dependencies if needed:

```bash
npm install clsx tailwind-merge
```

## Tailwind note

The class `-z-1` may not work in default Tailwind.

Use one of these instead:

```tsx
className="pointer-events-none fixed inset-0 z-[-1]"
```

or:

```tsx
className="pointer-events-none fixed inset-0 -z-10"
```

## App routing example

If React Router is available, configure routes like this:

```tsx
import { BrowserRouter, Routes, Route } from "react-router-dom";
import SplashScreen from "./pages/SplashScreen";
import AppHome from "./pages/AppHome";

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<SplashScreen />} />
        <Route path="/app" element={<AppHome />} />
      </Routes>
    </BrowserRouter>
  );
}
```

## Acceptance criteria

This task is complete when:

```text
1. Opening `/` shows the DottedSurface splash screen.
2. The splash screen displays Volley Draft branding.
3. The splash screen stays visible for 5 seconds.
4. After 5 seconds, the app navigates to `/app`.
5. `/app` shows the main MVP interface.
6. The animation does not break layout on mobile.
7. The splash screen does not appear again when navigating inside the app unless user opens `/` again.
```
