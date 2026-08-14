using System.Text.Json;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloLegacyPendingPayloadAdapterTests
{
    [Fact]
    public void Confirmation_payload_extracts_typed_session_and_person_ids_without_copying_blob()
    {
        const string raw = """
            {"SessionId":"session-6","SourceZaloUserId":"u1","TargetZaloUserId":"u2","SecretNote":"do not copy me"}
            """;

        var typed = ZaloLegacyPendingPayloadAdapter.Adapt("SlotTransferConfirm", raw);
        using var collected = JsonDocument.Parse(typed.CollectedArgumentsJson);
        using var missing = JsonDocument.Parse(typed.MissingArgumentsJson);

        Assert.Equal("session-6", collected.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(2, collected.RootElement.GetProperty("personIds").GetArrayLength());
        Assert.Contains(missing.RootElement.EnumerateArray(), item => item.GetString() == "confirmation");
        Assert.DoesNotContain("SecretNote", typed.CollectedArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("do not copy me", typed.CollectedArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_draft_string_array_is_typed_as_session_ids()
    {
        var typed = ZaloLegacyPendingPayloadAdapter.Adapt(
            "AutoDraftConfirm",
            "[\"s1\",\"s2\"]");
        using var collected = JsonDocument.Parse(typed.CollectedArgumentsJson);

        Assert.Equal(2, collected.RootElement.GetProperty("sessionIds").GetArrayLength());
        Assert.Contains("confirmation", typed.MissingArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_without_session_marks_session_reference_missing()
    {
        var typed = ZaloLegacyPendingPayloadAdapter.Adapt(
            "SlotTransfer",
            "{\"CandidateZaloUserIds\":[\"u1\",\"u2\"]}");

        Assert.Contains("sessionReference", typed.MissingArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("legacyPayload", typed.CollectedArgumentsJson, StringComparison.Ordinal);
    }
}
