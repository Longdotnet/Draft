using VolleyDraft.Api.Models;

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
            ZaloSemanticGuestActionKind.AddGuests => ValidateAddLike(plan, snapshot, settings, tentative: false),
            ZaloSemanticGuestActionKind.AddTentativeGuests => ValidateAddLike(plan, snapshot, settings, tentative: true),
            ZaloSemanticGuestActionKind.ConfirmGuests => ValidateConfirm(plan, snapshot, settings),
            ZaloSemanticGuestActionKind.ReplaceGuest => ValidateReplace(plan, snapshot, settings),
            ZaloSemanticGuestActionKind.UpdateGuestProfiles => ValidateUpdate(plan, snapshot, settings),
            ZaloSemanticGuestActionKind.CancelGuests => ValidateCancel(plan, snapshot, settings),
            _ => ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_action_not_allowed")
        };
    }

    private static ZaloSemanticGuestValidationResult ValidateAddLike(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings,
        bool tentative)
    {
        var directRecruitment = snapshot.AnchorKind == ZaloSemanticGuestAnchorKind.RecruitmentBroadcast;
        var resumedPendingAdd = snapshot.AnchorKind == ZaloSemanticGuestAnchorKind.PendingGuestAction &&
                                !string.IsNullOrWhiteSpace(snapshot.RecruitmentMessageId) &&
                                snapshot.PendingMissingFields.Any(item =>
                                    string.Equals(item, "quantity", StringComparison.OrdinalIgnoreCase));
        if (!directRecruitment && !resumedPendingAdd)
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
                "Ông đang nói 1 hay 2 bạn vậy? Nói số lượng giúp tui nha.");

        var quantity = plan.Quantity.Value;
        var items = new List<ZaloSemanticGuestValidatedItem>();
        for (var index = 0; index < quantity; index += 1)
        {
            var source = index < plan.Guests.Count ? plan.Guests[index] : null;
            if (source?.ReservationId is not null)
                return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_add_fabricated_target");
            items.Add(ProfileFromNewGuest(source, settings));
        }

        return new ZaloSemanticGuestValidationResult(
            true,
            tentative ? "semantic_guest_tentative_ready" : "semantic_guest_add_ready",
            plan.Action,
            quantity,
            items,
            plan.NeedsClarification,
            plan.ClarificationReason);
    }

    private static ZaloSemanticGuestValidationResult ValidateConfirm(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (!HasGuestContext(snapshot))
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_confirm_without_context");
        var sources = plan.Guests.Where(item => item.Confidence >= settings.MinimumConfidence).Take(2).ToList();
        if (sources.Count == 0 && snapshot.ExistingGuests.Count(IsTentative) == 1)
        {
            var only = snapshot.ExistingGuests.Single(IsTentative);
            sources.Add(Target(only, plan.Confidence));
        }
        if (sources.Count == 0)
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_confirm_target_ambiguous", "Ông xác nhận guest nào đi vậy?");

        var items = new List<ZaloSemanticGuestValidatedItem>();
        foreach (var source in sources)
        {
            var resolution = ZaloSemanticGuestEntityResolver.Resolve(source, snapshot);
            if (resolution.Status != ZaloSemanticGuestEntityResolutionStatus.Resolved || resolution.Guest is null || !IsTentative(resolution.Guest))
                return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_confirm_target_ambiguous", "Tui chưa xác định được guest tentative nào vừa được chốt đi.");
            items.Add(new ZaloSemanticGuestValidatedItem(
                resolution.Guest.ReservationId, resolution.Guest.SponsorSequence, null, null, null, null));
        }
        return new(true, "semantic_guest_confirm_ready", plan.Action, items.Count, items, false, string.Empty);
    }

    private static ZaloSemanticGuestValidationResult ValidateReplace(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (!HasGuestContext(snapshot))
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_replace_without_context");
        if (!snapshot.AddWindowOpen)
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_add_window_closed", "Chưa tới cửa sổ nhận/thay bạn ngoài group nha.");
        if (plan.Guests.Count < 2)
            return ZaloSemanticGuestValidationResult.Reject(
                plan, "semantic_guest_replace_ambiguous", "Tui cần biết rõ bạn nào nghỉ và ai thay vào.");

        var oldSource = plan.Guests[0];
        var replacementSource = plan.Guests[1];
        if (oldSource.Confidence < settings.MinimumConfidence || replacementSource.Confidence < settings.MinimumConfidence)
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_replace_low_confidence");
        var oldResolution = ZaloSemanticGuestEntityResolver.Resolve(oldSource, snapshot);
        if (oldResolution.Status != ZaloSemanticGuestEntityResolutionStatus.Resolved || oldResolution.Guest is null)
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_replace_target_ambiguous", "Tui chưa chắc guest nào đang được thay.");
        if (replacementSource.ReservationId is not null || replacementSource.SponsorSequence is not null)
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_replace_new_target_fabricated");

        var replacement = ProfileFromNewGuest(replacementSource, settings);
        return new(
            true,
            "semantic_guest_replace_ready",
            plan.Action,
            1,
            [
                new ZaloSemanticGuestValidatedItem(
                    oldResolution.Guest.ReservationId, oldResolution.Guest.SponsorSequence, null, null, null, null),
                replacement
            ],
            plan.NeedsClarification,
            plan.ClarificationReason);
    }

    private static ZaloSemanticGuestValidationResult ValidateUpdate(
        ZaloSemanticGuestPlan plan,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (!HasGuestContext(snapshot))
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_update_without_context");

        var sources = plan.Guests.ToList();
        if (sources.Count == 0 && snapshot.ExistingGuests.Count == 1)
            sources.Add(Target(snapshot.ExistingGuests[0], plan.Confidence));
        if (sources.Count == 0)
            return ZaloSemanticGuestValidationResult.Reject(
                plan,
                "semantic_guest_update_target_ambiguous",
                "Ông đang nói guest nào vậy? Nói `#1`, `#2`, tên, `bạn đầu` hoặc `bạn thứ hai` giúp tui nha.");

        var items = new List<ZaloSemanticGuestValidatedItem>();
        foreach (var source in sources.Take(4))
        {
            if (source.Confidence < settings.MinimumConfidence && sources.Count > 1)
                return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_update_target_low_confidence");
            var resolution = ZaloSemanticGuestEntityResolver.Resolve(source, snapshot);
            if (resolution.Status != ZaloSemanticGuestEntityResolutionStatus.Resolved || resolution.Guest is null)
                return ZaloSemanticGuestValidationResult.Reject(
                    plan,
                    resolution.Status == ZaloSemanticGuestEntityResolutionStatus.Ambiguous
                        ? "semantic_guest_update_target_ambiguous"
                        : "semantic_guest_invalid_guest_target",
                    "Tui chưa xác định chắc guest nào cần cập nhật nên chưa đổi gì nha.");
            var grounded = resolution.Guest;
            var profile = ProfileFromExistingGuest(source, grounded, settings);
            if (profile.DisplayName is null && profile.Gender is null && profile.Level is null && profile.Role is null)
                return ZaloSemanticGuestValidationResult.Reject(
                    plan,
                    "semantic_guest_profile_fields_ambiguous",
                    "Tui hiểu ông đang bổ sung hồ sơ nhưng chưa chắc thông tin nào cần đổi; nói rõ hơn giúp tui nha.");
            items.Add(profile);
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
        if (!HasGuestContext(snapshot))
            return ZaloSemanticGuestValidationResult.Reject(plan, "semantic_guest_cancel_without_context");

        var confidentSources = plan.Guests.Where(item => item.Confidence >= settings.MinimumConfidence).ToList();
        var resolutions = confidentSources.Select(item => ZaloSemanticGuestEntityResolver.Resolve(item, snapshot)).ToList();
        var resolved = resolutions
            .Where(item => item.Status == ZaloSemanticGuestEntityResolutionStatus.Resolved && item.Guest is not null)
            .Select(item => item.Guest!)
            .DistinctBy(item => item.ReservationId, StringComparer.Ordinal)
            .Take(2)
            .ToList();

        if (resolved.Count == 0 && plan.Guests.Count == 0 && snapshot.ExistingGuests.Count == 1 && plan.Quantity is null or 1)
            resolved.Add(snapshot.ExistingGuests[0]);

        if (resolved.Count == 0 ||
            resolved.Count != confidentSources.Count ||
            resolutions.Any(item => item.Status != ZaloSemanticGuestEntityResolutionStatus.Resolved) ||
            plan.NeedsClarification)
            return ZaloSemanticGuestValidationResult.Reject(
                plan,
                "semantic_guest_cancel_target_ambiguous",
                "Tui chưa chắc bạn nào nghỉ. Nói `#1`, `#2`, tên, `bạn đầu` hoặc `bạn thứ hai` giúp tui nha.");

        return new ZaloSemanticGuestValidationResult(
            true,
            "semantic_guest_cancel_ready",
            plan.Action,
            resolved.Count,
            resolved.Select(item => new ZaloSemanticGuestValidatedItem(
                item.ReservationId, item.SponsorSequence, null, null, null, null)).ToArray(),
            false,
            string.Empty);
    }

    private static bool HasGuestContext(ZaloSemanticGuestGroundingSnapshot snapshot) =>
        snapshot.AnchorKind is ZaloSemanticGuestAnchorKind.RecruitmentBroadcast or
            ZaloSemanticGuestAnchorKind.GuestConversation or
            ZaloSemanticGuestAnchorKind.ActiveGuestConversation or
            ZaloSemanticGuestAnchorKind.PendingGuestAction or
            ZaloSemanticGuestAnchorKind.RecentGuestMutation;

    private static bool IsTentative(ZaloSemanticGuestGroundingGuest guest) =>
        string.Equals(guest.Status, ZaloGuestReservationStatus.Tentative.ToString(), StringComparison.OrdinalIgnoreCase);

    private static ZaloSemanticGuestPlanItem Target(ZaloSemanticGuestGroundingGuest guest, double confidence) => new(
        guest.DisplayName, guest.ReservationId, guest.SponsorSequence, null, 0, null, 0, null, 0, null, 0, confidence);

    private static ZaloSemanticGuestValidatedItem ProfileFromExistingGuest(
        ZaloSemanticGuestPlanItem source,
        ZaloSemanticGuestGroundingGuest grounded,
        ZaloSemanticActionSettings settings) => new(
            grounded.ReservationId,
            grounded.SponsorSequence,
            source.NameConfidence >= settings.MinimumConfidence && IsUsableExplicitName(source.DisplayName) ? source.DisplayName!.Trim() : null,
            source.GenderConfidence >= settings.MinimumConfidence ? source.Gender : null,
            source.LevelConfidence >= settings.MinimumConfidence ? source.Level : null,
            source.RoleConfidence >= settings.MinimumConfidence ? source.Role : null);

    private static ZaloSemanticGuestValidatedItem ProfileFromNewGuest(
        ZaloSemanticGuestPlanItem? source,
        ZaloSemanticActionSettings settings) => new(
            null,
            null,
            source is not null && source.NameConfidence >= settings.MinimumConfidence && IsUsableExplicitName(source.DisplayName) ? source.DisplayName!.Trim() : null,
            source is not null && source.GenderConfidence >= settings.MinimumConfidence ? source.Gender : null,
            source is not null && source.LevelConfidence >= settings.MinimumConfidence ? source.Level : null,
            source is not null && source.RoleConfidence >= settings.MinimumConfidence ? source.Role : null);

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
