from pathlib import Path

# ---------------- Conversation service ----------------
path = Path('server/VolleyDraft.Api/Services/ZaloAutoSessionConversationService.cs')
text = path.read_text()
if 'private readonly ZaloAutoSessionTrustedOrganizerStore trustedOrganizers = new(db);' not in text:
    text = text.replace(
        '    private readonly ZaloAutoSessionV2Store runtimeStore = new(db);\n',
        '    private readonly ZaloAutoSessionV2Store runtimeStore = new(db);\n'
        '    private readonly ZaloAutoSessionTrustedOrganizerStore trustedOrganizers = new(db);\n',
        1)

old = '''        var trustedFallbackId = NormalizeId(roles.CreatorId);
        var senderTrustedForTakeover = string.Equals(
            senderId,
            trustedFallbackId,
            StringComparison.Ordinal);'''
new = '''        var trustedFallbackId = NormalizeId(roles.CreatorId);
        var trustedBackupIds = await trustedOrganizers.GetEnabledIdsAsync(tracked.Id, cancellationToken);
        var senderTrustedForTakeover =
            string.Equals(senderId, trustedFallbackId, StringComparison.Ordinal) ||
            trustedBackupIds.Contains(senderId);'''
if old in text:
    text = text.replace(old, new, 1)
elif 'var trustedBackupIds = await trustedOrganizers.GetEnabledIdsAsync(tracked.Id, cancellationToken);' not in text:
    raise SystemExit('incoming trusted fallback block not found')

old = '''                var trustedFallbackId = NormalizeId(roles.CreatorId);

                IReadOnlyList<string> targets;'''
new = '''                var trustedFallbackId = NormalizeId(roles.CreatorId);
                var trustedBackupIds = await trustedOrganizers.GetEnabledIdsAsync(tracked.Id, cancellationToken);
                var trustedFallbackTargets = new[] { trustedFallbackId }
                    .Concat(trustedBackupIds)
                    .Where(id => id.Length > 0)
                    .Where(id => organizers.Contains(id, StringComparer.Ordinal))
                    .Where(id => !string.Equals(id, NormalizeId(conversation.ActiveOrganizerId), StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                IReadOnlyList<string> targets;'''
if old in text:
    text = text.replace(old, new, 1)
elif 'var trustedFallbackTargets = new[] { trustedFallbackId }' not in text:
    raise SystemExit('follow-up trusted target block not found')

old = '''                    var fallbackAvailable =
                        trustedFallbackId.Length > 0 &&
                        organizers.Contains(trustedFallbackId, StringComparer.Ordinal) &&
                        !string.Equals(
                            trustedFallbackId,
                            NormalizeId(conversation.ActiveOrganizerId),
                            StringComparison.Ordinal);'''
new = '''                    var fallbackAvailable = trustedFallbackTargets.Count > 0;'''
if old in text:
    text = text.replace(old, new, 1)
elif 'var fallbackAvailable = trustedFallbackTargets.Count > 0;' not in text:
    raise SystemExit('fallback availability block not found')

old = '''                    targets = [trustedFallbackId];
                    text =
                        "Poll này vẫn chưa được xử lý nên website CHƯA được tạo. " +
                        "Bạn là trưởng nhóm fallback cho Auto Session. Nếu muốn xử lý thay, hãy bấm Trả lời tin này rồi nói “nhận xử lý” hoặc nói rõ lịch cần chỉnh. " +
                        "Bot vẫn sẽ chốt lại trước khi tạo website.";'''
new = '''                    targets = trustedFallbackTargets;
                    text =
                        "Poll này vẫn chưa được xử lý nên website CHƯA được tạo. " +
                        "Bạn được cấu hình là người fallback đáng tin cậy cho Auto Session. Nếu muốn xử lý thay, hãy bấm Trả lời tin này rồi nói “nhận xử lý” hoặc nói rõ lịch cần chỉnh. " +
                        "Bot vẫn sẽ chốt lại trước khi tạo website.";'''
if old in text:
    text = text.replace(old, new, 1)
elif 'targets = trustedFallbackTargets;' not in text:
    raise SystemExit('fallback target/message block not found')

execute_anchor = '''        if (!organizers.Contains(organizerId, StringComparer.Ordinal))
        {
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                "Quyền trưởng/phó của bạn đã thay đổi nên tui chưa thể tạo lịch. Website vẫn chưa được tạo.",
                cancellationToken);
            return true;
        }

        var proposal = await autoSessions.GetProposalAsync(tracked.Id, conversation.PollId, cancellationToken);'''
