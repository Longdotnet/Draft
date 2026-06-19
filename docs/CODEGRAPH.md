# Volley Draft Codegraph

Đọc file này trước khi làm task mới để nắm nhanh context, sau đó chỉ mở các file trong nhánh liên quan.

## Product Shape

- MVP có backend database và admin login.
- Live blind bag draft vẫn là one-device: admin mở session trên một điện thoại, đưa máy cho captain hiện tại bốc 1 túi, rồi lấy lại máy.
- Mobile public không login. Website/admin mới login.
- Không có captain login, realtime, SignalR, room code hay permission theo từng captain.

## Frontend Graph

- `src/App.tsx`
  - Route `/` -> `SplashScreen`.
  - Route `/app` -> `AppHome`.
- `src/pages/SplashScreen.tsx`
  - Loading 5 giây bằng `DottedSurface`, sau đó navigate sang `/app`.
- `src/pages/AppHome.tsx`
  - Chọn flow theo viewport:
  - Mobile `max-width: 640px` -> `MobilePublicDraftFlow`.
  - Desktop/tablet -> `DbDraftFlow`.
- `src/components/DbDraftFlow.tsx`
  - Admin login/register.
  - Tạo/sửa/xóa match session.
  - Danh sách session admin có phân trang.
  - CRUD player, shared slot, nhóm muốn chung team.
  - Auto/manual captain selection.
  - Admin one-device blind bag draft.
- `src/components/MobilePublicDraftFlow.tsx`
  - Public one-device flow trên điện thoại.
  - Chọn session gần đây, xem player, random captain, start draft, bốc túi.
- `src/api/dbClient.ts`
  - API client và TypeScript response types.
- `src/components/draft/*`
  - Blind bag card, shooting star reveal modal, reveal performance mode.
- `src/styles.css`
  - Tailwind component classes cho toàn app.

## Backend Graph

- `server/VolleyDraft.Api/Program.cs`
  - CORS, auth, DB provider, `EnsureCreated`, schema patch.
  - Routes:
    - `/api/auth/*`
    - `/api/public/sessions/*`
    - `/api/sessions/*` admin protected.
- `server/VolleyDraft.Api/Contracts/ApiContracts.cs`
  - Request/response records shared by all endpoints.
- `server/VolleyDraft.Api/Services/SessionDraftService.cs`
  - Main domain service.
  - Session CRUD, public session list, player CRUD, shared slots, team preference groups, captain selection, draft start, prepare reveal, open bag, team preview.
- `server/VolleyDraft.Api/Services/AuthService.cs`
  - Register/login/me.
- `server/VolleyDraft.Api/Data/VolleyDraftDbContext.cs`
  - EF Core model configuration.
- `server/VolleyDraft.Api/Data/DatabaseSchemaPatch.cs`
  - Lightweight schema patch for SQLite/PostgreSQL without formal migrations.

## Data Model

- `User`
  - Admin account.
- `MatchSession`
  - One match/day/session. Owns players, teams, slots, rounds, bags, turns and team preference groups.
- `SessionPlayer`
  - Registered participant in a session.
- `DraftSlot`
  - Single player slot, shared slot, or captain slot.
- `DraftSlotPlayer`
  - Join table between slot and session players.
- `Team`
  - Team A/B/C, captain and score summary.
- `TeamPreferenceGroup`
  - Hidden admin-only preference: players that should end up together when possible.
- `DraftRound`
  - Round of bag choices.
- `BlindBag`
  - Visible bag option. `PreparedDraftSlotId` stores the hidden slot picked before reveal animation.
- `DraftTurn`
  - Turn order and active/waiting/opened state.

## Draft Flow

1. Admin creates session.
2. Admin registers players.
3. Admin optionally creates shared slots.
4. Admin optionally creates hidden team preference groups.
5. Admin auto-selects captains or manually overrides.
6. Start draft creates rounds, bags and turns.
7. Current captain taps a bag.
8. API prepares hidden slot first.
9. UI plays shooting-star reveal without showing the name early.
10. User taps `Tiếp tục`.
11. API opens bag, assigns slot to current team, advances next turn.
12. Finished draft persists team result in DB state.

## Task Routing

- Admin UI/session list: `DbDraftFlow.tsx`, `dbClient.ts`, `SessionDraftService.cs`, `Program.cs`.
- Mobile one-device flow: `MobilePublicDraftFlow.tsx`, public routes in `Program.cs`, public methods in `SessionDraftService.cs`.
- Reveal animation/performance: `src/components/draft/*`, `src/lib/revealRarity.ts`, `src/styles.css`.
- DB/schema/model changes: model file, `VolleyDraftDbContext.cs`, `DatabaseSchemaPatch.cs`, contracts, service.
- Auth/deploy: `AuthService.cs`, `JwtTokenService.cs`, `Program.cs`, `docs/RENDER_DEPLOY.md`.

## Current Deployment Notes

- Frontend uses `VITE_API_BASE_URL`.
- Backend can use SQLite locally or PostgreSQL on Render.
- Render Postgres URL may be supplied via `ConnectionStrings__Default` or `DATABASE_URL`.
- Backend CORS origin list comes from `Cors__Origins__0`, plus local Vite URLs.
