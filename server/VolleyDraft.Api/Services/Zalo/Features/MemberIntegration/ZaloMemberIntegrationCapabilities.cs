namespace VolleyDraft.Api.Services;

internal static class ZaloMemberIntegrationCapabilitySupport
{
    public static int DaysSinceJoin(ZaloMemberIntegrationContext context)
    {
        var anchor = context.JoinedAt ?? context.IntegrationStartedAt;
        if (anchor is null) return int.MaxValue;
        return Math.Max(0, (int)Math.Floor((context.SnapshotAt - anchor.Value).TotalDays));
    }

    public static ZaloMemberIntegrationMentionTarget Subject(ZaloMemberIntegrationContext context) =>
        new(context.ZaloUserId, context.DisplayName);

    public static ValueTask<ZaloMemberIntegrationSuggestion?> None() =>
        ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(null);

    public static ValueTask<ZaloMemberIntegrationSuggestion?> Suggest(
        ZaloMemberIntegrationSuggestion suggestion) =>
        ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(suggestion);

    public static bool IsSafeLore(string? text)
    {
        var normalized = ZaloBotIntelligence.Normalize(text ?? string.Empty);
        if (normalized.Length == 0) return false;
        string[] sensitive =
        [
            "lgbt", "dong tinh", "gay", "lesbian", "gioi tinh", "xu huong tinh duc",
            "ton giao", "benh", "suc khoe", "chinh tri", "dan toc"
        ];
        return sensitive.All(token => !normalized.Contains(token, StringComparison.Ordinal));
    }
}

public sealed class ZaloMemberWelcomeCapability : IZaloMemberIntegrationCapability
{
    public string Key => "welcome";
    public int Priority => 100;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.JoinedAt is null && context.IntegrationStartedAt is null)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var days = ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context);
        var subject = ZaloMemberIntegrationCapabilitySupport.Subject(context);
        var text = context.IsRecoveredNewcomer
            ? $"👋 @{context.DisplayName} bữa trước tui chưa kịp chào 😆 Welcome vào {context.Facts.GroupName} nha. Cứ tự nhiên bắt chuyện, hỏi kèo hay chen vô tám với anh em bình thường."
            : context.IsRejoin
            ? $"👋 @{context.DisplayName} quay lại hội rồi nè 😄 Welcome back ông. Cứ nhập hội như cũ nha, thấy kèo hay câu chuyện nào vui thì chen vô bình thường."
            : days <= 1
                ? $"👋 Welcome @{context.DisplayName} vào {context.Facts.GroupName} nha 😄 Hội này chơi bóng chuyền để giao lưu vui vẻ, cứ tự nhiên bắt chuyện với anh em, đừng ngại."
                : $"👋 @{context.DisplayName} vào hội được {days} ngày rồi nè 😄 Nếu còn đang ngại thì cứ chen vô chat, hỏi kèo hay rủ anh em chơi bình thường nha.";
        var anchor = context.JoinedAt ?? context.IntegrationStartedAt!.Value;
        var joinKey = anchor.ToUniversalTime().ToString("yyyyMMddHHmmss");
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key,
            Priority,
            text,
            $"member-integration:welcome:{context.ZaloUserId}:{joinKey}",
            context.IsRecoveredNewcomer
                ? "admin_confirmed_recovered_newcomer"
                : context.IsRejoin ? "verified_rejoin" : "verified_join",
            [subject]));
    }
}

public sealed class ZaloMemberBotIdentityCapability : IZaloMemberIntegrationCapability
{
    public string Key => "bot-identity";
    public int Priority => 90;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 1)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var creator = context.Facts.BotCreatorName;
        var origin = string.IsNullOrWhiteSpace(creator)
            ? "Tui là NPC của group"
            : $"Tui là NPC được {creator} làm ra để phụ group";
        var text = $"🤖 @{context.DisplayName} giới thiệu thêm xíu: {origin} — nhắc kèo, vote, share slot, hỗ trợ anh em và lâu lâu réo mọi người cho group có nhiệt 😆 Có gì chưa rõ cứ hỏi tui.";
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:bot-identity", "new_member_needs_bot_orientation",
            [ZaloMemberIntegrationCapabilitySupport.Subject(context)]));
    }
}

