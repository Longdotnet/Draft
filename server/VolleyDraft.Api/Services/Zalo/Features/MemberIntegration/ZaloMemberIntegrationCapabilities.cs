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
        string[] variants = context.IsRecoveredNewcomer
            ?
            [
                $"👋 @{context.DisplayName} bữa trước tui chưa kịp chào 😆 Welcome vào {context.Facts.GroupName} nha. Cứ tự nhiên bắt chuyện, hỏi kèo hay chen vô tám với anh em bình thường.",
                $"👋 Chào bù @{context.DisplayName} cái nè 😄 Welcome vào {context.Facts.GroupName}. Thấy anh em đang nói gì hợp gu thì cứ nhập hội tự nhiên nha.",
                $"🏐 @{context.DisplayName} hôm trước bot lỡ nhịp chào ông 😆 Mừng ông vào {context.Facts.GroupName}; cứ hỏi kèo, bắt chuyện hay tám với mọi người thoải mái."
            ]
            : context.IsRejoin
            ?
            [
                $"👋 @{context.DisplayName} quay lại hội rồi nè 😄 Welcome back ông. Cứ nhập hội như cũ nha, thấy kèo hay câu chuyện nào vui thì chen vô bình thường.",
                $"😄 Welcome back @{context.DisplayName}, thấy ông quay lại là vui rồi. Cứ sinh hoạt với anh em như trước, khỏi cần làm quen lại từ đầu nha.",
                $"🏐 @{context.DisplayName} tái xuất rồi ha 😆 Vào lại nhịp cũ thôi ông, có kèo hay câu chuyện nào vui cứ chen vô như bình thường."
            ]
            : days <= 1
                ?
                [
                    $"👋 Welcome @{context.DisplayName} vào {context.Facts.GroupName} nha 😄 Hội này chơi bóng chuyền để giao lưu vui vẻ, cứ tự nhiên bắt chuyện với anh em, đừng ngại.",
                    $"🏐 Chào @{context.DisplayName}, mừng ông vào {context.Facts.GroupName} 😄 Cứ xem đây như hội bóng chuyền quen dần qua mấy kèo, thấy gì vui thì bắt chuyện luôn nha.",
                    $"😄 @{context.DisplayName} welcome vào {context.Facts.GroupName}. Mọi người chủ yếu giao lưu bóng chuyền với nhau, ông cứ tự nhiên chat hay hỏi kèo cho nhanh quen hội."
                ]
                :
                [
                    $"👋 @{context.DisplayName} vào hội được {days} ngày rồi nè 😄 Nếu còn đang ngại thì cứ chen vô chat, hỏi kèo hay rủ anh em chơi bình thường nha.",
                    $"🏐 @{context.DisplayName} nhập hội được {days} ngày rồi ha 😄 Thấy kèo nào hợp lịch hay câu chuyện nào vui thì cứ bắt nhịp với anh em nha.",
                    $"😄 @{context.DisplayName} đã ở hội {days} ngày rồi đó. Cứ tự nhiên hỏi sân, hỏi kèo hoặc tám với mọi người, vài bữa là quen hết thôi."
                ];
        var text = variants[0];
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
            [subject],
            variants));
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
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 1 ||
            string.IsNullOrWhiteSpace(context.Facts.GroupPurpose))
            return ZaloMemberIntegrationCapabilitySupport.None();
        var founder = string.IsNullOrWhiteSpace(context.Facts.GroupFoundersSummary)
            ? "Nhóm được thành lập với mong muốn "
            : $"{context.Facts.GroupFoundersSummary} cùng thành lập nhóm, mỗi người đều góp một vai trò quan trọng, với mong muốn ";
        string[] variants =
        [
            $"📖 @{context.DisplayName} kể chút về {context.Facts.GroupName} nha: {founder}{context.Facts.GroupPurpose} Tinh thần chung là vui vẻ, tôn trọng nhau, không blame hay trách móc; có gì thì góp ý nhẹ nhàng để cùng chơi tốt hơn 😄",
            $"🏐 @{context.DisplayName} thêm chút context về {context.Facts.GroupName} cho dễ bắt nhịp nè: {founder}{context.Facts.GroupPurpose} Mọi người ưu tiên vui vẻ, hòa đồng, không blame nhau; có lỗi thì góp ý nhẹ nhàng rồi cùng chơi tiếp nha 😄",
            $"😄 @{context.DisplayName} biết sơ về {context.Facts.GroupName} luôn nha: {founder}{context.Facts.GroupPurpose} Hội mình hướng đến giao lưu lâu dài, tôn trọng và động viên nhau, không trách móc hay blame khi chơi nha."
        ];
        var text = variants[0];
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:group-origins", "new_member_group_context",
            [ZaloMemberIntegrationCapabilitySupport.Subject(context)],
            variants));
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
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 2 ||
            string.IsNullOrWhiteSpace(context.Facts.CultureSummary))
            return ZaloMemberIntegrationCapabilitySupport.None();
        string[] variants =
        [
            $"🤝 @{context.DisplayName} note văn hóa hội nè: {context.Facts.CultureSummary} Đánh lỗi thì góp ý vui vẻ rồi đánh tiếp, mục tiêu là càng chơi càng quen nhau nha 😄",
            $"😄 @{context.DisplayName} bắt vibe hội nhanh thì nhớ khúc này: {context.Facts.CultureSummary} Có lỗi trên sân cứ nói vui vẻ rồi chơi tiếp, đừng căng nha.",
            $"🏐 @{context.DisplayName} tinh thần chung của hội là: {context.Facts.CultureSummary} Chơi lâu dài nên ưu tiên thoải mái, góp ý nhau xong là qua pha mới thôi 😄"
        ];
        var text = variants[0];
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:culture", "new_member_culture_orientation",
            [ZaloMemberIntegrationCapabilitySupport.Subject(context)],
            variants));
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
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 2)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var facts = new List<string>();
        if (context.Facts.NormalCourtFeeVnd is { } fee)
            facts.Add($"tiền sân thường khoảng {fee / 1000d:0.#}k/người");
        if (context.Facts.MaxPlayersPerSession is { } max)
            facts.Add($"kèo thường chốt tối đa {max} người; vượt capacity là tui sẽ réo nhẹ để mọi người check lại vote");
        if (facts.Count == 0) return ZaloMemberIntegrationCapabilitySupport.None();
        var factText = string.Join("; ", facts);
        string[] variants =
        [
            $"🏐 @{context.DisplayName} vài info thực dụng để khỏi ngại hỏi: {factText}. Thấy poll hợp lịch thì cứ vote, còn không chắc gì cứ hỏi trong group nha.",
            $"📝 @{context.DisplayName} bỏ túi nhanh mấy info này nha: {factText}. Kèo nào hợp lịch thì vote, phần nào chưa rõ cứ hỏi anh em luôn.",
            $"😄 @{context.DisplayName} mấy con số hay phải hỏi tui gom luôn nè: {factText}. Cứ nhìn poll rồi chọn kèo hợp lịch là được nha."
        ];
        var text = variants[0];
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, "member-integration:practical-guide", "grounded_group_practical_facts",
            [ZaloMemberIntegrationCapabilitySupport.Subject(context)],
            variants));
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
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 3 ||
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
        string[] variants =
        [
            $"🫱🏻‍🫲🏼 @{context.DisplayName} {question} @{peer.DisplayName} là một trong mấy mem hoạt động khá đều gần đây, hai ông bắt chuyện làm quen trước cũng được nha 😄",
            $"😄 @{context.DisplayName} bắt chuyện thử với @{peer.DisplayName} đi ông; mem này hoạt động khá đều gần đây. Mở màn bằng chuyện vị trí chơi hay tối nào rảnh là dễ nhất.",
            $"🏐 @{context.DisplayName} nếu chưa biết bắt nhịp với ai thì réo @{peer.DisplayName} thử nha 😆 Hai ông nói chuyện kèo, vị trí hay lịch rảnh là vào guồng nhanh lắm."
        ];
        var text = variants[0];
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, $"member-integration:social-bridge:{peer.ZaloUserId}", "low_human_interaction",
            [subject, peerMention],
            variants));
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
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 3)
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
            string[] variants =
            [
                $"😆 @{context.DisplayName} thêm tí lore của hội cho dễ nhập vai: {approvedText}",
                $"📖 @{context.DisplayName} bonus một mẩu chuyện nội bộ cho nhanh quen hội nè: {approvedText}",
                $"😂 @{context.DisplayName} học chút lore group luôn nha: {approvedText}"
            ];
            var text = variants[0];
            return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
                Key,
                Priority,
                text,
                $"member-integration:lore:approved:{StableKey(approved)}",
                "approved_group_lore",
                loreMentions.DistinctBy(item => item.ZaloUserId).ToArray(),
                variants));
        }
        var hint = context.LoreHints.FirstOrDefault();
        if (hint is null) return ZaloMemberIntegrationCapabilitySupport.None();
        var mentions = new[] { subject }.Concat(hint.MentionTargets).DistinctBy(item => item.ZaloUserId).ToArray();
        string[] hintVariants =
        [
            $"😆 @{context.DisplayName} lore nhẹ dựa trên hoạt động thật của group nè: {hint.Text}",
            $"📖 @{context.DisplayName} thêm một mẩu lore từ hoạt động gần đây của hội nè: {hint.Text}",
            $"😂 @{context.DisplayName} muốn bắt vibe group nhanh thì nghe mẩu này: {hint.Text}"
        ];
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key,
            Priority,
            hintVariants[0],
            $"member-integration:lore:{hint.Key}",
            "safe_activity_backed_lore",
            mentions,
            hintVariants));
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
        if (ZaloMemberIntegrationCapabilitySupport.DaysSinceJoin(context) < 4)
            return ZaloMemberIntegrationCapabilitySupport.None();
        var score = Calculate(context.Signals);
        var subject = ZaloMemberIntegrationCapabilitySupport.Subject(context);
        string[] variants = score switch
        {
            < 35 when context.Signals.VoteCount > 0 =>
            [
                $"👀 @{context.DisplayName} tui thấy ông có theo kèo/vote rồi mà còn hơi ít nói 😆 Cứ chen vô chat bình thường nha, hội này mục tiêu là quen nhau chứ không phải chỉ bấm poll rồi biến mất.",
                $"😄 @{context.DisplayName} vote kèo có mặt rồi đó, giờ thêm vài câu chat nữa là bắt nhịp hội nhanh lắm. Thấy chuyện nào hợp cứ chen vô nha.",
                $"🏐 @{context.DisplayName} đã theo poll/kèo rồi thì coi như có một chân trong hội 😆 Rảnh cứ tám thêm với anh em cho mau quen mặt."
            ],
            < 35 =>
            [
                $"👋 @{context.DisplayName} vào hội cũng được mấy hôm rồi, đừng đứng ngoài nha 😄 Thấy câu chuyện nào hợp cứ reply, hỏi sân/kèo hoặc tự giới thiệu vị trí chơi cũng được.",
                $"😄 @{context.DisplayName} mấy hôm đầu cứ tự nhiên nha; hỏi kèo, nói vị trí chơi hay reply câu chuyện đang có đều được.",
                $"🏐 @{context.DisplayName} chưa nói nhiều cũng không sao, nhưng thấy chủ đề nào hợp thì chen vô thử nha. Làm quen qua vài câu chat nhanh hơn nhiều 😄"
            ],
            < 70 =>
            [
                $"🌱 @{context.DisplayName} bắt đầu nhập hội rồi đó 😄 Cứ giữ nhịp chat/chơi với nhiều anh em hơn, vài bữa là thành người nhà thôi.",
                $"😄 @{context.DisplayName} bắt nhịp ổn rồi nha. Cứ nói chuyện và đi kèo với thêm vài anh em nữa là quen hội nhanh thôi.",
                $"🏐 @{context.DisplayName} đang hòa nhập ngon rồi đó. Giữ nhịp chơi với chat như này là vài bữa quen hết mặt 😄"
            ],
            _ =>
            [
                $"🔥 @{context.DisplayName} hòa nhập nhanh nha 😄 Đã có tương tác qua lại với anh em rồi, cứ giữ nhịp này là đẹp.",
                $"😄 @{context.DisplayName} vào guồng với hội rồi đó. Cứ giữ nhịp nói chuyện/chơi cùng anh em như hiện tại là ngon.",
                $"🔥 @{context.DisplayName} bắt sóng group nhanh ghê 😆 Tương tác qua lại ổn rồi, cứ sinh hoạt tự nhiên vậy thôi nha."
            ]
        };
        var text = variants[0];
        return ZaloMemberIntegrationCapabilitySupport.Suggest(new(
            Key, Priority, text, $"member-integration:score:{ScoreBand(score)}", "integration_signals", [subject], variants));
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
