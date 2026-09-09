using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftReminderDeliveryStateContractTests
{
    [Fact]
    public void Reminder_lane_does_not_mark_bucket_handled_when_no_eligible_organizer_exists()
    {
        var source = ReadReminderSource();
        var branch = ExtractBlock(
            source,
            "if (eligible.Count == 0 || desiredTags <= 0)",
            "IReadOnlyList<DraftApproverCandidate> recipients;");

        Assert.DoesNotContain("MarkHandledAsync(", branch, StringComparison.Ordinal);
        Assert.Contains("no eligible organizer can receive it", branch, StringComparison.Ordinal);
        Assert.Contains("continue;", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void Reminder_lane_does_not_mark_bucket_handled_when_pending_conversation_cannot_be_reserved()
    {
        var source = ReadReminderSource();
        var branch = ExtractBlock(
            source,
            "if (recipients.Count == 0)",
            "var ids = recipients.Select");

        Assert.DoesNotContain("MarkHandledAsync(", branch, StringComparison.Ordinal);
        Assert.Contains("no organizer conversation could be reserved", branch, StringComparison.Ordinal);
        Assert.Contains("continue;", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void Reminder_lane_marks_delivery_only_after_provider_send_returns()
    {
        var source = ReadReminderSource();
        var send = source.IndexOf("var providerId = await SendDraftProactiveAsync(", StringComparison.Ordinal);
        var markHandled = source.IndexOf("await reminderStore.MarkHandledAsync(", send, StringComparison.Ordinal);
        var sentAt = source.IndexOf("                    now,", markHandled, StringComparison.Ordinal);

        Assert.True(send >= 0, "reminder lane must send through the proactive provider path");
        Assert.True(markHandled > send, "delivered reminder state must be committed only after provider success");
        Assert.True(sentAt > markHandled, "provider-confirmed reminder state must record a real send timestamp");
    }

    private static string ReadReminderSource()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(
            root,
            "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Draft",
            "ZaloOverbookService.DraftPreparationRemindersV2.cs");
        return File.ReadAllText(path);
    }

    private static string ExtractBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not locate start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not locate end marker after: {startMarker}");
        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "VolleyDraft.sln")) ||
                    Directory.Exists(Path.Combine(directory.FullName, ".git")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