execute_new = '''        if (!organizers.Contains(organizerId, StringComparer.Ordinal))
        {
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                "Quyền trưởng/phó của bạn đã thay đổi nên tui chưa thể tạo lịch. Website vẫn chưa được tạo.",
                cancellationToken);
            return true;
        }

        var isOriginalOrganizer = string.Equals(
            organizerId,
            NormalizeId(conversation.OriginalOrganizerId),
            StringComparison.Ordinal);
        if (!isOriginalOrganizer)
        {
            var currentCreatorId = NormalizeId(roles.CreatorId);
            var trustedBackupIds = await trustedOrganizers.GetEnabledIdsAsync(tracked.Id, cancellationToken);
            var stillTrusted =
                string.Equals(organizerId, currentCreatorId, StringComparison.Ordinal) ||
                trustedBackupIds.Contains(organizerId);
            if (!stillTrusted)
            {
                await SendConversationTextAsync(
                    conversation,
                    organizerId,
                    organizerName,
                    "Quyền Auto Session operator của bạn đã thay đổi nên tui chưa tạo website. Bản nháp vẫn được giữ an toàn.",
                    cancellationToken);
                return true;
            }
        }

        var proposal = await autoSessions.GetProposalAsync(tracked.Id, conversation.PollId, cancellationToken);'''
if execute_anchor in text:
    text = text.replace(execute_anchor, execute_new, 1)
elif 'var isOriginalOrganizer = string.Equals(' not in text:
    raise SystemExit('execution organizer gate not found')
path.write_text(text)

# ---------------- Settings service ----------------
path = Path('server/VolleyDraft.Api/Services/ZaloAutoSessionSettingsService.cs')
text = path.read_text()
if 'private readonly ZaloAutoSessionTrustedOrganizerStore trustedOrganizerStore = new(db);' not in text:
    text = text.replace(
        '    private readonly ZaloAutoSessionV2Store v2Store = new(db);\n',
        '    private readonly ZaloAutoSessionV2Store v2Store = new(db);\n'
        '    private readonly ZaloAutoSessionTrustedOrganizerStore trustedOrganizerStore = new(db);\n',
        1)

marker = '        tracked.AutoSessionEnabled = request.AutoSessionEnabled;'
if 'var trustedOrganizerChange =' not in text:
    validation = '''        var trustedOrganizerId = NormalizeId(request.TrustedOrganizerZaloUserId);
        var trustedOrganizerChange =
            request.TrustedOrganizerEnabled is not null ||
            trustedOrganizerId.Length > 0;
        if (trustedOrganizerChange &&
            (trustedOrganizerId.Length == 0 || request.TrustedOrganizerEnabled is null))
            return BadRequest<ZaloAutoSessionGroupResponse>("Trusted Backup cần Zalo user id và trạng thái bật/tắt.");

        tracked.AutoSessionEnabled = request.AutoSessionEnabled;'''
    if marker not in text:
        raise SystemExit('settings update validation marker not found')
    text = text.replace(marker, validation, 1)

update_start = text.index('    public async Task<ServiceResult<ZaloAutoSessionGroupResponse>> UpdateGroupAsync(')
if 'await trustedOrganizerStore.EnsureAsync(cancellationToken);' not in text[update_start:text.index('    internal static bool TryParseStartTime', update_start)]:
    ensure_pos = text.index('        await v2Store.EnsureAsync(cancellationToken);', update_start)
    ensure_line_end = text.index('\n', ensure_pos)
    text = text[:ensure_line_end + 1] + '        await trustedOrganizerStore.EnsureAsync(cancellationToken);\n' + text[ensure_line_end + 1:]

connection_marker = '''        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tracked.ZaloConnectionId && item.AdminUserId == adminUserId, cancellationToken);'''
if 'if (trustedOrganizerChange)' not in text:
    trust_action = '''        if (trustedOrganizerChange)
        {
            var enabled = request.TrustedOrganizerEnabled!.Value;
            var displayName = string.IsNullOrWhiteSpace(request.TrustedOrganizerDisplayName)
                ? trustedOrganizerId
                : request.TrustedOrganizerDisplayName.Trim();

            if (enabled)
            {
                var trustedConnection = await db.ZaloConnections
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.Id == tracked.ZaloConnectionId &&
                        item.AdminUserId == adminUserId,
                        cancellationToken);
                if (trustedConnection is null || trustedConnection.Status != ZaloConnectionStatus.Connected)
                    return BadRequest<ZaloAutoSessionGroupResponse>("Cần Zalo connection đang Connected để bật Trusted Backup.");

                try
                {
                    using var trustedDocument = JsonDocument.Parse(
                        credentialProtector.Unprotect(trustedConnection.EncryptedCredentials));
                    var roles = await bridge.GetGroupRolesAsync(trustedDocument.RootElement.Clone(), tracked.GroupId);
                    var creatorId = NormalizeId(roles.CreatorId);
                    if (string.Equals(trustedOrganizerId, creatorId, StringComparison.Ordinal))
                        return BadRequest<ZaloAutoSessionGroupResponse>("Trưởng nhóm đã là fallback mặc định, không cần thêm Trusted Backup.");

                    var currentOrganizerIds = new[] { creatorId }
                        .Concat(roles.AdminIds.Select(NormalizeId))
                        .Where(id => id.Length > 0)
                        .ToHashSet(StringComparer.Ordinal);
                    if (!currentOrganizerIds.Contains(trustedOrganizerId))
                        return BadRequest<ZaloAutoSessionGroupResponse>("Chỉ trưởng/phó nhóm Zalo hiện tại mới được bật Trusted Backup.");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    return BridgeFailure<ZaloAutoSessionGroupResponse>(exception);
                }
            }

            await trustedOrganizerStore.SetAsync(
                tracked.Id,
                trustedOrganizerId,
                displayName,
                enabled,
                adminUserId,
                cancellationToken);
        }

        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tracked.ZaloConnectionId && item.AdminUserId == adminUserId, cancellationToken);'''
    # Use the last occurrence because CreateGroupAsync also declares a connection earlier.
    pos = text.rfind(connection_marker)
    if pos < 0:
        raise SystemExit('settings connection marker not found')
    text = text[:pos] + trust_action + text[pos + len(connection_marker):]

