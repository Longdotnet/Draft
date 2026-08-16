using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOperatorPermissionParserSafetyTests
{
    [Theory]
    [InlineData("@Npc To An có quyền chưa?")]
    [InlineData("@Npc quyền share slot hoạt động sao?")]
    [InlineData("@Npc cho tui coi quyền hạn của bot")]
    public void Ordinary_permission_questions_do_not_become_grant_or_revoke(string content)
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: Guid.NewGuid().ToString("n"),
            senderId: "user-long",
            senderName: "Long",
            content: content,
            mentions: [],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var command = ZaloOperatorPermissionCommandService.TryParse(incoming);

        Assert.True(command is null || command.Kind == ZaloOperatorPermissionCommandKind.List);
    }
}
