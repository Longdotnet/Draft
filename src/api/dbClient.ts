export type DbRole = "Attack" | "Defense" | "Setter" | "FullStack" | "New";
export type DbLevel = "Good" | "Average" | "New";
export type DbGender = "Unknown" | "Male" | "Female";
export type SessionStatus = "Setup" | "CaptainSelection" | "Drafting" | "Finished" | "Cancelled";
export type DraftSlotType = "Single" | "Shared";

export type AuthUser = {
  id: string;
  displayName: string;
  email: string;
};

export type AuthResponse = {
  token: string;
  user: AuthUser;
};

export type SessionResponse = {
  id: string;
  name: string;
  status: SessionStatus;
  teamCount: number;
  teamSize: number;
  totalSets: number;
  adminUserId: string;
  zaloConnectionId: string | null;
  zaloGroupId: string | null;
  zaloGroupName: string | null;
  zaloGroupAvatarUrl: string | null;
  teams: TeamSummary[];
};

export type PublicSessionSummaryResponse = {
  id: string;
  name: string;
  status: SessionStatus;
  teamCount: number;
  teamSize: number;
  totalSets: number;
  playerCount: number;
  requiredPlayerCount: number;
  createdAt: string;
  updatedAt: string;
};

export type AdminSessionSummaryResponse = PublicSessionSummaryResponse;

export type PagedResponse<T> = {
  items: T[];
  page: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
};

export type DeleteResponse = {
  message: string;
};

export type SessionPlayerResponse = {
  id: string;
  displayName: string;
  userId: string | null;
  playerProfileId: string | null;
  zaloUserId: string | null;
  avatarUrl: string | null;
  role: DbRole;
  level: DbLevel;
  gender: DbGender;
  score: number;
  isPresent: boolean;
  isCaptainEligible: boolean;
  isInsideSharedSlot: boolean;
};

export type ZaloConnectionResponse = {
  id: string;
  accountZaloId: string;
  displayName: string;
  avatarUrl: string | null;
  status: "Connected" | "Invalid" | "Disconnected";
  lastValidatedAt: string;
};

export type StartZaloQrLoginResponse = {
  loginId: string;
  status: string;
  expiresAt: string;
};

export type ZaloQrLoginStatusResponse = {
  loginId: string;
  status: string;
  qrImageBase64: string | null;
  displayName: string | null;
  avatarUrl: string | null;
  error: string | null;
  connection: ZaloConnectionResponse | null;
};

export type ZaloGroupResponse = {
  id: string;
  name: string;
  avatarUrl: string | null;
  totalMembers: number;
};

export type ZaloPollOptionResponse = {
  id: string;
  content: string;
  voteCount: number;
};

export type ZaloPollResponse = {
  id: string;
  question: string;
  options: ZaloPollOptionResponse[];
  allowMultipleChoices: boolean;
  isAnonymous: boolean;
  isClosed: boolean;
  hideVotePreview: boolean;
  uniqueVoteCount: number;
  createdAtUnixMs: number;
  updatedAtUnixMs: number;
  expiredAtUnixMs: number;
};

export type ZaloImportCandidateResponse = {
  zaloUserId: string;
  displayName: string;
  avatarUrl: string | null;
  gender: DbGender | null;
  needsGenderSelection: boolean;
  role: DbRole;
  level: DbLevel;
  alreadyInSession: boolean;
  optionIds: string[];
  optionNames: string[];
};

export type ZaloImportPreviewResponse = {
  pollId: string;
  pollQuestion: string;
  selectedOptions: ZaloPollOptionResponse[];
  candidates: ZaloImportCandidateResponse[];
  uniqueVoterCount: number;
  canDivideIntoTeams: boolean;
  playersPerTeam: number | null;
  pollUpdatedAtUnixMs: number;
};

export type ZaloPollImportResultResponse = {
  addedCount: number;
  updatedProfileCount: number;
  skippedExistingCount: number;
  sessionPlayerCount: number;
  message: string;
};

export type SharedSlotResponse = {
  id: string;
  displayName: string;
  role: DbRole;
  gender: DbGender;
  averageScore: number;
  sessionPlayerIds: string[];
  playerNames: string[];
};

export type TeamPreferenceGroupResponse = {
  id: string;
  sessionPlayerIds: string[];
  playerNames: string[];
  averageScore: number;
};

export type CaptainsResponse = {
  captains: CaptainTeamResponse[];
  balance: {
    difference: number;
    status: string;
    warning: string | null;
  };
};

export type CaptainTeamResponse = {
  teamId: string;
  teamName: string;
  sessionPlayerId: string;
  displayName: string;
  score: number;
};

export type DraftStateResponse = {
  sessionStatus: SessionStatus;
  currentRound: number | null;
  totalRounds: number;
  currentTeam: TeamSummary | null;
  currentCaptain: { id: string; name: string } | null;
  viewer: {
    id: string;
    role: string;
    canOpenBag: boolean;
    mode: string;
  };
  bags: BlindBagStateResponse[];
  teamPreview: TeamPreviewResponse[];
  message: string | null;
  lastOpenedBag: OpenedBagResultResponse | null;
};

export type BlindBagStateResponse = {
  id: string;
  label: string;
  isOpened: boolean;
  revealedSlot: RevealedSlotResponse | null;
};

export type RevealedSlotResponse = {
  id: string;
  displayName: string;
  type: DraftSlotType;
  role: DbRole;
  gender: DbGender;
  averageScore: number;
};

export type OpenBagResponse = {
  message: string;
  revealedSlot: RevealedSlotResponse;
  assignedTeam: TeamSummary;
  nextTurn: {
    teamName: string;
    captainName: string;
    roundNumber: number;
  } | null;
};

export type PrepareRevealResponse = {
  revealedSlot: RevealedSlotResponse;
  currentTeam: TeamSummary;
  currentCaptain: { id: string; name: string };
};

export type OpenedBagResultResponse = {
  message: string;
  revealedSlot: RevealedSlotResponse;
  assignedTeam: TeamSummary;
};

export type TeamSummary = {
  id: string;
  name: string;
};

export type TeamPreviewResponse = {
  teamId: string;
  teamName: string;
  captainName: string | null;
  slots: Array<{
    id: string;
    displayName: string;
    type: DraftSlotType;
    gender: DbGender;
    isCaptainSlot: boolean;
    averageScore: number;
  }>;
};

type ApiOptions = {
  method?: string;
  token?: string | null;
  body?: unknown;
};

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/$/, "");

export class ApiRequestError extends Error {
  status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = "ApiRequestError";
    this.status = status;
  }
}

export async function apiFetch<T>(path: string, options: ApiOptions = {}) {
  const response = await fetch(`${apiBaseUrl}/api${path}`, {
    method: options.method ?? "GET",
    headers: {
      "Content-Type": "application/json",
      ...(options.token ? { Authorization: `Bearer ${options.token}` } : {}),
    },
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });

  const contentType = response.headers.get("content-type") ?? "";
  const payload = contentType.includes("application/json") ? await response.json() : null;

  if (!response.ok) {
    const message = payload?.message ?? `Request failed with status ${response.status}`;
    throw new ApiRequestError(message, response.status);
  }

  return payload as T;
}
