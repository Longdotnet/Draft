from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)


bot_path = Path("server/VolleyDraft.Api/Services/ZaloBotService.cs")
bot = bot_path.read_text(encoding="utf-8")
old = '''        var participantInputs = partners.Select((partnerName, index) =>
        {
            var commandPartnerId = command.PartnerZaloUserIds is { Count: > 0 } && index < command.PartnerZaloUserIds.Count
                ? command.PartnerZaloUserIds[index]
                : null;
            var mention = FindMentionedUser(partnerName, mentionedUsers);
            var mentionId = commandPartnerId ?? mention?.ZaloUserId;
            var normalizedMentionId = NormalizeId(mentionId ?? string.Empty);
            var existing = normalizedMentionId.Length > 0 && session.PlayerNamesByZaloUserId.TryGetValue(normalizedMentionId, out var mentionedPartnerName)
                ? mentionedPartnerName
                : ResolvePlayerReference(partnerName, session.PlayerNames);
            mentionedMembers.TryGetValue(NormalizeId(mentionId ?? string.Empty), out var member);
            var displayName = existing ?? (NormalizeText(partnerName) == "ban"
                ? NextExternalShareName(anchor, session.PlayerNames)
                : partnerName);
            return new ShareSlotParticipantInput(displayName, mentionId, member?.AvatarUrl);
        }).ToList();'''
new = '''        var participantInputs = new List<ShareSlotParticipantInput>();
        for (var index = 0; index < partners.Count; index += 1)
        {
            var partnerName = partners[index];
            var commandPartnerId = command.PartnerZaloUserIds is { Count: > 0 } && index < command.PartnerZaloUserIds.Count
                ? command.PartnerZaloUserIds[index]
                : null;
            var mention = FindMentionedUser(partnerName, mentionedUsers);
            var mentionId = commandPartnerId ?? mention?.ZaloUserId;
            var normalizedMentionId = NormalizeId(mentionId ?? string.Empty);
            var identityLabel = !string.IsNullOrWhiteSpace(mention?.DisplayName)
                ? mention!.DisplayName
                : partnerName;
            var existingByUid = normalizedMentionId.Length > 0 &&
                                session.PlayerNamesByZaloUserId.TryGetValue(normalizedMentionId, out var mentionedPartnerName)
                ? mentionedPartnerName
                : null;
            if (existingByUid is not null && NormalizeText(identityLabel) != NormalizeText(existingByUid))
            {
                return new BotAnswer(
                    $"Mình không ghép vì @mention '{identityLabel}' đang mang UID đã gắn với '{existingByUid}' trong {session.Name}. Dữ liệu chưa thay đổi; hãy mention lại đúng người hoặc nhờ admin sửa identity trước.",
                    null,
                    decision.Intent,
                    aiCalled,
                    ProtectedTerms: [identityLabel, existingByUid, session.Name]);
            }

            var existing = existingByUid ?? ResolvePlayerReference(partnerName, session.PlayerNames);
            mentionedMembers.TryGetValue(normalizedMentionId, out var member);
            var displayName = existing ?? (NormalizeText(partnerName) == "ban"
                ? NextExternalShareName(anchor, session.PlayerNames)
                : partnerName);
            participantInputs.Add(new ShareSlotParticipantInput(displayName, mentionId, member?.AvatarUrl));
        }'''
bot = replace_once(bot, old, new, "bot participant binding")
bot_path.write_text(bot, encoding="utf-8")

service_path = Path("server/VolleyDraft.Api/Services/SessionDraftService.cs")
service = service_path.read_text(encoding="utf-8")

old = '''        foreach (var input in inputs)
        {
            var normalizedInputName = ZaloBotIntelligence.Normalize(input.DisplayName);'''
new = '''        foreach (var input in inputs)
        {
            if (input.ZaloUserId is not null)
            {
                var storedProfile = await db.PlayerProfiles.AsNoTracking()
                    .SingleOrDefaultAsync(profile => profile.ZaloUserId == input.ZaloUserId, cancellationToken);
                if (storedProfile is not null &&
                    NormalizePlayerLookup(storedProfile.DisplayName) != NormalizePlayerLookup(input.DisplayName))
                {
                    return Conflict<ShareSlotPreview>(
                        $"Xung đột định danh: @mention '{input.DisplayName}' mang UID đang thuộc hồ sơ '{storedProfile.DisplayName}'. Bot không tự ghép hoặc đổi hồ sơ.");
                }
            }

            var normalizedInputName = ZaloBotIntelligence.Normalize(input.DisplayName);'''
service = replace_once(service, old, new, "preview profile guard")

