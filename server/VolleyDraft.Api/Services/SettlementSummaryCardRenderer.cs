using System.Globalization;
using SkiaSharp;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class SettlementSummaryCardRenderer
{
    private const string RosterBrandResourceName = "VolleyDraft.Api.Assets.Branding.ZyroHeaderLogo.png";
    private const int Width = 1080;
    private const int SidePadding = 64;
    private const int HeaderHeight = 238;
    private const int MetricTop = 280;
    private const int MetricHeight = 196;
    private const int ListTop = 530;
    private const int FooterHeight = 110;
    private const int MaxHeight = 8000;
    private const int RosterHeaderHeight = 220;
    private const int RosterOverviewTop = 246;
    private const int RosterOverviewHeight = 174;
    private const int RosterListTop = 452;
    private const int RosterFooterHeight = 96;
    private const int RosterSectionGap = 22;

    private static readonly SKColor Navy = new(5, 28, 68);
    private static readonly SKColor Red = new(205, 12, 42);
    private static readonly SKColor RedSoft = new(253, 235, 238);
    private static readonly SKColor Canvas = new(247, 249, 252);
    private static readonly SKColor Surface = SKColors.White;
    private static readonly SKColor Border = new(222, 228, 237);
    private static readonly SKColor Text = new(15, 35, 64);
    private static readonly SKColor Muted = new(93, 111, 137);
    private static readonly SKColor Success = new(25, 135, 84);
    private static readonly SKColor SuccessSoft = new(232, 247, 239);

    private static readonly SKTypeface RegularTypeface = FindTypeface(SKFontStyle.Normal);
    private static readonly SKTypeface BoldTypeface = FindTypeface(SKFontStyle.Bold);
    private static readonly SKTypeface BlackTypeface = FindTypeface(new SKFontStyle(900, 5, SKFontStyleSlant.Upright));
    private static readonly Lazy<SKBitmap?> RosterBrandLogo = new(LoadRosterBrandLogo);

    public byte[] Render(
        MatchSession session,
        MatchSettlement settlement,
        decimal groupFundBalance,
        decimal fundAppliedAmount = 0m)
    {
        var rows = settlement.Entries
            .Select(entry =>
            {
                var due = settlement.AmountPerFullSlot * entry.SlotFactor;
                var received = entry.PaymentStatus == SettlementPaymentStatus.Paid
                    ? (entry.ActualPaidAmount > 0 ? entry.ActualPaidAmount : due)
                    : 0m;
                return new SummaryRow(
                    entry.DisplayNameSnapshot.Trim(),
                    entry.PaymentStatus == SettlementPaymentStatus.Paid,
                    due,
                    received,
                    Math.Max(0m, received - due),
                    entry.SlotFactor);
            })
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var paid = rows.Where(row => row.IsPaid).ToList();
        var unpaid = rows.Where(row => !row.IsPaid).ToList();
        var layout = BuildSummaryLayoutPlan(paid.Count, unpaid.Count);
        var compactUnpaid = layout.Mode == SummaryLayoutMode.CompactUnpaid;
        var rowHeight = layout.RowHeight;
        var height = layout.ImageHeight;

        using var surface = SKSurface.Create(new SKImageInfo(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create settlement summary canvas.");
        var canvas = surface.Canvas;
        canvas.Clear(Canvas);

        DrawHeader(canvas, session, "TỔNG KẾT SỔ TIỀN");

        var derivedTotalDue = rows.Sum(row => row.Due);
        var totalDue = settlement.TotalDueOverride ?? derivedTotalDue;
        var totalReceived = rows.Sum(row => row.Received);
        var entrySurplus = rows.Sum(row => row.Surplus);
        var surplus = settlement.TotalDueOverride.HasValue
            ? Math.Max(0m, totalReceived - totalDue)
            : entrySurplus;
        var settledAmount = totalReceived + Math.Max(0m, fundAppliedAmount);
        DrawMetrics(
            canvas,
            settlement.AmountPerFullSlot,
            settlement.TotalDueOverride.HasValue,
            totalDue,
            totalReceived,
            surplus,
            Math.Max(0m, fundAppliedAmount),
            groupFundBalance,
            paid.Count,
            rows.Count);

        if (unpaid.Count == 0)
        {
            DrawSummaryPaidGrid(
                canvas,
                new SKRect(SidePadding, ListTop, Width - SidePadding, height - FooterHeight),
                paid,
                rowHeight);
        }
        else if (compactUnpaid)
        {
            var compactTop = height - FooterHeight - layout.CompactUnpaidHeight;
            DrawSummaryPaidGrid(
                canvas,
                new SKRect(SidePadding, ListTop, Width - SidePadding, compactTop - RosterSectionGap),
                paid,
                rowHeight);
            DrawCompactUnpaidGrid(
                canvas,
                new SKRect(SidePadding, compactTop, Width - SidePadding, height - FooterHeight),
                unpaid);
        }
        else
        {
            DrawRosterColumn(
                canvas,
                new SKRect(SidePadding, ListTop, Width / 2f - 16, height - FooterHeight),
                "ĐÃ ĐÓNG",
                paid,
                true,
                rowHeight);
            DrawRosterColumn(
                canvas,
                new SKRect(Width / 2f + 16, ListTop, Width - SidePadding, height - FooterHeight),
                "CHƯA ĐÓNG",
                unpaid,
                false,
                rowHeight);
        }

        DrawFooter(canvas, height, paid.Count == rows.Count && rows.Count > 0 && settledAmount >= totalDue);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 94);
        return data.ToArray();
    }

    public byte[] RenderPaymentRoster(
        MatchSession session,
        MatchSettlement settlement)
    {
        var roster = BuildPaymentRosterSnapshot(settlement);
        var paid = roster.PaidRows;
        var unpaid = roster.UnpaidRows;
        var preferredRowHeight = roster.TotalCount switch
        {
            <= 24 => 54,
            <= 48 => 48,
            <= 100 => 42,
            _ => 34
        };
        var totalGridRows = GridRowCount(paid.Count, true) + GridRowCount(unpaid.Count, false);
        var sectionCount = unpaid.Count > 0 ? 2 : 1;
        var sectionChromeHeight = sectionCount * 92 + (sectionCount - 1) * RosterSectionGap;
        var availableForRows = MaxHeight - RosterListTop - RosterFooterHeight - sectionChromeHeight - 24;
        var fitRowHeight = totalGridRows == 0 ? preferredRowHeight : Math.Max(26, availableForRows / totalGridRows);
        var rowHeight = Math.Min(preferredRowHeight, fitRowHeight);
        var paidSectionHeight = SectionHeight(paid.Count, true, rowHeight);
        var unpaidSectionHeight = unpaid.Count > 0 ? SectionHeight(unpaid.Count, false, rowHeight) : 0;
        var sectionsHeight = paidSectionHeight +
                             (unpaid.Count > 0 ? RosterSectionGap + unpaidSectionHeight : 0);
        var height = Math.Clamp(RosterListTop + sectionsHeight + RosterFooterHeight + 24, 980, MaxHeight);

        using var surface = SKSurface.Create(new SKImageInfo(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create settlement roster canvas.");
        var canvas = surface.Canvas;
        canvas.Clear(Canvas);

        DrawRosterHeader(canvas, session);
        DrawRosterOverview(canvas, settlement.AmountPerFullSlot, roster.TotalReceived, paid.Count, roster.TotalCount);
        DrawRosterPaidSection(
            canvas,
            new SKRect(SidePadding, RosterListTop, Width - SidePadding, RosterListTop + paidSectionHeight),
            paid,
            rowHeight);
        if (unpaid.Count > 0)
        {
            var unpaidTop = RosterListTop + paidSectionHeight + RosterSectionGap;
            DrawRosterUnpaidSection(
                canvas,
                new SKRect(SidePadding, unpaidTop, Width - SidePadding, unpaidTop + unpaidSectionHeight),
                unpaid,
                rowHeight);
        }

        DrawRosterFooter(canvas, height, paid.Count, roster.TotalCount);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 94);
        return data.ToArray();
    }

    internal static PaymentRosterSnapshot BuildPaymentRosterSnapshot(MatchSettlement settlement)
    {
        var paidRows = settlement.Entries
            .Where(entry => entry.PaymentStatus == SettlementPaymentStatus.Paid)
            .Select(entry =>
            {
                var due = settlement.AmountPerFullSlot * entry.SlotFactor;
                var received = entry.ActualPaidAmount > 0 ? entry.ActualPaidAmount : due;
                return new PaymentRosterRow(entry.DisplayNameSnapshot.Trim(), received, entry.SlotFactor);
            })
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var unpaidRows = settlement.Entries
            .Where(entry => entry.PaymentStatus != SettlementPaymentStatus.Paid)
            .Select(entry => new PaymentRosterUnpaidRow(
                entry.DisplayNameSnapshot.Trim(),
                settlement.AmountPerFullSlot * entry.SlotFactor,
                entry.SlotFactor))
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new PaymentRosterSnapshot(
            paidRows,
            unpaidRows,
            settlement.Entries.Count,
            paidRows.Sum(row => row.Received));
    }

    private static int GridColumnCount(int rowCount, bool paid) =>
        rowCount >= (paid ? 8 : 7) ? 2 : 1;

    private static int GridRowCount(int rowCount, bool paid)
    {
        if (rowCount <= 0) return 0;
        var columns = GridColumnCount(rowCount, paid);
        return (int)Math.Ceiling(rowCount / (double)columns);
    }

    private static int SectionHeight(int rowCount, bool paid, int rowHeight) =>
        92 + Math.Max(1, GridRowCount(rowCount, paid)) * rowHeight;

    internal static bool UseCompactUnpaidLayout(int unpaidCount) => unpaidCount is > 0 and <= 4;

    internal static SummaryLayoutPlan BuildSummaryLayoutPlan(int paidCount, int unpaidCount)
    {
        paidCount = Math.Max(0, paidCount);
        unpaidCount = Math.Max(0, unpaidCount);
        var mode = unpaidCount == 0
            ? SummaryLayoutMode.PaidOnly
            : UseCompactUnpaidLayout(unpaidCount)
                ? SummaryLayoutMode.CompactUnpaid
                : SummaryLayoutMode.Split;

        var paidColumns = mode == SummaryLayoutMode.Split ? 1 : paidCount >= 8 ? 2 : 1;
        var paidGridRows = paidCount == 0 ? 0 : (int)Math.Ceiling(paidCount / (double)paidColumns);
        var unpaidColumns = mode == SummaryLayoutMode.CompactUnpaid && unpaidCount > 1 ? 2 : 1;
        var unpaidGridRows = unpaidCount == 0 ? 0 : (int)Math.Ceiling(unpaidCount / (double)unpaidColumns);
        var sizingRows = mode == SummaryLayoutMode.Split
            ? Math.Max(4, Math.Max(paidCount, unpaidCount))
            : Math.Max(4, paidGridRows);
        var preferredRowHeight = sizingRows switch
        {
            <= 24 => 48,
            <= 36 => 42,
            _ => 36
        };
        var compactUnpaidHeight = mode == SummaryLayoutMode.CompactUnpaid
            ? 92 + Math.Max(1, unpaidGridRows) * 54 + 16
            : 0;
        var compactGap = mode == SummaryLayoutMode.CompactUnpaid ? RosterSectionGap : 0;
        // DrawRosterColumn and DrawSummaryPaidGrid both need at least 122px of chrome
        // above/below their data rows. Using the real chrome height prevents a planned
        // N-row section from collapsing into a false "… và N người khác" tail.
        var fixedHeight = ListTop + 122 + FooterHeight + compactUnpaidHeight + compactGap;
        var fitRowHeight = Math.Max(20, (MaxHeight - fixedHeight) / sizingRows);
        var rowHeight = Math.Min(preferredRowHeight, fitRowHeight);
        var imageHeight = Math.Clamp(fixedHeight + sizingRows * rowHeight, 1220, MaxHeight);

        return new SummaryLayoutPlan(
            mode,
            paidColumns,
            paidGridRows,
            unpaidColumns,
            unpaidGridRows,
            rowHeight,
            compactUnpaidHeight,
            imageHeight);
    }

    internal static (string Headline, string Metadata) BuildRosterHeaderCopy(MatchSession session)
    {
        if (session.StartTime is null)
        {
            var fallback = string.IsNullOrWhiteSpace(session.Name) ? "Trận ZYRO" : session.Name.Trim();
            var fallbackMeta = string.IsNullOrWhiteSpace(session.Location)
                ? "Thời gian đang cập nhật"
                : session.Location.Trim();
            return (fallback, fallbackMeta);
        }

        var culture = CultureInfo.GetCultureInfo("vi-VN");
        var local = session.StartTime.Value.ToOffset(TimeSpan.FromHours(7));
        var weekday = culture.TextInfo.ToTitleCase(local.ToString("dddd", culture));
        var headline = $"{weekday} • {local:dd/MM}";
        var metadata = $"Bắt đầu {local:HH:mm}";
        if (!string.IsNullOrWhiteSpace(session.Location))
            metadata += $" • {session.Location.Trim()}";
        return (headline, metadata);
    }

    internal static bool HasEmbeddedRosterBrandLogo() => RosterBrandLogo.Value is not null;

    private static void DrawRosterHeader(SKCanvas canvas, MatchSession session)
    {
        using (var fill = new SKPaint { Color = Surface, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, RosterHeaderHeight), fill);

        using (var topBar = new SKPaint { Color = Navy, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, 9), topBar);

        var copy = BuildRosterHeaderCopy(session);
        DrawRosterBrandLogo(canvas, new SKRect(SidePadding, 19, SidePadding + 255, 188));

        var badge = new SKRect(820, 37, Width - SidePadding, 82);
        DrawRoundedRect(canvas, badge, 23, SuccessSoft);
        DrawText(canvas, "THEO THỰC NHẬN", badge.MidX, 65, 14, Success, true, badge.Width - 28, BlackTypeface, SKTextAlign.Center);

        const float contentLeft = 360;
        DrawText(canvas, "DANH SÁCH ĐÓNG TIỀN", contentLeft, 88, 29, Navy, true, 430, BlackTypeface);
        DrawText(canvas, copy.Headline, contentLeft, 136, 25, Text, true, 590, BoldTypeface);
        DrawText(canvas, copy.Metadata, contentLeft, 174, 17, Muted, false, 590);

        using var divider = new SKPaint { Color = Border, StrokeWidth = 1f, IsAntialias = true };
        canvas.DrawLine(SidePadding, RosterHeaderHeight - 1, Width - SidePadding, RosterHeaderHeight - 1, divider);
    }

    private static void DrawRosterBrandLogo(SKCanvas canvas, SKRect destination)
    {
        var logo = RosterBrandLogo.Value;
        if (logo is null)
        {
            DrawText(canvas, "ZYRO", destination.Left, destination.MidY + 16, 42, Navy, true, destination.Width, BlackTypeface);
            return;
        }

        canvas.DrawBitmap(
            logo,
            new SKRect(0, 0, logo.Width, logo.Height),
            destination);
    }

    private static SKBitmap? LoadRosterBrandLogo()
    {
        using var stream = typeof(SettlementSummaryCardRenderer)
            .Assembly
            .GetManifestResourceStream(RosterBrandResourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        var encoded = reader.ReadToEnd().Trim();
        if (encoded.Length == 0) return null;

        try
        {
            return SKBitmap.Decode(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void DrawHeader(SKCanvas canvas, MatchSession session, string title)
    {
        using (var fill = new SKPaint { Color = Surface, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, HeaderHeight), fill);
        using (var topBar = new SKPaint { Color = Navy, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, 9), topBar);

        DrawRosterBrandLogo(canvas, new SKRect(SidePadding, 22, SidePadding + 255, 191));
        const float contentLeft = 360;
        DrawText(canvas, title, contentLeft, 88, 29, Navy, true, 470, BlackTypeface);
        DrawText(canvas, session.Name, contentLeft, 137, 27, Text, true, 590, BoldTypeface);

        var metadata = session.StartTime is null
            ? "Thông tin trận đang cập nhật"
            : session.StartTime.Value.ToOffset(TimeSpan.FromHours(7))
                .ToString("HH:mm • dddd, dd/MM/yyyy", CultureInfo.GetCultureInfo("vi-VN"));
        DrawText(canvas, metadata, contentLeft, 176, 17, Muted, false, 590);

        var badge = new SKRect(820, 39, Width - SidePadding, 84);
        DrawRoundedRect(canvas, badge, 23, SuccessSoft);
        DrawText(canvas, "ĐỐI SOÁT LIVE", badge.MidX, 67, 14, Success, true, badge.Width - 28, BlackTypeface, SKTextAlign.Center);

        using var divider = new SKPaint { Color = Border, StrokeWidth = 1f, IsAntialias = true };
        canvas.DrawLine(SidePadding, HeaderHeight - 1, Width - SidePadding, HeaderHeight - 1, divider);
    }

    private static void DrawMetrics(
        SKCanvas canvas,
        decimal amountPerFullSlot,
        bool totalDueOverridden,
        decimal totalDue,
        decimal totalReceived,
        decimal surplus,
        decimal fundAppliedAmount,
        decimal fund,
        int paidCount,
        int totalCount)
    {
        var outer = new SKRect(SidePadding, MetricTop, Width - SidePadding, MetricTop + MetricHeight);
        DrawRoundedRect(canvas, outer, 28, Surface, Border);

        var progressWidth = outer.Width - 64;
        var progress = totalCount == 0 ? 0f : Math.Clamp(paidCount / (float)totalCount, 0f, 1f);
        DrawText(canvas, $"{paidCount}/{totalCount} người đã đóng", outer.Left + 32, outer.Top + 42, 22, Text, true, 300);
        DrawText(canvas, totalCount > 0 && paidCount == totalCount ? "ĐÃ CHỐT SỔ" : $"CÒN {Math.Max(0, totalCount - paidCount)} NGƯỜI", outer.Right - 32, outer.Top + 42, 18, paidCount == totalCount ? Success : Red, true, 220, null, SKTextAlign.Right);
        DrawRoundedRect(canvas, new SKRect(outer.Left + 32, outer.Top + 60, outer.Right - 32, outer.Top + 73), 7, new SKColor(229, 234, 241));
        DrawRoundedRect(canvas, new SKRect(outer.Left + 32, outer.Top + 60, outer.Left + 32 + progressWidth * progress, outer.Top + 73), 7, paidCount == totalCount ? Success : Red);

        var cellWidth = (outer.Width - 64) / 4f;
        DrawMetric(canvas, outer.Left + 32, outer.Top + 111, cellWidth, "CẦN THU", Money(totalDue), Text);
        DrawMetric(canvas, outer.Left + 32 + cellWidth, outer.Top + 111, cellWidth, "THỰC NHẬN", Money(totalReceived), Navy);
        DrawMetric(
            canvas,
            outer.Left + 32 + cellWidth * 2,
            outer.Top + 111,
            cellWidth,
            fundAppliedAmount > 0 ? "BÙ TỪ QUỸ" : "DƯ TRẬN",
            fundAppliedAmount > 0 ? Money(fundAppliedAmount) : SignedMoney(surplus),
            fundAppliedAmount > 0 ? Red : surplus > 0 ? Success : Muted);
        DrawMetric(
            canvas,
            outer.Left + 32 + cellWidth * 3,
            outer.Top + 111,
            cellWidth,
            fund < 0 ? "QUỸ CẦN BÙ" : "QUỸ ZYRO",
            Money(fund),
            fund < 0 ? Red : Navy);
        DrawText(
            canvas,
            totalDueOverridden
                ? $"MỨC THU  •  1 slot {Money(amountPerFullSlot)}  •  1/2 slot {Money(amountPerFullSlot / 2m)}  •  TỔNG CẦN THU ĐÃ CHỈNH"
                : $"MỨC THU  •  1 slot {Money(amountPerFullSlot)}  •  1/2 slot {Money(amountPerFullSlot / 2m)}",
            outer.Left + 32,
            outer.Top + 181,
            13,
            Muted,
            true,
            outer.Width - 64,
            BlackTypeface);
    }

    private static void DrawRosterOverview(
        SKCanvas canvas,
        decimal amountPerFullSlot,
        decimal totalReceived,
        int paidCount,
        int totalCount)
    {
        var outer = new SKRect(SidePadding, RosterOverviewTop, Width - SidePadding, RosterOverviewTop + RosterOverviewHeight);
        DrawRoundedRect(canvas, outer, 28, Surface, Border);

        DrawText(canvas, "TỔNG ĐÃ THU", outer.Left + 32, outer.Top + 39, 14, Muted, true, 180, BlackTypeface);
        DrawText(canvas, Money(totalReceived), outer.Left + 32, outer.Top + 91, 46, Success, true, 440, BlackTypeface);
        DrawText(canvas, "Thực nhận đã ghi nhận trong Sổ tiền", outer.Left + 32, outer.Top + 119, 14, Muted, false, 430);

        var splitX = outer.Left + 526;
        using (var split = new SKPaint { Color = Border, StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawLine(splitX, outer.Top + 24, splitX, outer.Top + 116, split);

        var progressLeft = splitX + 32;
        var progressRight = outer.Right - 32;
        var progress = totalCount == 0 ? 0f : Math.Clamp(paidCount / (float)totalCount, 0f, 1f);
        DrawText(canvas, "TIẾN ĐỘ", progressLeft, outer.Top + 39, 14, Muted, true, 120, BlackTypeface);
        var unpaidCount = Math.Max(0, totalCount - paidCount);
        if (unpaidCount > 0)
        {
            var statusRect = new SKRect(progressRight - 190, outer.Top + 17, progressRight, outer.Top + 54);
            DrawRoundedRect(canvas, statusRect, 19, RedSoft);
            DrawText(
                canvas,
                $"CÒN {unpaidCount} CHƯA ĐÓNG",
                statusRect.MidX,
                statusRect.Top + 24,
                12,
                Red,
                true,
                statusRect.Width - 20,
                BlackTypeface,
                SKTextAlign.Center);
        }
        DrawText(canvas, $"{paidCount}/{totalCount}", progressLeft, outer.Top + 79, 31, Navy, true, 150, BlackTypeface);
        DrawText(canvas, "đã ghi nhận", progressLeft + 112, outer.Top + 76, 16, Muted, false, 160);
        DrawRoundedRect(canvas, new SKRect(progressLeft, outer.Top + 102, progressRight, outer.Top + 112), 5, new SKColor(231, 235, 242));
        if (progress > 0)
            DrawRoundedRect(canvas, new SKRect(progressLeft, outer.Top + 102, progressLeft + (progressRight - progressLeft) * progress, outer.Top + 112), 5, Success);

        using (var lowerDivider = new SKPaint { Color = new SKColor(235, 238, 243), StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawLine(outer.Left + 32, outer.Top + 136, outer.Right - 32, outer.Top + 136, lowerDivider);

        DrawText(canvas, "MỨC THU THAM KHẢO", outer.Left + 32, outer.Top + 158, 12, Muted, true, 190, BlackTypeface);
        DrawText(
            canvas,
            $"1 slot {Money(amountPerFullSlot)}   •   1/2 slot {Money(amountPerFullSlot / 2m)}",
            outer.Left + 235,
            outer.Top + 158,
            15,
            Text,
            false,
            outer.Width - 280);
    }

    private static void DrawRosterPaidSection(
        SKCanvas canvas,
        SKRect rect,
        IReadOnlyList<PaymentRosterRow> rows,
        int rowHeight)
    {
        DrawRoundedRect(canvas, rect, 28, Surface, Border);
        DrawText(canvas, "CHI TIẾT THỰC NHẬN", rect.Left + 28, rect.Top + 43, 18, Navy, true, 280, BlackTypeface);
        DrawText(canvas, $"{rows.Count} người", rect.Right - 28, rect.Top + 43, 15, Muted, false, 120, null, SKTextAlign.Right);

        using (var headerDivider = new SKPaint { Color = Border, StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawLine(rect.Left + 28, rect.Top + 66, rect.Right - 28, rect.Top + 66, headerDivider);

        if (rows.Count == 0)
        {
            DrawText(canvas, "Chưa có khoản thu nào được ghi nhận", rect.Left + 28, rect.Top + 122, 18, Muted, false, rect.Width - 56);
            return;
        }

        var columns = GridColumnCount(rows.Count, true);
        var rowsPerColumn = (int)Math.Ceiling(rows.Count / (double)columns);
        const float columnGap = 32f;
        var innerLeft = rect.Left + 28;
        var innerRight = rect.Right - 28;
        var columnWidth = columns == 1
            ? innerRight - innerLeft
            : (innerRight - innerLeft - columnGap) / 2f;
        var gridTop = rect.Top + 82;

        if (columns > 1)
        {
            var dividerX = innerLeft + columnWidth + columnGap / 2f;
            using var centerDivider = new SKPaint { Color = new SKColor(237, 240, 245), StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawLine(dividerX, gridTop, dividerX, rect.Bottom - 18, centerDivider);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var column = index / rowsPerColumn;
            var rowIndex = index % rowsPerColumn;
            var left = innerLeft + column * (columnWidth + columnGap);
            var top = gridTop + rowIndex * rowHeight;
            var centerY = top + rowHeight / 2f;
            var row = rows[index];

            if (rowIndex > 0)
            {
                using var divider = new SKPaint { Color = new SKColor(237, 240, 245), StrokeWidth = 1f, IsAntialias = true };
                canvas.DrawLine(left, top, left + columnWidth, top, divider);
            }

            var numberSize = rowHeight < 42 ? 24f : 30f;
            var numberRect = new SKRect(left, centerY - numberSize / 2f, left + numberSize, centerY + numberSize / 2f);
            DrawRoundedRect(canvas, numberRect, numberSize / 2f, new SKColor(240, 244, 249));
            DrawText(canvas, (index + 1).ToString("00", CultureInfo.InvariantCulture), numberRect.MidX, centerY + 4, rowHeight < 42 ? 9 : 11, Navy, true, numberSize - 4, BlackTypeface, SKTextAlign.Center);

            var hasHalfSlotLabel = row.SlotFactor == .5m && rowHeight >= 46;
            var nameSize = rowHeight >= 50 ? 17 : rowHeight >= 42 ? 15 : 13;
            var amountSize = rowHeight >= 50 ? 17 : rowHeight >= 42 ? 15 : 13;
            var nameBaseline = centerY + (hasHalfSlotLabel ? -1 : 6);
            DrawText(canvas, row.Name, left + numberSize + 12, nameBaseline, nameSize, Text, true, columnWidth - numberSize - 170);
            if (hasHalfSlotLabel)
                DrawText(canvas, "1/2 slot", left + numberSize + 12, nameBaseline + 17, 11, Muted, false, 70);
            DrawText(canvas, Money(row.Received), left + columnWidth, centerY + 6, amountSize, Success, true, 140, BlackTypeface, SKTextAlign.Right);
        }
    }

    private static void DrawSummaryPaidGrid(
        SKCanvas canvas,
        SKRect rect,
        IReadOnlyList<SummaryRow> rows,
        int rowHeight)
    {
        DrawRoundedRect(canvas, rect, 26, Surface, Border);
        var header = new SKRect(rect.Left + 16, rect.Top + 16, rect.Right - 16, rect.Top + 72);
        DrawRoundedRect(canvas, header, 18, SuccessSoft);
        DrawStatusIcon(canvas, header.Left + 27, header.MidY, 9, true);
        DrawText(canvas, "ĐÃ ĐÓNG • CHI TIẾT THỰC NHẬN", header.Left + 50, header.Top + 36, 17, Success, true, 390, BlackTypeface);
        DrawText(canvas, $"{rows.Count} người", header.Right - 18, header.Top + 36, 15, Muted, true, 110, null, SKTextAlign.Right);

        if (rows.Count == 0)
        {
            DrawText(canvas, "Chưa có người đã đóng", rect.Left + 30, rect.Top + 126, 18, Muted, false, rect.Width - 60);
            return;
        }

        var columns = rows.Count >= 8 ? 2 : 1;
        var rowsPerColumn = (int)Math.Ceiling(rows.Count / (double)columns);
        const float columnGap = 28f;
        var innerLeft = rect.Left + 28;
        var innerRight = rect.Right - 28;
        var columnWidth = columns == 1
            ? innerRight - innerLeft
            : (innerRight - innerLeft - columnGap) / 2f;
        var gridTop = rect.Top + 94;

        if (columns > 1)
        {
            var dividerX = innerLeft + columnWidth + columnGap / 2f;
            using var centerDivider = new SKPaint { Color = new SKColor(237, 240, 245), StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawLine(dividerX, gridTop, dividerX, rect.Bottom - 18, centerDivider);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var column = index / rowsPerColumn;
            var rowIndex = index % rowsPerColumn;
            var left = innerLeft + column * (columnWidth + columnGap);
            var top = gridTop + rowIndex * rowHeight;
            var centerY = top + rowHeight / 2f;
            var row = rows[index];

            if (rowIndex > 0)
            {
                using var divider = new SKPaint { Color = new SKColor(237, 240, 245), StrokeWidth = 1f, IsAntialias = true };
                canvas.DrawLine(left, top, left + columnWidth, top, divider);
            }

            DrawStatusIcon(canvas, left + 10, centerY, rowHeight < 40 ? 7f : 8f, true);
            var hasMeta = rowHeight >= 42 && (row.SlotFactor == .5m || row.Surplus > 0);
            var nameSize = rowHeight >= 46 ? 16 : rowHeight >= 40 ? 14 : 12;
            var amountSize = rowHeight >= 46 ? 16 : rowHeight >= 40 ? 14 : 12;
            var nameBaseline = centerY + (hasMeta ? -1 : 5);
            DrawText(canvas, row.Name, left + 30, nameBaseline, nameSize, Text, true, columnWidth - 190);
            if (row.SlotFactor == .5m && rowHeight >= 42)
                DrawText(canvas, "1/2 slot", left + 30, nameBaseline + 16, 10, Muted, false, 60);
            DrawText(canvas, Money(row.Received), left + columnWidth, nameBaseline + 5, amountSize, Navy, true, 135, BlackTypeface, SKTextAlign.Right);
            if (row.Surplus > 0 && rowHeight >= 42)
                DrawText(canvas, $"+{Money(row.Surplus)} quỹ", left + columnWidth, nameBaseline + 20, 10, Success, true, 135, null, SKTextAlign.Right);
        }
    }

    private static void DrawRosterUnpaidSection(
        SKCanvas canvas,
        SKRect rect,
        IReadOnlyList<PaymentRosterUnpaidRow> rows,
        int rowHeight)
    {
        DrawRoundedRect(canvas, rect, 28, Surface, Border);
        var header = new SKRect(rect.Left + 18, rect.Top + 16, rect.Right - 18, rect.Top + 72);
        DrawRoundedRect(canvas, header, 18, RedSoft);
        DrawStatusIcon(canvas, header.Left + 28, header.MidY, 11, false);
        DrawText(canvas, "CHƯA ĐÓNG", header.Left + 52, header.Top + 36, 18, Red, true, 210, BlackTypeface);
        DrawText(canvas, $"{rows.Count} người", header.Right - 18, header.Top + 36, 16, Muted, true, 120, null, SKTextAlign.Right);

        var columns = GridColumnCount(rows.Count, false);
        var rowsPerColumn = (int)Math.Ceiling(rows.Count / (double)columns);
        const float columnGap = 32f;
        var innerLeft = rect.Left + 28;
        var innerRight = rect.Right - 28;
        var columnWidth = columns == 1
            ? innerRight - innerLeft
            : (innerRight - innerLeft - columnGap) / 2f;
        var gridTop = rect.Top + 88;

        for (var index = 0; index < rows.Count; index++)
        {
            var column = index / rowsPerColumn;
            var rowIndex = index % rowsPerColumn;
            var left = innerLeft + column * (columnWidth + columnGap);
            var top = gridTop + rowIndex * rowHeight;
            var centerY = top + rowHeight / 2f;
            var row = rows[index];

            if (rowIndex > 0)
            {
                using var divider = new SKPaint { Color = new SKColor(237, 240, 245), StrokeWidth = 1f, IsAntialias = true };
                canvas.DrawLine(left, top, left + columnWidth, top, divider);
            }

            DrawStatusIcon(canvas, left + 12, centerY, rowHeight < 40 ? 8f : 10f, false);
            var hasHalfSlotLabel = row.SlotFactor == .5m && rowHeight >= 46;
            var nameSize = rowHeight >= 50 ? 17 : rowHeight >= 42 ? 15 : 13;
            var amountSize = rowHeight >= 50 ? 17 : rowHeight >= 42 ? 15 : 13;
            var nameBaseline = centerY + (hasHalfSlotLabel ? -1 : 6);
            DrawText(canvas, row.Name, left + 34, nameBaseline, nameSize, Text, true, columnWidth - 190);
            if (hasHalfSlotLabel)
                DrawText(canvas, "1/2 slot", left + 34, nameBaseline + 17, 11, Muted, false, 70);
            DrawText(canvas, Money(row.Due), left + columnWidth, centerY + 6, amountSize, Red, true, 140, BlackTypeface, SKTextAlign.Right);
        }
    }

    private static void DrawCompactUnpaidGrid(
        SKCanvas canvas,
        SKRect rect,
        IReadOnlyList<SummaryRow> rows)
    {
        DrawRoundedRect(canvas, rect, 24, Surface, Border);
        var header = new SKRect(rect.Left + 16, rect.Top + 14, rect.Right - 16, rect.Top + 66);
        DrawRoundedRect(canvas, header, 17, RedSoft);
        DrawStatusIcon(canvas, header.Left + 26, header.MidY, 9, false);
        DrawText(canvas, "CHƯA ĐÓNG", header.Left + 48, header.Top + 33, 16, Red, true, 180, BlackTypeface);
        DrawText(canvas, $"{rows.Count} người", header.Right - 16, header.Top + 33, 14, Muted, true, 100, null, SKTextAlign.Right);

        if (rows.Count == 0) return;
        var columns = rows.Count == 1 ? 1 : 2;
        var rowsPerColumn = (int)Math.Ceiling(rows.Count / (double)columns);
        const float columnGap = 24f;
        var innerLeft = rect.Left + 24;
        var innerRight = rect.Right - 24;
        var columnWidth = columns == 1
            ? innerRight - innerLeft
            : (innerRight - innerLeft - columnGap) / 2f;
        var rowHeight = 54f;
        var gridTop = rect.Top + 78;

        if (columns > 1)
        {
            var dividerX = innerLeft + columnWidth + columnGap / 2f;
            using var divider = new SKPaint { Color = new SKColor(237, 240, 245), StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawLine(dividerX, gridTop, dividerX, rect.Bottom - 14, divider);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var column = index / rowsPerColumn;
            var rowIndex = index % rowsPerColumn;
            var left = innerLeft + column * (columnWidth + columnGap);
            var top = gridTop + rowIndex * rowHeight;
            var centerY = top + rowHeight / 2f;
            var row = rows[index];
            if (rowIndex > 0)
            {
                using var rowDivider = new SKPaint { Color = new SKColor(237, 240, 245), StrokeWidth = 1f, IsAntialias = true };
                canvas.DrawLine(left, top, left + columnWidth, top, rowDivider);
            }

            DrawStatusIcon(canvas, left + 10, centerY, 8f, false);
            DrawText(canvas, row.Name, left + 30, centerY + 5, 15, Text, true, columnWidth - 180);
            if (row.SlotFactor == .5m)
                DrawText(canvas, "1/2", left + 30, centerY + 20, 10, Muted, false, 34);
            DrawText(canvas, Money(row.Due), left + columnWidth, centerY + 5, 15, Red, true, 130, BlackTypeface, SKTextAlign.Right);
        }
    }

    private static void DrawMetric(SKCanvas canvas, float x, float top, float width, string label, string value, SKColor valueColor)
    {
        DrawText(canvas, label, x, top, 15, Muted, true, width - 10);
        DrawText(canvas, value, x, top + 37, 26, valueColor, true, width - 10, BlackTypeface);
    }

    private static void DrawRosterColumn(
        SKCanvas canvas,
        SKRect rect,
        string title,
        IReadOnlyList<SummaryRow> rows,
        bool paid,
        int rowHeight,
        bool showFinancials = true)
    {
        DrawRoundedRect(canvas, rect, 26, Surface, Border);
        var headerFill = paid ? SuccessSoft : RedSoft;
        DrawRoundedRect(canvas, new SKRect(rect.Left + 16, rect.Top + 16, rect.Right - 16, rect.Top + 72), 18, headerFill);
        DrawText(
            canvas,
            title,
            rect.Left + 34,
            rect.Top + 52,
            19,
            paid ? Success : Red,
            true,
            Math.Max(210, rect.Width - 220),
            BlackTypeface);
        DrawText(canvas, $"{rows.Count} người", rect.Right - 34, rect.Top + 52, 17, Muted, true, 120, null, SKTextAlign.Right);

        if (rows.Count == 0)
        {
            DrawText(canvas, paid ? "Chưa có người đã đóng" : "Không còn ai chưa đóng", rect.Left + 34, rect.Top + 122, 19, Muted, false, rect.Width - 68);
            return;
        }

        var availableHeight = Math.Max(rowHeight, rect.Bottom - (rect.Top + 110) - 12);
        var maxVisibleRows = Math.Max(1, (int)Math.Floor(availableHeight / rowHeight));
        var hasOverflow = rows.Count > maxVisibleRows;
        var visibleRows = hasOverflow
            ? rows.Take(Math.Max(0, maxVisibleRows - 1)).ToList()
            : rows.ToList();
        var y = rect.Top + 110;
        for (var index = 0; index < visibleRows.Count; index++)
        {
            var row = visibleRows[index];
            if (index > 0)
            {
                using var divider = new SKPaint { Color = new SKColor(236, 240, 245), StrokeWidth = 1.2f, IsAntialias = true };
                canvas.DrawLine(rect.Left + 30, y - rowHeight / 2f, rect.Right - 30, y - rowHeight / 2f, divider);
            }
            var compact = rowHeight < 36;
            var badgeRadius = compact ? 8 : 10;
            var nameSize = compact ? 14 : rowHeight < 42 ? 16 : 19;
            var amountSize = compact ? 14 : rowHeight < 42 ? 16 : 18;
            DrawStatusIcon(canvas, rect.Left + 34, y - 7, badgeRadius, paid);
            DrawText(canvas, row.Name, rect.Left + 56, y, nameSize, Text, true, rect.Width - 210);

            var amount = paid && showFinancials ? row.Received : row.Due;
            DrawText(canvas, Money(amount), rect.Right - 28, y, amountSize, paid ? Navy : Red, true, 130, null, SKTextAlign.Right);
            if (showFinancials && rowHeight >= 42 && paid && row.Surplus > 0)
                DrawText(canvas, $"+{Money(row.Surplus)} quỹ", rect.Right - 28, y + 18, 12, Success, true, 140, null, SKTextAlign.Right);
            else if (rowHeight >= 42 && row.SlotFactor == .5m)
                DrawText(canvas, "1/2 slot", rect.Left + 56, y + 18, 12, Muted, false, 80);
            y += rowHeight;
        }

        if (hasOverflow)
        {
            if (visibleRows.Count > 0)
            {
                using var divider = new SKPaint { Color = new SKColor(236, 240, 245), StrokeWidth = 1.2f, IsAntialias = true };
                canvas.DrawLine(rect.Left + 30, y - rowHeight / 2f, rect.Right - 30, y - rowHeight / 2f, divider);
            }
            var remaining = rows.Count - visibleRows.Count;
            DrawText(canvas, $"… và {remaining} người khác", rect.Left + 34, y, rowHeight < 36 ? 13 : 16, Muted, true, rect.Width - 68);
        }
    }

    private static void DrawRosterFooter(SKCanvas canvas, int height, int paidCount, int totalCount)
    {
        using var line = new SKPaint { Color = new SKColor(223, 228, 236), StrokeWidth = 1.3f, IsAntialias = true };
        canvas.DrawLine(SidePadding, height - 70, Width - SidePadding, height - 70, line);
        DrawText(canvas, "SỔ TIỀN", SidePadding, height - 32, 15, Navy, true, 120, BlackTypeface);
        DrawText(
            canvas,
            $"{paidCount}/{totalCount} đã ghi nhận • Số tiền hiển thị = thực nhận",
            Width - SidePadding,
            height - 32,
            14,
            Muted,
            false,
            470,
            null,
            SKTextAlign.Right);
    }

    private static void DrawStatusIcon(
        SKCanvas canvas,
        float centerX,
        float centerY,
        float radius,
        bool paid)
    {
        using var fill = new SKPaint
        {
            Color = paid ? Success : Red,
            IsAntialias = true
        };
        canvas.DrawCircle(centerX, centerY, radius, fill);

        using var mark = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2f, radius * .23f),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
        if (paid)
        {
            canvas.DrawLine(centerX - radius * .42f, centerY, centerX - radius * .08f, centerY + radius * .32f, mark);
            canvas.DrawLine(centerX - radius * .08f, centerY + radius * .32f, centerX + radius * .48f, centerY - radius * .38f, mark);
            return;
        }

        canvas.DrawLine(centerX, centerY - radius * .42f, centerX, centerY + radius * .10f, mark);
        using var dot = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(centerX, centerY + radius * .48f, Math.Max(1.4f, radius * .12f), dot);
    }

    private static void DrawFooter(SKCanvas canvas, int height, bool settled)
    {
        using var line = new SKPaint { Color = new SKColor(223, 228, 236), StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawLine(SidePadding, height - 84, Width - SidePadding, height - 84, line);
        DrawText(canvas, "SỔ TIỀN • ĐỐI SOÁT", SidePadding, height - 42, 17, Navy, true, 260, BlackTypeface);
        DrawText(canvas, settled ? "Sổ đã chốt • Cảm ơn mọi người" : "Cập nhật trực tiếp từ Sổ tiền trận", Width - SidePadding, height - 42, 15, Muted, false, 380, null, SKTextAlign.Right);
    }

    private static string Money(decimal value) => $"{value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))}đ";
    private static string SignedMoney(decimal value) => value > 0 ? $"+{Money(value)}" : Money(value);

    private static void DrawRoundedRect(SKCanvas canvas, SKRect rect, float radius, SKColor fill, SKColor? border = null)
    {
        using var paint = new SKPaint { Color = fill, IsAntialias = true };
        canvas.DrawRoundRect(rect, radius, radius, paint);
        if (border is null) return;
        using var stroke = new SKPaint { Color = border.Value, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        canvas.DrawRoundRect(rect, radius, radius, stroke);
    }

    private static void DrawText(
        SKCanvas canvas,
        string value,
        float x,
        float baseline,
        float size,
        SKColor color,
        bool bold,
        float maxWidth = float.PositiveInfinity,
        SKTypeface? typeface = null,
        SKTextAlign align = SKTextAlign.Left)
    {
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            TextSize = size,
            Typeface = typeface ?? (bold ? BoldTypeface : RegularTypeface),
            TextAlign = align,
            SubpixelText = true
        };
        var text = FitText(value.Trim(), paint, maxWidth);
        canvas.DrawText(text, x, baseline, paint);
    }

    private static string FitText(string value, SKPaint paint, float maxWidth)
    {
        if (float.IsInfinity(maxWidth) || paint.MeasureText(value) <= maxWidth) return value;
        var text = value;
        while (text.Length > 1 && paint.MeasureText(text + "…") > maxWidth)
            text = text[..^1].TrimEnd();
        return text + "…";
    }

    private static SKTypeface FindTypeface(SKFontStyle style) =>
        SKTypeface.FromFamilyName("Noto Sans", style) ??
        SKTypeface.FromFamilyName("DejaVu Sans", style) ??
        SKTypeface.FromFamilyName("Segoe UI", style) ??
        SKTypeface.Default;

    private sealed record SummaryRow(
        string Name,
        bool IsPaid,
        decimal Due,
        decimal Received,
        decimal Surplus,
        decimal SlotFactor);

    internal sealed record PaymentRosterRow(string Name, decimal Received, decimal SlotFactor);

    internal sealed record PaymentRosterUnpaidRow(string Name, decimal Due, decimal SlotFactor);

    internal sealed record PaymentRosterSnapshot(
        IReadOnlyList<PaymentRosterRow> PaidRows,
        IReadOnlyList<PaymentRosterUnpaidRow> UnpaidRows,
        int TotalCount,
        decimal TotalReceived);

    internal enum SummaryLayoutMode
    {
        PaidOnly,
        CompactUnpaid,
        Split
    }

    internal sealed record SummaryLayoutPlan(
        SummaryLayoutMode Mode,
        int PaidColumns,
        int PaidGridRows,
        int UnpaidColumns,
        int UnpaidGridRows,
        int RowHeight,
        int CompactUnpaidHeight,
        int ImageHeight);
}