public sealed class ZaloMemberGroupOriginsCapability : IZaloMemberIntegrationCapability
{
    public string Key => "group-origins";
    public int Priority => 80;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 2 ||
            string.IsNullOrWhiteSpace(context.Facts.GroupPurpose))
            return ZaloMemberIntegrationCapabilitySupport.None();
        var founder = string.IsNullOrWhiteSpace(context.Facts.GroupFounderName)
            ? string.Empty
            : $"{context.Facts.GroupFounderName} lập group này. ";
        var text = $"📖 @{context.DisplayName} kể chút lịch sử hội nha: {founder}{context.Facts.GroupPurpose} Mục tiêu là quen nhau rồi chơi lâu dài, nên có gì cứ giao lưu thoải mái 😄";
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:group-origins", "new_member_group_context",
            [ZaloMemberIntegrationCapabilitySupport.Subject(context)]));
    }
}

public sealed class ZaloMemberRoleIntroductionCapability : IZaloMemberIntegrationCapability
{
    public string Key => "role-introduction";
    public int Priority => 75;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 2)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var people = new List<(string Role, ZaloMemberIntegrationMentionTarget Person)>();
        if (context.Roles.Creator is { } creator) people.Add(("người tạo/đứng group", creator));
        people.AddRange(context.Roles.Admins.Take(2).Select(item => ("admin/phó nhóm", item)));
        if (people.Count == 0) return ZaloMemberIntegrationCapabilitySupport.None();

        var intro = string.Join(", ", people.Select(item => $"@{item.Person.DisplayName} ({item.Role})"));
        var mentions = new[] { ZaloMemberIntegrationCapabilitySupport.Subject(context) }
            .Concat(people.Select(item => item.Person))
            .DistinctBy(item => item.ZaloUserId)
            .ToArray();
        var text = $"👥 @{context.DisplayName} để dễ bắt nhịp, đây là mấy người đang giữ vai trò điều hành group: {intro}. Có chuyện về group/sân/kèo chưa rõ thì cứ hỏi mấy ông này nha 😄";
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:roles", "provider_group_roles_available", mentions));
    }
}

public sealed class ZaloMemberGroupCultureCapability : IZaloMemberIntegrationCapability
{
    public string Key => "group-culture";
    public int Priority => 70;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 3 ||
            string.IsNullOrWhiteSpace(context.Facts.CultureSummary))
            return ZaloMemberIntegrationCapabilitySupport.None();
        var text = $"🤝 @{context.DisplayName} note văn hóa hội nè: {context.Facts.CultureSummary} Đánh lỗi thì góp ý vui vẻ rồi đánh tiếp, mục tiêu là càng chơi càng quen nhau nha 😄";
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:culture", "new_member_culture_orientation",
            [ZaloMemberIntegrationCapabilitySupport.Subject(context)]));
    }
}

public sealed class ZaloMemberPracticalGuideCapability : IZaloMemberIntegrationCapability
{
    public string Key => "practical-guide";
    public int Priority => 65;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 3)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var facts = new List<string>();
        if (context.Facts.NormalCourtFeeVnd is { } fee)
            facts.Add($"tiền sân thường khoảng {fee / 1000d:0.#}k/người");
        if (context.Facts.MaxPlayersPerSession is { } max)
            facts.Add($"kèo thường chốt tối đa {max} người; vượt capacity là tui sẽ réo nhẹ để mọi người check lại vote");
        if (facts.Count == 0) return ZaloMemberIntegrationCapabilitySupport.None();
        var text = $"🏐 @{context.DisplayName} vài info thực dụng để khỏi ngại hỏi: {string.Join("; ", facts)}. Thấy poll hợp lịch thì cứ vote, còn không chắc gì cứ hỏi trong group nha.";
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:practical-guide", "grounded_group_practical_facts",
            [ZaloMemberIntegrationCapabilitySupport.Subject(context)]));
    }
}

public sealed class ZaloMemberSocialBridgeCapability : IZaloMemberIntegrationCapability
{
    public string Key => "social-bridge";
    public int Priority => 60;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 4 ||
            context.Signals.DistinctHumanInteractionCount >= 3)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var peer = context.PeerCandidates
            .OrderByDescending(item => item.MessageCount + item.VoteCount * 2)
            .ThenByDescending(item => item.LastActivityAt)
            .FirstOrDefault();
        if (peer is null) return ZaloMemberIntegrationCapabilitySupport.None();

        var subject = ZaloMemberIntegrationCapabilitySupport.Subject(context);
        var peerMention = new ZaloMemberIntegrationMentionTarget(peer.ZaloUserId, peer.DisplayName);
        var question = context.Signals.HasSpoken
            ? "ông thường rảnh tối nào hoặc hay đánh vị trí nào?"
            : "phá băng cái coi 😆 ông thường đánh vị trí nào hoặc rảnh tối nào?";
        var text = $"🫱🏻‍🫲🏼 @{context.DisplayName} {question} @{peer.DisplayName} là một trong mấy mem hoạt động khá đều gần đây, hai ông bắt chuyện làm quen trước cũng được nha 😄";
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, $"member-integration:social-bridge:{peer.ZaloUserId}", "low_human_interaction",
            [subject, peerMention]));
    }
}

