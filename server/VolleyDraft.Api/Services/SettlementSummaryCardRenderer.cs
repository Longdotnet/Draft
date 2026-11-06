using System.Globalization;
using SkiaSharp;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class SettlementSummaryCardRenderer
{
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

    public byte[] Render(
        MatchSession session,
        MatchSettlement settlement,
        decimal groupFundBalance)
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
        var longestColumn = Math.Max(4, Math.Max(paid.Count, unpaid.Count));
        var preferredRowHeight = longestColumn switch
        {
            <= 24 => 48,
            <= 36 => 42,
            _ => 36
        };
        var fixedHeight = ListTop + 92 + FooterHeight;
        var fitRowHeight = Math.Max(20, (MaxHeight - fixedHeight) / longestColumn);
        var rowHeight = Math.Min(preferredRowHeight, fitRowHeight);
        var height = Math.Clamp(fixedHeight + longestColumn * rowHeight, 1220, MaxHeight);

        using var surface = SKSurface.Create(new SKImageInfo(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create settlement summary canvas.");
        var canvas = surface.Canvas;
        canvas.Clear(Canvas);

        DrawHeader(canvas, session, "TỔNG KẾT SỔ TIỀN");

        var totalDue = rows.Sum(row => row.Due);
        var totalReceived = rows.Sum(row => row.Received);
        var surplus = rows.Sum(row => row.Surplus);
        DrawMetrics(canvas, totalDue, totalReceived, surplus, groupFundBalance, paid.Count, rows.Count);

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

        DrawFooter(canvas, height, paid.Count == rows.Count && rows.Count > 0);

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
        var preferredRowHeight = roster.TotalCount switch
        {
            <= 24 => 54,
            <= 48 => 48,
            <= 100 => 42,
            _ => 34
        };
        var totalGridRows = GridRowCount(paid.Count, true);
        var sectionChromeHeight = 92;
        var availableForRows = MaxHeight - RosterListTop - RosterFooterHeight - sectionChromeHeight - 24;
        var fitRowHeight = totalGridRows == 0 ? preferredRowHeight : Math.Max(26, availableForRows / totalGridRows);
        var rowHeight = Math.Min(preferredRowHeight, fitRowHeight);
        var sectionHeight = SectionHeight(paid.Count, true, rowHeight);
        var height = Math.Clamp(RosterListTop + sectionHeight + RosterFooterHeight + 24, 980, MaxHeight);

        using var surface = SKSurface.Create(new SKImageInfo(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create settlement roster canvas.");
        var canvas = surface.Canvas;
        canvas.Clear(Canvas);

        DrawRosterHeader(canvas, session);
        DrawRosterOverview(canvas, settlement.AmountPerFullSlot, roster.TotalReceived, paid.Count, roster.TotalCount);
        DrawRosterPaidSection(
            canvas,
            new SKRect(SidePadding, RosterListTop, Width - SidePadding, RosterListTop + sectionHeight),
            paid,
            rowHeight);

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
        return new PaymentRosterSnapshot(
            paidRows,
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

    private static void DrawRosterHeader(SKCanvas canvas, MatchSession session)
    {
        using (var fill = new SKPaint { Color = Navy, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, RosterHeaderHeight), fill);

        using (var red = new SKPaint { Color = Red, IsAntialias = true })
        {
            using var path = new SKPath();
            path.MoveTo(730, 0);
            path.LineTo(Width, 0);
            path.LineTo(Width, 82);
            path.LineTo(860, 58);
            path.Close();
            canvas.DrawPath(path, red);
        }

        var copy = BuildRosterHeaderCopy(session);
        DrawBrand(canvas, 64, 60);
        DrawText(canvas, "DANH SÁCH ĐÓNG TIỀN", 64, 118, 20, new SKColor(201, 215, 236), true, 520, BlackTypeface);
        DrawText(canvas, copy.Headline, 64, 166, 38, SKColors.White, true, 720, BlackTypeface);
        DrawText(canvas, copy.Metadata, 64, 201, 18, new SKColor(183, 200, 225), false, 730);
        DrawVolleyballMark(canvas, 930, 128, 54);
    }

    private static void DrawHeader(SKCanvas canvas, MatchSession session, string title)
    {
        using (var fill = new SKPaint { Color = Navy, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, HeaderHeight), fill);

        using (var red = new SKPaint { Color = Red, IsAntialias = true })
        {
            using var path = new SKPath();
            path.MoveTo(650, 0);
            path.LineTo(1080, 0);
            path.LineTo(1080, 105);
            path.LineTo(815, 68);
            path.Close();
            canvas.DrawPath(path, red);
        }

        DrawBrand(canvas, 64, 65);
        DrawText(canvas, title, 64, 142, 23, new SKColor(206, 218, 236), true, 520);
        DrawText(canvas, session.Name, 64, 192, 42, SKColors.White, true, 720, BlackTypeface);

        var metadata = session.StartTime is null
            ? "Thông tin trận đang cập nhật"
            : session.StartTime.Value.ToOffset(TimeSpan.FromHours(7))
                .ToString("HH:mm • dddd, dd/MM/yyyy", CultureInfo.GetCultureInfo("vi-VN"));
        DrawText(canvas, metadata, 64, 224, 19, new SKColor(184, 200, 224), false, 800);

        DrawVolleyballMark(canvas, 920, 146, 67);
    }

    private static void DrawBrand(SKCanvas canvas, float x, float baseline)
    {
        canvas.Save();
        canvas.Skew(-0.12f, 0);
        DrawText(canvas, "ZYRO", x + 8, baseline, 48, SKColors.White, true, 260, BlackTypeface);
        canvas.Restore();

        using var slash = new SKPaint
        {
            Color = Red,
            IsAntialias = true,
            StrokeWidth = 7,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(x + 8, baseline + 14, x + 210, baseline - 2, slash);
        DrawText(canvas, "VOLLEYBALL CLUB", x + 228, baseline - 4, 16, new SKColor(204, 216, 234), true, 250);
    }

    private static void DrawVolleyballMark(SKCanvas canvas, float centerX, float centerY, float radius)
    {
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(centerX, centerY, radius, fill);
        using var stroke = new SKPaint
        {
            Color = Navy,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 8,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawArc(new SKRect(centerX - radius + 8, centerY - radius + 8, centerX + radius - 8, centerY + radius - 8), -78, 112, false, stroke);
        canvas.DrawArc(new SKRect(centerX - radius * .9f, centerY - radius * .35f, centerX + radius * .95f, centerY + radius * .95f), 175, 92, false, stroke);
        canvas.DrawArc(new SKRect(centerX - radius * .95f, centerY - radius * .9f, centerX + radius * .25f, centerY + radius * .95f), -12, 105, false, stroke);
        using var accent = new SKPaint { Color = Red, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7 };
        canvas.DrawArc(new SKRect(centerX - radius - 18, centerY - radius - 16, centerX + radius + 20, centerY + radius + 12), 20, 56, false, accent);
    }

    private static void DrawMetrics(
        SKCanvas canvas,
        decimal totalDue,
        decimal totalReceived,
        decimal surplus,
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
        DrawMetric(canvas, outer.Left + 32 + cellWidth * 2, outer.Top + 111, cellWidth, "DƯ TRẬN", SignedMoney(surplus), surplus > 0 ? Success : Muted);
        DrawMetric(canvas, outer.Left + 32 + cellWidth * 3, outer.Top + 111, cellWidth, "QUỸ ZYRO", Money(fund), Red);
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

        var progressWidth = outer.Width - 64;
        var progress = totalCount == 0 ? 0f : Math.Clamp(paidCount / (float)totalCount, 0f, 1f);
        var settled = totalCount > 0 && paidCount == totalCount;
        DrawText(canvas, "TIẾN ĐỘ", outer.Left + 32, outer.Top + 28, 13, Muted, true, 120, BlackTypeface);
        DrawText(canvas, $"{paidCount}/{totalCount}", outer.Left + 32, outer.Top + 64, 34, Navy, true, 150, BlackTypeface);
        DrawText(canvas, "đã đóng", outer.Left + 151, outer.Top + 62, 18, Muted, false, 120);

        var statusWidth = settled ? 226f : 246f;
        var statusRect = new SKRect(outer.Right - 32 - statusWidth, outer.Top + 20, outer.Right - 32, outer.Top + 68);
        DrawRoundedRect(canvas, statusRect, 24, settled ? SuccessSoft : RedSoft);
        DrawStatusIcon(canvas, statusRect.Left + 25, statusRect.MidY, 11, settled);
        DrawText(
            canvas,
            settled ? "ĐÃ ĐÓNG ĐỦ" : $"CÒN {Math.Max(0, totalCount - paidCount)} CHƯA ĐÓNG",
            statusRect.Left + 48,
            statusRect.Top + 31,
            15,
            settled ? Success : Red,
            true,
            statusWidth - 58,
            BlackTypeface);

        DrawRoundedRect(canvas, new SKRect(outer.Left + 32, outer.Top + 82, outer.Right - 32, outer.Top + 92), 5, new SKColor(231, 235, 242));
        if (progress > 0)
            DrawRoundedRect(canvas, new SKRect(outer.Left + 32, outer.Top + 82, outer.Left + 32 + progressWidth * progress, outer.Top + 92), 5, settled ? Success : Red);

        var metricsLeft = outer.Left + 32;
        var metricsRight = outer.Right - 32;
        var metricWidth = (metricsRight - metricsLeft) / 3f;
        for (var index = 1; index <= 2; index++)
        {
            var dividerX = metricsLeft + metricWidth * index;
            using var divider = new SKPaint { Color = new SKColor(230, 235, 242), StrokeWidth = 1.2f, IsAntialias = true };
            canvas.DrawLine(dividerX, outer.Top + 112, dividerX, outer.Bottom - 22, divider);
        }

        DrawText(canvas, "1 SLOT", metricsLeft, outer.Top + 127, 13, Muted, true, 100, BlackTypeface);
        DrawText(canvas, Money(amountPerFullSlot), metricsLeft, outer.Top + 157, 24, Navy, true, metricWidth - 20, BlackTypeface);
        DrawText(canvas, "1/2 SLOT", metricsLeft + metricWidth + 24, outer.Top + 127, 13, Muted, true, 100, BlackTypeface);
        DrawText(canvas, Money(amountPerFullSlot / 2m), metricsLeft + metricWidth + 24, outer.Top + 157, 24, Navy, true, metricWidth - 44, BlackTypeface);
        DrawText(canvas, "TỔNG ĐÃ THU", metricsLeft + metricWidth * 2 + 24, outer.Top + 127, 13, Muted, true, 140, BlackTypeface);
        DrawText(canvas, Money(totalReceived), metricsLeft + metricWidth * 2 + 24, outer.Top + 157, 24, Success, true, metricWidth - 44, BlackTypeface);
    }

    private static void DrawRosterPaidSection(
        SKCanvas canvas,
        SKRect rect,
        IReadOnlyList<PaymentRosterRow> rows,
        int rowHeight)
    {
        DrawRoundedRect(canvas, rect, 28, Surface, Border);
        var header = new SKRect(rect.Left + 18, rect.Top + 16, rect.Right - 18, rect.Top + 72);
        DrawRoundedRect(canvas, header, 18, SuccessSoft);
        DrawStatusIcon(canvas, header.Left + 28, header.MidY, 11, true);
        DrawText(canvas, "ĐÃ ĐÓNG", header.Left + 52, header.Top + 36, 18, Success, true, 210, BlackTypeface);
        DrawText(canvas, $"{rows.Count} người", header.Right - 18, header.Top + 36, 16, Muted, true, 120, null, SKTextAlign.Right);

        if (rows.Count == 0)
        {
            DrawText(canvas, "Chưa có dữ liệu đóng tiền", rect.Left + 34, rect.Top + 124, 18, Muted, false, rect.Width - 68);
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

            var iconRadius = rowHeight < 40 ? 8f : 10f;
            DrawStatusIcon(canvas, left + 12, centerY, iconRadius, true);
            var hasHalfSlotLabel = row.SlotFactor == .5m && rowHeight >= 46;
            var nameSize = rowHeight >= 50 ? 18 : rowHeight >= 42 ? 16 : 14;
            var amountSize = rowHeight >= 50 ? 17 : rowHeight >= 42 ? 15 : 13;
            var nameBaseline = centerY + (hasHalfSlotLabel ? -1 : 6);
            DrawText(canvas, row.Name, left + 34, nameBaseline, nameSize, Text, true, columnWidth - 190);
            if (hasHalfSlotLabel)
                DrawText(canvas, "1/2 slot", left + 34, nameBaseline + 17, 11, Muted, false, 70);
            DrawText(canvas, Money(row.Received), left + columnWidth, centerY + 6, amountSize, Navy, true, 140, null, SKTextAlign.Right);
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
        DrawText(canvas, title, rect.Left + 34, rect.Top + 52, 19, paid ? Success : Red, true, 210, BlackTypeface);
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
        DrawText(canvas, "ZYRO • SỔ TIỀN", SidePadding, height - 32, 15, Navy, true, 230, BlackTypeface);
        var remaining = Math.Max(0, totalCount - paidCount);
        var status = totalCount > 0 && remaining == 0
            ? $"{paidCount}/{totalCount} đã đóng • Cập nhật từ Sổ tiền"
            : $"Còn {remaining} chưa đóng • Cập nhật từ Sổ tiền";
        DrawText(canvas, status, Width - SidePadding, height - 32, 14, Muted, false, 430, null, SKTextAlign.Right);
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
        DrawText(canvas, "ZYRO • VOLLEYBALL CLUB", SidePadding, height - 42, 17, Navy, true, 280, BlackTypeface);
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

    internal sealed record PaymentRosterSnapshot(
        IReadOnlyList<PaymentRosterRow> PaidRows,
        int TotalCount,
        decimal TotalReceived);
}
