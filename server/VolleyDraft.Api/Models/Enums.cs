namespace VolleyDraft.Api.Models;

public enum SessionStatus
{
    Setup,
    CaptainSelection,
    Drafting,
    Finished,
    Cancelled
}

public enum DraftTurnStatus
{
    Waiting,
    Active,
    Completed,
    Skipped
}

public enum DraftRoundStatus
{
    Waiting,
    Active,
    Completed
}

public enum DraftSlotType
{
    Single,
    Shared
}

public enum PlayerRole
{
    Attack,
    Defense,
    Setter,
    FullStack,
    New
}

public enum PlayerLevel
{
    Good,
    Average,
    New
}

public enum PlayerGender
{
    Unknown,
    Male,
    Female
}

public enum ZaloConnectionStatus
{
    Connected,
    Invalid,
    Disconnected
}

public enum SessionWaitlistStatus
{
    Waiting,
    Invited,
    Accepted,
    Declined,
    Expired,
    Cancelled
}
