using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationInterpreterTests
{
    private static readonly DateTimeOffset T4 = new(2026, 8, 19, 17, 30, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset T6 = new(2026, 8, 21, 17, 30, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Cn = new(2026, 8, 23, 17, 30, 0, TimeSpan.FromHours(7));

    [Fact]
    public void T6Only_ReplacesSelectionWithoutRequiringCommandSyntax()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "t6 thôi",
            BuildDraft(),
            ZaloAutoSessionConversationState.PreviewSent,
            null);

        Assert.Equal(ZaloAutoSessionConversationIntent.ModifyDraft, result.Intent);
        Assert.Equal(ZaloAutoSessionSelectionMode.Replace, result.SelectionMode);
        Assert.Equal(["T6"], result.Days);
        Assert.False(result.ExplicitExecute);
    }


    [Fact]
    public void VietnameseFullWeekday_IsUnderstood()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "thứ 6 thôi",
            BuildDraft(),
            ZaloAutoSessionConversationState.PreviewSent,
            null);

        Assert.Equal(["T6"], result.Days);
        Assert.Equal(ZaloAutoSessionSelectionMode.Replace, result.SelectionMode);
    }

    [Fact]
    public void FirstTwoOrdinal_IsUnderstood()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "ừ làm 2 cái đầu",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(["T4", "T6"], result.Days);
        Assert.Equal(ZaloAutoSessionSelectionMode.Replace, result.SelectionMode);
    }

    [Fact]
    public void AddSunday_IsUnderstoodFromNaturalFollowUp()
    {
        var draft = BuildDraft() with
        {
            Items = BuildDraft().Items.Select(item => item with
            {
                Selected = item.DayKey == "T6"
            }).ToList()
        };

        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "à thêm cn",
            draft,
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(ZaloAutoSessionSelectionMode.Add, result.SelectionMode);
        Assert.Equal(["CN"], result.Days);
    }

    [Fact]
    public void LastItemRemove_UsesConversationOrdinal()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "cái cuối bỏ đi",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(ZaloAutoSessionSelectionMode.Remove, result.SelectionMode);
        Assert.Equal(["CN"], result.Days);
    }



    [Fact]
    public void VietnameseHalfHour_IsUnderstood()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "CN 5 rưỡi nha",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(ZaloAutoSessionSelectionMode.None, result.SelectionMode);
        Assert.Equal(17 * 60 + 30, result.TimeOverrides["CN"]);
    }

    [Fact]
    public void DayWithTimeOnly_ChangesTimeWithoutDroppingOtherSelectedDays()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "T6 6h nha",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(ZaloAutoSessionSelectionMode.None, result.SelectionMode);
        Assert.Equal(1080, result.TimeOverrides["T6"]);
        Assert.False(result.ExplicitExecute);
    }

    [Fact]
    public void GenericTimeAcrossMultipleDays_AsksWhichDayAndCarriesPendingTime()
    {
        var first = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "6h nha",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.True(first.NeedsClarification);
        Assert.Equal("time-pending:1080", first.QuestionType);

        var second = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "t6",
            BuildDraft(),
            ZaloAutoSessionConversationState.Clarifying,
            first.QuestionType);

        Assert.Equal(ZaloAutoSessionSelectionMode.None, second.SelectionMode);
        Assert.Equal(1080, second.TimeOverrides["T6"]);
    }


    [Fact]
    public void CapacityDivisibleByThree_MapsToTeamSize()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "cho 21 người nha",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(7, result.TeamSize);
        Assert.False(result.NeedsClarification);
    }

    [Fact]
    public void UnevenCapacity_AsksInsteadOfGuessing()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "20 người",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.True(result.NeedsClarification);
        Assert.Contains("18 người", result.Clarification);
        Assert.Contains("21 người", result.Clarification);
    }


    [Fact]
    public void CapacityClarification_CanBeAnsweredWithBareNumber()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "21",
            BuildDraft(),
            ZaloAutoSessionConversationState.Clarifying,
            "capacity");

        Assert.Equal(7, result.TeamSize);
        Assert.Equal(ZaloAutoSessionConversationIntent.ModifyDraft, result.Intent);
    }

    [Fact]
    public void PerTeamSize_IsUnderstoodDirectly()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "mỗi đội 7",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(7, result.TeamSize);
    }

    [Fact]
    public void PlainOk_IsNotConfirmationBeforeFinalGate()
    {
        var before = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "ok",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);
        var final = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "ok",
            BuildDraft(),
            ZaloAutoSessionConversationState.ReadyToConfirm,
            null);

        Assert.Equal(ZaloAutoSessionConversationIntent.Uncertain, before.Intent);
        Assert.True(before.NeedsClarification);
        Assert.Equal(ZaloAutoSessionConversationIntent.Confirm, final.Intent);
        Assert.False(final.ExplicitExecute);
    }

    [Fact]
    public void NegatedCreate_RemovesDayAndNeverExecutes()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "không tạo t6",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal(ZaloAutoSessionConversationIntent.ModifyDraft, result.Intent);
        Assert.Equal(ZaloAutoSessionSelectionMode.Remove, result.SelectionMode);
        Assert.Equal(["T6"], result.Days);
        Assert.False(result.ExplicitExecute);
    }

    [Fact]
    public void ExplicitNaturalCreate_IsDeterministicExecuteSignal()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "t6 cn nha, tạo đi",
            BuildDraft(),
            ZaloAutoSessionConversationState.PreviewSent,
            null);

        Assert.Equal(ZaloAutoSessionConversationIntent.Confirm, result.Intent);
        Assert.True(result.ExplicitExecute);
        Assert.Equal(ZaloAutoSessionSelectionMode.Replace, result.SelectionMode);
        Assert.Equal(["T6", "CN"], result.Days);
    }

    [Fact]
    public void TwoItemsWithoutIdentity_ClarifiesInsteadOfGuessing()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "làm 2 cái đi",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.True(result.NeedsClarification);
        Assert.Contains("T4 + T6", result.Clarification);
        Assert.Contains("T6 + CN", result.Clarification);
    }

    [Fact]
    public void OldCourt_UsesInitialLocationMarker()
    {
        var result = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "sân cũ nha",
            BuildDraft(),
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.Equal("__INITIAL__", result.Location);
    }

    private static ZaloAutoSessionConversationDraft BuildDraft() => new(
    [
        new("o1", "T4 17h30", "T4", T4, 8, true),
        new("o2", "T6 17h30", "T6", T6, 10, true),
        new("o3", "CN 17h30", "CN", Cn, 9, true)
    ],
    "Sân UTE",
    6);
}
