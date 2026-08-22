namespace VolleyDraft.Api.Services;

internal static class ZaloSemanticGuestPlanValidator
{
    public static ZaloSemanticGuestValidationResult Validate(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (plan.Action == ZaloSemanticGuestActionKind.None)
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_not_mutation");
        if (plan.Confidence < settings.MinimumConfidence)
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_low_confidence");

        return plan.Action switch
        {
            ZaloSemanticGuestActionKind.AddGuests => ValidateAdd(plan, snapshot, settings),
            ZaloSemanticGuestActionKind.UpdateGuestProfiles => ValidateUpdate(plan, snapshot, settings),
            ZaloSemanticGuestActionKind.CancelGuests => ValidateCancel(plan, snapshot, settings),
            _ => ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_action_not_allowed")
        };
    }

    private static ZaloSemanticGuestValidationResult ValidateAdd(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (snapshot.AnchorKind != ZaloSemanticGuestAnchorKind.RecruitmentBroadcast)
            return ZaloSemanticGuestValidationResult.Reject(
                plan,
                "semantic_guest_add_requires_recruitment_reply",
                "Muốn + bạn thì reply đúng tin `@all` tuyển người của tui nha.");
        if (!snapshot.AddWindowOpen)
            return ZaloSemanticGuestValidationResult.Reject(
                plan,
                "semantic_guest_add_window_closed",
                "Kèo này chưa mở nhận bạn ngoài group; tui vẫn ưu tiên anh em trong group vote trước nha.");
        if (plan.Quantity is not (1 or 2) || plan.QuantityConfidence < settings.MinimumConfidence)
            return ZaloSemanticGuestValidationResult.Reject(
                plan,
                "semantic_guest_quantity_ambiguous",
                "Ông muốn +1 hay +2 bạn vậy? Nói số lượng giúp tui nha.");

        var quantity = plan.Quantity.Value;
        var items = new List<ZaloSemanticGuestValidatedItem>();
        for (var index = 0; index < quantity; index += 1)
        {
            var source = index < plan.Guests.Count ? plan.Guests[index] : null;
            if (source?.ReservationId is not null)
                return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_add_fabricated_target");

            var displayName = source is not null &&
                              source.NameConfidence >= settings.MinimumConfidence &&
                              IsUsableExplicitName(source.DisplayName)
                ? source.DisplayName!.Trim()
                : null;
            var gender = source is not null && source.GenderConfidence >= settings.MinimumConfidence
                ? source.Gender
                : null;
            var level = source is not null && source.LevelConfidence >= settings.MinimumConfidence
                ? source.Level
                : null;
            var role = source is not null && source.RoleConfidence >= settings.MinimumConfidence
                ? source.Role
                : null;
            items.Add(new ZaloSemanticGuestValidatedItem(null, null, displayName, gender, level, role));
        }

        // Optional profile ambiguity never loses a slot. The validator simply drops
        // low-confidence optional fields; the deterministic result composer asks for
        // missing gender after the DB mutation succeeds.
        return new ZaloSemanticGuestValidationResult(
            true,
            "semantic_guest_add_ready",
            plan.Action,
            quantity,
            items,
            plan.NeedsClarification,
            plan.ClarificationReason);
    }

    private static ZaloSemanticGuestValidationResult ValidateUpdate(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (snapshot.AnchorKind is not (ZaloSemanticGuestAnchorKind.RecruitmentBroadcast or
            ZaloSemanticGuestAnchorKind.GuestConversation or
            ZaloSemanticGuestAnchorKind.ActiveGuestConversation))
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_update_without_context");

        var sources = plan.Guests.ToList();
        if (sources.Count == 0 && snapshot.ExistingGuests.Count == 1)
        {
            var only = snapshot.ExistingGuests[0];
            sources.Add(new ZaloSemanticGuestPlanItem(
                only.DisplayName,
                only.ReservationId,
                only.SponsorSequence,
                null,
                0,
                null,
                0,
                null,
                0,
                null,
                0,
                plan.Confidence));
        }
        if (sources.Count == 0)
            return ZaloSemanticGuestValidationResult.Reject(
                plan,
                "semantic_guest_update_target_ambiguous",
                "Ông đang nói guest nào vậy? Nói `#1`, `#2` hoặc tên giúp tui nha.");

        var items = new List<ZaloSemanticGuestValidatedItem>();
        foreach (var source in sources.Take(4))
        {
            if (source.Confidence < settings.MinimumConfidence && sources.Count > 1)
                return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_update_target_low_confidence");
            var grounded = ResolveGroundedGuest(source, snapshot);
            if (grounded is null)
                return ZaloSemanticGuestValidationResult.Reject(
                    plan,
                    "semantic_guest_invalid_guest_target",
                    "Tui chưa xác định chắc guest nào cần cập nhật nên chưa đổi gì nha.");

            var displayName = source.NameConfidence >= settings.MinimumConfidence && IsUsableExplicitName(source.DisplayName)
                ? source.DisplayName!.Trim()
                : null;
            var gender = source.GenderConfidence >= settings.MinimumConfidence ? source.Gender : null;
            var level = source.LevelConfidence >= settings.MinimumConfidence ? source.Level : null;
            var role = source.RoleConfidence >= settings.MinimumConfidence ? source.Role : null;
            if (displayName is null && gender is null && level is null && role is null)
                return ZaloSemanticGuestValidationResult.Reject(
                    plan,
                    "semantic_guest_profile_fields_ambiguous",
                    "Tui hiểu ông đang bổ sung hồ sơ nhưng chưa chắc thông tin nào cần đổi; nói rõ hơn giúp tui nha.");
            items.Add(new ZaloSemanticGuestValidatedItem(
                grounded.ReservationId,
                grounded.SponsorSequence,
                displayName,
                gender,
                level,
                role));
        }

        return new ZaloSemanticGuestValidationResult(
            true,
            "semantic_guest_update_ready",
            plan.Action,
            items.Count,
            items.DistinctBy(item => item.ReservationId, StringComparer.Ordinal).ToArray(),
            plan.NeedsClarification,
            plan.ClarificationReason);
    }