old = '''            if (byZaloId.Count == 1) return new(byZaloId[0], null, false);
            return byZaloId.Count > 1
                ? new(null, "UID Zalo này đang gắn với nhiều người trong cùng trận. Admin cần kiểm tra dữ liệu trước khi cập nhật.", false)
                : allowCreateFromZaloMention
                    ? new(null, "Người được @mention chưa có trong danh sách trận này và sẽ được thêm sau khi xác nhận.", true)
                    : new(null, "Người được @mention chưa có trong danh sách trận này.", false);'''
new = '''            if (byZaloId.Count == 1)
            {
                var matched = byZaloId[0];
                var requestedName = NormalizePlayerLookup(playerReference);
                var playerName = NormalizePlayerLookup(matched.DisplayName);
                var profileName = NormalizePlayerLookup(matched.PlayerProfile?.DisplayName);
                if (requestedName.Length == 0 ||
                    requestedName != playerName ||
                    profileName.Length > 0 && requestedName != profileName)
                {
                    return new(
                        null,
                        $"Xung đột định danh: @mention '{playerReference}' mang UID đang gắn với '{matched.DisplayName}'. Bot không tự ghép hoặc ghi dữ liệu.",
                        false);
                }
                return new(matched, null, false);
            }
            if (byZaloId.Count > 1)
                return new(null, "UID Zalo này đang gắn với nhiều người trong cùng trận. Admin cần kiểm tra dữ liệu trước khi cập nhật.", false);

            var requestedReference = NormalizePlayerLookup(playerReference);
            var sameNamePlayers = players.Where(player =>
                    NormalizePlayerLookup(player.DisplayName) == requestedReference)
                .ToList();
            if (sameNamePlayers.Count > 0)
            {
                return new(
                    null,
                    $"Xung đột định danh: '{playerReference}' đã có trong trận nhưng chưa được xác minh bằng UID mention này. Bot không tự gắn UID/profile vào player chỉ vì trùng tên.",
                    false);
            }

            return allowCreateFromZaloMention
                ? new(null, "Người được @mention chưa có trong danh sách trận này và sẽ được thêm sau khi xác nhận.", true)
                : new(null, "Người được @mention chưa có trong danh sách trận này.", false);'''
service = replace_once(service, old, new, "profile resolver guard")

old = '''        var addedPlayers = new List<SessionPlayer>();
        var newlyAddedNames = new List<string>();
        foreach (var input in inputs)
        {
            SessionPlayer? partner = null;'''
new = '''        var addedPlayers = new List<SessionPlayer>();
        var newlyAddedNames = new List<string>();
        foreach (var input in inputs)
        {
            if (input.ZaloUserId is not null)
            {
                var uidPlayers = sessionPlayers.Where(player =>
                        NormalizeZaloId(player.PlayerProfile?.ZaloUserId) == input.ZaloUserId)
                    .ToList();
                if (uidPlayers.Count > 1)
                    return Conflict<PreDraftSharedSlotResult>("UID Zalo này đang gắn với nhiều người trong cùng trận; đã chặn ghi share slot.");

                var uidPlayer = uidPlayers.SingleOrDefault();
                var namePlayers = sessionPlayers.Where(player =>
                        NormalizePlayerLookup(player.DisplayName) == NormalizePlayerLookup(input.DisplayName))
                    .ToList();
                if (namePlayers.Count > 1)
                    return Conflict<PreDraftSharedSlotResult>($"Tên '{input.DisplayName}' đang trùng nhiều player; đã chặn ghi share slot.");
                var namePlayer = namePlayers.SingleOrDefault();
                var profileByUid = await db.PlayerProfiles.SingleOrDefaultAsync(profile => profile.ZaloUserId == input.ZaloUserId);

                if (uidPlayer is not null)
                {
                    var labelMatchesPlayer = NormalizePlayerLookup(uidPlayer.DisplayName) == NormalizePlayerLookup(input.DisplayName);
                    var labelMatchesProfile = uidPlayer.PlayerProfile is null ||
                                              NormalizePlayerLookup(uidPlayer.PlayerProfile.DisplayName) == NormalizePlayerLookup(input.DisplayName);
                    if (!labelMatchesPlayer || !labelMatchesProfile)
                    {
                        return Conflict<PreDraftSharedSlotResult>(
                            $"Xung đột định danh: @mention '{input.DisplayName}' mang UID đang gắn với '{uidPlayer.DisplayName}'. Đã chặn ghi share slot.");
                    }
                    if (namePlayer is not null && namePlayer.Id != uidPlayer.Id)
                    {
                        return Conflict<PreDraftSharedSlotResult>(
                            $"Xung đột định danh: label '{input.DisplayName}' và UID đang trỏ tới hai player khác nhau. Đã chặn ghi share slot.");
                    }
                }
                else
                {
                    if (profileByUid is not null &&
                        NormalizePlayerLookup(profileByUid.DisplayName) != NormalizePlayerLookup(input.DisplayName))
                    {
                        return Conflict<PreDraftSharedSlotResult>(
                            $"Xung đột định danh: @mention '{input.DisplayName}' mang UID thuộc hồ sơ '{profileByUid.DisplayName}'. Đã chặn ghi và giữ nguyên hồ sơ.");
                    }
                    if (namePlayer is not null)
                    {
                        var namePlayerUid = NormalizeZaloId(namePlayer.PlayerProfile?.ZaloUserId);
                        if (namePlayerUid.Length == 0)
                        {
                            return Conflict<PreDraftSharedSlotResult>(
                                $"'{input.DisplayName}' đã tồn tại theo tên nhưng chưa có UID đã xác minh. Bot không tự gắn UID/profile chỉ vì trùng tên.");
                        }
                        if (namePlayerUid != input.ZaloUserId)
                        {
                            return Conflict<PreDraftSharedSlotResult>(
                                $"Xung đột định danh: '{input.DisplayName}' đang gắn với UID khác. Đã chặn ghi share slot.");
                        }
                        if (profileByUid is not null && namePlayer.PlayerProfileId != profileByUid.Id)
                        {
                            return Conflict<PreDraftSharedSlotResult>(
                                $"Xung đột định danh: label/UID/profile của '{input.DisplayName}' không cùng một identity. Đã chặn ghi share slot.");
                        }
                    }
                }
            }

            SessionPlayer? partner = null;'''