public sealed class ZaloMemberGroupLoreCapability : IZaloMemberIntegrationCapability
{
    public string Key => "group-lore";
    public int Priority => 50;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 5)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var subject = ZaloMemberIntegrationCapabilitySupport.Subject(context);
        var approved = context.Facts.ApprovedLore.FirstOrDefault(ZaloMemberIntegrationCapabilitySupport.IsSafeLore);
        if (!string.IsNullOrWhiteSpace(approved))
        {
            var approvedText = approved;
            var loreMentions = new List<ZaloMemberIntegrationMentionTarget> { subject };
            var referencedPeer = context.PeerCandidates.FirstOrDefault(peer =>
                peer.DisplayName.Length >= 2 &&
                approvedText.Contains(peer.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            if (referencedPeer is not null)
            {
                approvedText = approvedText.Replace(
                    referencedPeer.DisplayName,
                    $"@{referencedPeer.DisplayName}",
                    StringComparison.CurrentCultureIgnoreCase);
                loreMentions.Add(new(referencedPeer.ZaloUserId, referencedPeer.DisplayName));
            }
            var text = $"😆 @{context.DisplayName} thêm tí lore của hội cho dễ nhập vai: {approvedText}";
            return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
                Key,
                Priority,
                text,
                $"member-integration:lore:approved:{StableKey(approved)}",
                "approved_group_lore",
                loreMentions.DistinctBy(item => item.ZaloUserId).ToArray()));
        }
        var hint = context.LoreHints.FirstOrDefault();
        if (hint is null) return ZaloMemberIntegrationCapabilitySupport.None();
        var mentions = new[] { subject }.Concat(hint.MentionTargets).DistinctBy(item => item.ZaloUserId).ToArray();
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key,
            Priority,
            $"😆 @{context.DisplayName} lore nhẹ dựa trên hoạt động thật của group nè: {hint.Text}",
            $"member-integration:lore:{hint.Key}",
            "safe_activity_backed_lore",
            mentions));
    }

    private static string StableKey(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in text) hash = hash * 31 + character;
            return ((uint)hash).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

public sealed class ZaloMemberIntegrationScoreCapability : IZaloMemberIntegrationCapability
{
    public string Key => "integration-score";
    public int Priority => 40;

    public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 7)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var score = Calculate(context.Signals);
        var subject = ZaloMemberIntegrationCapabilitySupport.Subject(context);
        var text = score switch
        {
            < 35 when context.Signals.VoteCount > 0 =>
                $"👀 @{context.DisplayName} tui thấy ông có theo kèo/vote rồi mà còn hơi ít nói 😆 Cứ chen vô chat bình thường nha, hội này mục tiêu là quen nhau chứ không phải chỉ bấm poll rồi biến mất.",
            < 35 =>
                $"👋 @{context.DisplayName} vào hội cũng được mấy hôm rồi, đừng đứng ngoài nha 😄 Thấy câu chuyện nào hợp cứ reply, hỏi sân/kèo hoặc tự giới thiệu vị trí chơi cũng được.",
            < 70 =>
                $"🌱 @{context.DisplayName} bắt đầu nhập hội rồi đó 😄 Cứ giữ nhịp chat/chơi với nhiều anh em hơn, vài bữa là thành người nhà thôi.",
            _ =>
                $"🔥 @{context.DisplayName} hòa nhập nhanh nha 😄 Đã có tương tác qua lại với anh em rồi, cứ giữ nhịp này là đẹp."
        };
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, $"member-integration:score:{ScoreBand(score)}", "integration_signals", [subject]));
    }

    internal static int Calculate(ZaloMemberIntegrationSignals signals)
    {
        var score = 0;
        if (signals.HasSpoken) score += 20;
        score += Math.Min(20, signals.ActiveMessageDays * 5);
        score += Math.Min(15, signals.MessageCount * 3);
        score += Math.Min(15, signals.VoteCount * 3);
        score += Math.Min(20, signals.DistinctHumanInteractionCount * 7);
        if (signals.HasRepliedToMember) score += 5;
        if (signals.HasBeenRepliedTo) score += 5;
        return Math.Clamp(score, 0, 100);
    }

    private static string ScoreBand(int score) => score < 35 ? "quiet" : score < 70 ? "warming" : "integrated";
}