    private static ZaloSemanticGuestValidationResult ValidateCancel(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (snapshot.AnchorKind is not (ZaloSemanticGuestAnchorKind.RecruitmentBroadcast or
            ZaloSemanticGuestAnchorKind.GuestConversation or
            ZaloSemanticGuestAnchorKind.ActiveGuestConversation))
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_cancel_without_context");

        var resolved = plan.Guests
            .Where(item => item.Confidence >= settings.MinimumConfidence)
            .Select(item => ResolveGroundedGuest(item, snapshot))
            .Where(item => item is not null)
            .Cast<ZaloSemanticGuestGroundingGuest>()
            .DistinctBy(item => item.ReservationId, StringComparer.Ordinal)
            .Take(2)
            .ToList();
        if (resolved.Count == 0 && snapshot.ExistingGuests.Count == 1 && plan.Quantity is null or 1)
            resolved.Add(snapshot.ExistingGuests[0]);

        if (resolved.Count == 0 || plan.NeedsClarification)
            return ZaloSemanticGuestValidationResult.Reject(
                plan,
                "semantic_guest_cancel_target_ambiguous",
                "Tui chưa chắc bạn nào nghỉ. Nói `#1`, `#2` hoặc tên guest giúp tui nha.");

        return new ZaloSemanticGuestValidationResult(
            true,
            "semantic_guest_cancel_ready",
            plan.Action,
            resolved.Count,
            resolved.Select(item => new ZaloSemanticGuestValidatedItem(
                item.ReservationId,
                item.SponsorSequence,
                null,
                null,
                null,
                null)).ToArray(),
            false,
            string.Empty);
    }

    private static ZaloSemanticGuestGroundingGuest? ResolveGroundedGuest(
        ZaloSemanticGuestPlanItem source,
        ZaloSemanticGuestGroundingSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(source.ReservationId))
            return snapshot.ExistingGuests.SingleOrDefault(item =>
                string.Equals(item.ReservationId, source.ReservationId, StringComparison.Ordinal));
        if (source.SponsorSequence is { } sequence)
            return snapshot.ExistingGuests.SingleOrDefault(item => item.SponsorSequence == sequence);
        if (!string.IsNullOrWhiteSpace(source.ReferenceText))
        {
            var reference = ZaloBotIntelligence.Normalize(source.ReferenceText);
            var matches = snapshot.ExistingGuests.Where(item =>
                    ZaloBotIntelligence.Normalize(item.DisplayName).Contains(reference, StringComparison.Ordinal) ||
                    reference.Contains(ZaloBotIntelligence.Normalize(item.DisplayName), StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        return null;
    }

    internal static bool IsUsableExplicitName(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length is < 2 or > 80) return false;
        var normalized = ZaloBotIntelligence.Normalize(text);
        if (normalized is "ban" or "ban tui" or "ban toi" or "ban minh" or
            "thang ban" or "nho ban" or "dua ban" or "nguoi" or "khach" or "ban nha")
            return false;
        if (normalized.StartsWith("cho ban", StringComparison.Ordinal) ||
            normalized.StartsWith("them ban", StringComparison.Ordinal) ||
            normalized.EndsWith(" ban tui", StringComparison.Ordinal) ||
            normalized.EndsWith(" ban minh", StringComparison.Ordinal))
            return false;
        return true;
    }
}