operational_marker = '''        var learning = await v2Store.GetLearningSignalsAsync(response.Id, 100, cancellationToken);
        var visibleLearning = learning.Take(20).Select(ToLearningResponse).ToList();'''
if 'var organizerCandidates = await GetOrganizerCandidatesAsync(' not in text:
    operational_new = '''        var learning = await v2Store.GetLearningSignalsAsync(response.Id, 100, cancellationToken);
        var visibleLearning = learning.Take(20).Select(ToLearningResponse).ToList();
        await trustedOrganizerStore.EnsureAsync(cancellationToken);
        var trustedOrganizers = await trustedOrganizerStore.GetAsync(response.Id, cancellationToken);
        var organizerCandidates = await GetOrganizerCandidatesAsync(
            response,
            connection,
            trustedOrganizers,
            cancellationToken);'''
    if operational_marker not in text:
        raise SystemExit('operational data marker not found')
    text = text.replace(operational_marker, operational_new, 1)

return_marker = '''            LearningSignals = visibleLearning,
            PendingLearningCount = learning.Count(item => item.Status == ZaloAutoSessionLearningStatus.Pending)
        };'''
if 'OrganizerCandidates = organizerCandidates' not in text:
    return_new = '''            LearningSignals = visibleLearning,
            PendingLearningCount = learning.Count(item => item.Status == ZaloAutoSessionLearningStatus.Pending),
            OrganizerCandidates = organizerCandidates
        };'''
    if return_marker not in text:
        raise SystemExit('settings response marker not found')
    text = text.replace(return_marker, return_new, 1)

helper_marker = '    private async Task<int> CountSessionsAsync('
if 'GetOrganizerCandidatesAsync(' not in text:
    helper = '''    private async Task<IReadOnlyList<ZaloAutoSessionOrganizerCandidateResponse>> GetOrganizerCandidatesAsync(
        ZaloAutoSessionGroupResponse response,
        ZaloConnection? connection,
        IReadOnlyList<ZaloAutoSessionTrustedOrganizerData> trusted,
        CancellationToken cancellationToken)
    {
        var trustedById = trusted
            .GroupBy(item => NormalizeId(item.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var current = new Dictionary<string, (string Name, string Role, bool DefaultFallback)>(StringComparer.Ordinal);

        if (connection is not null && connection.Status == ZaloConnectionStatus.Connected)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    credentialProtector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var roles = await bridge.GetGroupRolesAsync(credentials, response.GroupId);
                var creatorId = NormalizeId(roles.CreatorId);
                var ids = new[] { creatorId }
                    .Concat(roles.AdminIds.Select(NormalizeId))
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var members = ids.Count == 0 ? [] : await bridge.GetMembersAsync(credentials, ids);
                var names = members
                    .GroupBy(item => NormalizeId(item.ZaloUserId), StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().DisplayName,
                        StringComparer.Ordinal);

                foreach (var id in ids)
                {
                    trustedById.TryGetValue(id, out var saved);
                    var name = names.GetValueOrDefault(id);
                    if (string.IsNullOrWhiteSpace(name)) name = saved?.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) name = id;
                    current[id] = (
                        name,
                        string.Equals(id, creatorId, StringComparison.Ordinal) ? "Creator" : "Admin",
                        string.Equals(id, creatorId, StringComparison.Ordinal));
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Keep Operations usable even when Zalo role lookup is temporarily unavailable.
            }
        }

        var result = current.Select(item =>
        {
            trustedById.TryGetValue(item.Key, out var saved);
            return new ZaloAutoSessionOrganizerCandidateResponse(
                item.Key,
                item.Value.Name,
                item.Value.Role,
                true,
                !item.Value.DefaultFallback && saved?.Enabled == true,
                item.Value.DefaultFallback);
        }).ToList();

        foreach (var saved in trusted.Where(item => item.Enabled))
        {
            var id = NormalizeId(saved.ZaloUserId);
            if (id.Length == 0 || current.ContainsKey(id)) continue;
            result.Add(new ZaloAutoSessionOrganizerCandidateResponse(
                id,
                string.IsNullOrWhiteSpace(saved.DisplayName) ? id : saved.DisplayName,
                "NoLongerAdmin",
                false,
                true,
                false));
        }

        return result
            .OrderByDescending(item => item.IsFallbackByDefault)
            .ThenByDescending(item => item.IsCurrentOrganizer)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

'''
    if helper_marker not in text:
        raise SystemExit('settings helper marker not found')
    text = text.replace(helper_marker, helper + helper_marker, 1)

path.write_text(text)
print('Trusted backup backend integration applied.')
