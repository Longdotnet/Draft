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
    public void Reminder_lane_does_not_persist_new_approval_request_before_recipient_reservation_succeeds()
    {
        var source = ReadReminderSource();
        var zeroRecipients = source.IndexOf("if (recipients.Count == 0)", StringComparison.Ordinal);
        var createRequest = source.IndexOf("approvalRequest = await escalationStore.CreateOrReuseAsync(", StringComparison.Ordinal);

        Assert.True(zeroRecipients >= 0, "reminder lane must explicitly handle failed organizer reservation");
        Assert.True(createRequest > zeroRecipients,
            "a new durable approval request must not exist until at least one organizer conversation is reserved");
    }

    [Fact]
    public void Reminder_lane_cleans_reserved_conversations_if_request_persistence_or_send_fails()
    {
        var source = ReadReminderSource();
        var tryStart = source.IndexOf("            try\n            {", source.IndexOf("if (recipients.Count == 0)", StringComparison.Ordinal), StringComparison.Ordinal);
        var createRequest = source.IndexOf("approvalRequest = await escalationStore.CreateOrReuseAsync(", tryStart, StringComparison.Ordinal);
        var catchStart = source.IndexOf("            catch\n            {", createRequest, StringComparison.Ordinal);
        var catchEnd = source.IndexOf("                throw;", catchStart, StringComparison.Ordinal);
        var catchBlock = source[catchStart..catchEnd];

        Assert.True(tryStart >= 0 && createRequest > tryStart,
            "request persistence must be covered by the same failure cleanup boundary as provider delivery");
        Assert.Contains("RemoveDraftPendingAsync(", catchBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("if (approvalRequest is not null)", catchBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Reminder_lane_releases_reservations_when_execution_wins_after_reservation()
    {
        var source = ReadReminderSource();
        var createRequest = source.IndexOf("approvalRequest = await escalationStore.CreateOrReuseAsync(", StringComparison.Ordinal);
        var executionFence = source.IndexOf("approvalRequest?.State == ZaloDraftEscalationState.Executing", createRequest, StringComparison.Ordinal);
        var providerSend = source.IndexOf("var providerId = await SendDraftProactiveAsync(", createRequest, StringComparison.Ordinal);
        var fenceBlock = source[executionFence..providerSend];

        Assert.True(createRequest >= 0, "reminder lane must persist/reuse an approval request after reservation");
        Assert.True(executionFence > createRequest && providerSend > executionFence,
            "execution ownership must be revalidated after request persistence and before provider send");
        Assert.Contains("RemoveDraftPendingAsync(", fenceBlock, StringComparison.Ordinal);
        Assert.Contains("continue;", fenceBlock, StringComparison.Ordinal);
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
