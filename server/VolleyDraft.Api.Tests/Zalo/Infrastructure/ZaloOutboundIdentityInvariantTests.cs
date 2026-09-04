using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOutboundIdentityInvariantTests
{
    [Fact]
    public void Critical_outbound_paths_never_persist_idempotency_keys_as_provider_message_ids()
    {
        var root = FindRepositoryRoot();
        var files = new[]
        {
            Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile", "ZaloOverbookService.MissingProfilePrompts.cs"),
            Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile", "ZaloOverbookService.MissingProfileRepliesV2.cs"),
            Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Reminder", "ZaloOverbookManualReminder.cs"),
            Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Reminder", "ZaloReminderService.cs")
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("providerMessageId ?? idempotencyKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("? idempotencyKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageId = idempotencyKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SaveBotMessageAsync(owned, idempotencyKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SaveBotMessageAsync(session, idempotencyKey", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Reminder_paths_require_positive_bridge_delivery_ack_before_advancing_state()
    {
        var root = FindRepositoryRoot();
        var files = new[]
        {
            Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Reminder", "ZaloOverbookManualReminder.cs"),
            Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Reminder", "ZaloReminderService.cs")
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.Contains("var send = await bridge.SendGroupMessageAsync", source, StringComparison.Ordinal);
            Assert.Contains("if (!send.Sent)", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Profile_conversation_history_is_only_written_for_real_provider_message_ids()
    {
        var root = FindRepositoryRoot();
        var file = Path.Combine(
            root,
            "server",
            "VolleyDraft.Api",
            "Services",
            "Zalo",
            "Features",
            "Profile",
            "ZaloOverbookService.MissingProfileRepliesV2.cs");
        var source = File.ReadAllText(file);

        Assert.Contains("NormalizeProviderMessageId(send.MessageId)", source, StringComparison.Ordinal);
        Assert.Contains("if (providerMessageId is not null)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var persistedId =", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "server", "VolleyDraft.Api")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