service = replace_once(service, old, new, "pre-draft write guard")

old = '''                    else
                    {
                        profile.DisplayName = input.DisplayName;
                        if (!string.IsNullOrWhiteSpace(input.AvatarUrl))
                            profile.AvatarUrl = input.AvatarUrl;'''
new = '''                    else
                    {
                        // Keep the canonical profile label after identity validation.
                        if (!string.IsNullOrWhiteSpace(input.AvatarUrl))
                            profile.AvatarUrl = input.AvatarUrl;'''
service = replace_once(service, old, new, "pre-draft profile rename")

old = '''            var exactPartner = sessionPlayers
                .Where(player => ZaloBotIntelligence.Normalize(player.DisplayName) == normalizedPartnerReference)
                .ToList();'''
new = '''            var exactPartner = sessionPlayers
                .Where(player => ZaloBotIntelligence.Normalize(player.DisplayName) == normalizedPartnerReference)
                .ToList();
            if (partnerZaloId is not null)
            {
                var profileByUid = await db.PlayerProfiles.SingleOrDefaultAsync(profile => profile.ZaloUserId == partnerZaloId);
                if (profileByUid is not null &&
                    NormalizePlayerLookup(profileByUid.DisplayName) != NormalizePlayerLookup(partnerReference))
                {
                    return Conflict<PostDraftSharedSlotResult>(
                        $"Xung đột định danh: @mention '{partnerReference}' mang UID thuộc hồ sơ '{profileByUid.DisplayName}'. Đã chặn ghi share slot.");
                }
                if (partnerByZaloId is not null)
                {
                    var matchesPlayer = NormalizePlayerLookup(partnerByZaloId.DisplayName) == NormalizePlayerLookup(partnerReference);
                    var matchesProfile = partnerByZaloId.PlayerProfile is null ||
                                         NormalizePlayerLookup(partnerByZaloId.PlayerProfile.DisplayName) == NormalizePlayerLookup(partnerReference);
                    if (!matchesPlayer || !matchesProfile)
                    {
                        return Conflict<PostDraftSharedSlotResult>(
                            $"Xung đột định danh: @mention '{partnerReference}' mang UID đang gắn với '{partnerByZaloId.DisplayName}'. Đã chặn ghi share slot.");
                    }
                    if (exactPartner.Count == 1 && exactPartner[0].Id != partnerByZaloId.Id)
                    {
                        return Conflict<PostDraftSharedSlotResult>(
                            $"Xung đột định danh: label '{partnerReference}' và UID đang trỏ tới hai player khác nhau. Đã chặn ghi share slot.");
                    }
                }
                else if (exactPartner.Count == 1)
                {
                    return Conflict<PostDraftSharedSlotResult>(
                        $"'{partnerReference}' đã tồn tại theo tên nhưng chưa khớp UID mention này. Bot không tự attach PlayerProfile theo UID vào SessionPlayer chỉ vì trùng tên.");
                }
            }'''
service = replace_once(service, old, new, "post-draft write guard")

old = '''                    else
                    {
                        profile.DisplayName = partnerReference;
                        if (!string.IsNullOrWhiteSpace(partnerInput.AvatarUrl))
                            profile.AvatarUrl = partnerInput.AvatarUrl;'''
new = '''                    else
                    {
                        // Keep the canonical profile label after identity validation.
                        if (!string.IsNullOrWhiteSpace(partnerInput.AvatarUrl))
                            profile.AvatarUrl = partnerInput.AvatarUrl;'''
service = replace_once(service, old, new, "post-draft profile rename")

service_path.write_text(service, encoding="utf-8")
