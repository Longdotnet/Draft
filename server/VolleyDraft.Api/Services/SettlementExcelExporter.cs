using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record SettlementExcelFile(byte[] Content, string FileName);

public sealed class SettlementExcelExporter(VolleyDraftDbContext db)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly XLColor Navy = XLColor.FromHtml("#051C44");
    private static readonly XLColor Red = XLColor.FromHtml("#CD0C2A");
    private static readonly XLColor Green = XLColor.FromHtml("#198754");
    private static readonly XLColor GreenSoft = XLColor.FromHtml("#E8F7EF");
    private static readonly XLColor RedSoft = XLColor.FromHtml("#FDEBEE");
    private static readonly XLColor Canvas = XLColor.FromHtml("#F7F9FC");
    private static readonly XLColor Border = XLColor.FromHtml("#DEE4ED");
    private static readonly XLColor Muted = XLColor.FromHtml("#5D6F89");
    private const string MoneyFormat = "#,##0 [$₫-vi-VN]";

    public async Task<ServiceResult<SettlementExcelFile>> ExportAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var settlement = await db.MatchSettlements
            .AsNoTracking()
            .Include(item => item.Session)
            .Include(item => item.Entries)
            .Include(item => item.Messages)
            .SingleOrDefaultAsync(
                item => item.SessionId == sessionId && item.Session.AdminUserId == adminUserId,
                cancellationToken);
        if (settlement is null)
        {
            return ServiceResult<SettlementExcelFile>.Failure(
                StatusCodes.Status404NotFound,
                "Chưa có sổ tiền cho trận này.");
        }

        var connectionId = settlement.Session.ZaloConnectionId?.Trim();
        var groupId = settlement.Session.ZaloGroupId?.Trim();
        var fundTransactions = string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(groupId)
            ? []
            : await db.GroupFundTransactions
                .AsNoTracking()
                .Include(item => item.Settlement)
                    .ThenInclude(item => item.Session)
                .Where(item => item.ZaloConnectionId == connectionId && item.GroupId == groupId)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);

        byte[]? paymentQr = null;
        var qrAssetId = TryReadBotImageAssetId(settlement.Session.PaymentQrImageUrl);
        if (qrAssetId is not null)
        {
            paymentQr = await db.ZaloBotImageAssets.AsNoTracking()
                .Where(item => item.Id == qrAssetId && item.AdminUserId == adminUserId)
                .Select(item => item.Data)
                .SingleOrDefaultAsync(cancellationToken);
        }

        var entries = settlement.Entries
            .OrderBy(item => item.DisplayNameSnapshot, StringComparer.CurrentCultureIgnoreCase)
            .Select(item =>
            {
                var due = settlement.AmountPerFullSlot * item.SlotFactor;
                var received = item.PaymentStatus == SettlementPaymentStatus.Paid
                    ? (item.ActualPaidAmount > 0m ? item.ActualPaidAmount : due)
                    : 0m;
                var surplus = item.PaymentStatus == SettlementPaymentStatus.Paid
                    ? Math.Max(0m, received - due)
                    : 0m;
                return new PaymentRow(item, due, received, surplus);
            })
            .ToList();

        var derivedTotalDue = entries.Sum(item => item.Due);
        var totalDue = settlement.TotalDueOverride ?? derivedTotalDue;
        var totalReceived = entries.Sum(item => item.Received);
        var entrySurplus = entries.Sum(item => item.Surplus);
        var currentSurplus = settlement.TotalDueOverride.HasValue
            ? Math.Max(0m, totalReceived - totalDue)
            : entrySurplus;
        var groupFund = fundTransactions.Sum(item => item.Amount);
        var fundAppliedAmount = Math.Max(0m, -fundTransactions
            .Where(item => item.SettlementId == settlement.Id &&
                           item.Kind == GroupFundTransactionKind.SettlementFundCoverageAdjustment)
            .Sum(item => item.Amount));
        var settledAmount = totalReceived + fundAppliedAmount;
        var remainingAmount = Math.Max(0m, totalDue - settledAmount);
        var paidCount = entries.Count(item => item.Entry.PaymentStatus == SettlementPaymentStatus.Paid);

        using var workbook = new XLWorkbook();
        BuildOverviewSheet(
            workbook,
            settlement,
            entries,
            totalDue,
            totalReceived,
            currentSurplus,
            fundAppliedAmount,
            settledAmount,
            remainingAmount,
            groupFund,
            paidCount,
            paymentQr,
            fundTransactions);
        var paymentRowMap = BuildPaymentSheet(workbook, settlement, entries);
        BuildFundSheet(workbook, fundTransactions, groupFund, settlement.Id, paymentRowMap);
        BuildMessageSheet(workbook, settlement);
        BuildSourceSheet(workbook, settlement, entries, fundTransactions);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return ServiceResult<SettlementExcelFile>.Success(new SettlementExcelFile(
            stream.ToArray(),
            BuildFileName(settlement.Session)));
    }

    private static void BuildOverviewSheet(
        XLWorkbook workbook,
        MatchSettlement settlement,
        IReadOnlyList<PaymentRow> entries,
        decimal totalDue,
        decimal totalReceived,
        decimal currentSurplus,
        decimal fundAppliedAmount,
        decimal settledAmount,
        decimal remainingAmount,
        decimal groupFund,
        int paidCount,
        byte[]? paymentQr,
        IReadOnlyList<GroupFundTransaction> fundTransactions)
    {
        var ws = workbook.Worksheets.Add("01_TONG_QUAN");
        BaseSheet(ws);
        ws.ShowGridLines = false;
        ws.Column(1).Width = 3;
        ws.Columns(2, 9).Width = 16;
        ws.Column(10).Width = 3;

        var header = ws.Range("B2:I3").Merge();
        header.Value = "ZYRO VOLLEYBALL CLUB";
        header.Style.Fill.BackgroundColor = Navy;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Font.Bold = true;
        header.Style.Font.FontSize = 22;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        header.Style.Alignment.Indent = 1;

        ws.Range("B4:I4").Merge().Value = "BÁO CÁO SỔ TIỀN TRẬN";
        ws.Cell("B4").Style.Font.Bold = true;
        ws.Cell("B4").Style.Font.FontSize = 16;
        ws.Cell("B4").Style.Font.FontColor = Navy;

        ws.Range("B5:I5").Merge().Value = settlement.Session.Name;
        ws.Cell("B5").Style.Font.Bold = true;
        ws.Cell("B5").Style.Font.FontSize = 20;

        var when = settlement.Session.StartTime is { } start
            ? start.ToOffset(VietnamOffset).ToString("HH:mm • dddd, dd/MM/yyyy", Vi)
            : "Chưa cập nhật thời gian trận";
        var place = string.IsNullOrWhiteSpace(settlement.Session.Location) ? "Chưa cập nhật địa điểm" : settlement.Session.Location.Trim();
        var group = string.IsNullOrWhiteSpace(settlement.Session.ZaloGroupName) ? "Chưa gắn tên nhóm Zalo" : settlement.Session.ZaloGroupName.Trim();
        ws.Range("B6:I6").Merge().Value = $"{when}   |   {place}   |   {group}";
        ws.Cell("B6").Style.Font.FontColor = Muted;

        var statusText = settlement.IsClosed ? "ĐÃ CHỐT SỔ" : "ĐANG THEO DÕI";
        ws.Range("B8:C9").Merge();
        ws.Cell("B8").Value = statusText;
        ws.Cell("B8").Style.Fill.BackgroundColor = settlement.IsClosed ? GreenSoft : RedSoft;
        ws.Cell("B8").Style.Font.FontColor = settlement.IsClosed ? Green : Red;
        ws.Cell("B8").Style.Font.Bold = true;
        ws.Cell("B8").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell("B8").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        WriteKpi(ws, "D8:E9", "PHẢI THU", totalDue, Navy);
        WriteKpi(ws, "F8:G9", "THỰC NHẬN", totalReceived, Navy);
        WriteKpi(
            ws,
            "H8:I9",
            fundAppliedAmount > 0 ? "BÙ TỪ QUỸ" : "DƯ TRẬN NÀY",
            fundAppliedAmount > 0 ? fundAppliedAmount : currentSurplus,
            fundAppliedAmount > 0 ? Red : currentSurplus > 0 ? Green : Muted);

        WriteKpi(ws, "B11:D13", "QUỸ NHÓM HIỆN TẠI", groupFund, Red);
        ws.Range("E11:I13").Merge();
        var matchCount = fundTransactions.Select(item => item.SettlementId).Distinct().Count();
        ws.Cell("E11").Value = fundTransactions.Count == 0
            ? "Quỹ chưa có phát sinh. Khi có người đóng dư, hệ thống sẽ ghi rõ phát sinh theo từng trận."
            : $"Quỹ có {fundTransactions.Count} lần phát sinh thuộc {matchCount} trận. Mở sheet 03_SO_QUY để xem tiền dư, khoản bù thiếu và các lần hoàn quỹ.";
        ws.Cell("E11").Style.Alignment.WrapText = true;
        ws.Cell("E11").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell("E11").Style.Font.FontColor = Muted;
        ApplyCard(ws.Range("B11:I13"));

        var progress = entries.Count == 0 ? 0d : paidCount / (double)entries.Count;
        ws.Range("B15:I15").Merge().Value = $"TIẾN ĐỘ THU TIỀN   •   {paidCount}/{entries.Count} người đã đóng   •   {progress:P0}";
        ws.Cell("B15").Style.Font.Bold = true;
        ws.Cell("B15").Style.Font.FontColor = Navy;
        ws.Range("B16:I16").Merge();
        ws.Cell("B16").Value = progress;
        ws.Cell("B16").Style.NumberFormat.Format = "0%";
        var progressRule = ws.Cell("B16").AddConditionalFormat().DataBar(XLColor.FromHtml("#198754"));
        _ = progressRule;

        var unpaid = entries.Where(item => item.Entry.PaymentStatus == SettlementPaymentStatus.Unpaid).ToList();
        ws.Range("B18:F18").Merge().Value = "CẦN XỬ LÝ";
        ws.Cell("B18").Style.Font.Bold = true;
        ws.Cell("B18").Style.Font.FontColor = Red;
        ws.Range("B19:F22").Merge();
        ws.Cell("B19").Value = unpaid.Count == 0
            ? "Đã thu đủ tất cả thành viên. Có thể chốt sổ hoặc gửi ảnh tổng kết."
            : $"Còn {unpaid.Count} người chưa đóng, tổng còn phải thu {Money(unpaid.Sum(item => item.Due))}.\n{string.Join(", ", unpaid.Take(8).Select(item => item.Entry.DisplayNameSnapshot))}{(unpaid.Count > 8 ? $" và {unpaid.Count - 8} người khác" : string.Empty)}";
        ws.Cell("B19").Style.Alignment.WrapText = true;
        ws.Cell("B19").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ApplyCard(ws.Range("B19:F22"));

        ws.Range("G18:I18").Merge().Value = "THANH TOÁN";
        ws.Cell("G18").Style.Font.Bold = true;
        ws.Cell("G18").Style.Font.FontColor = Navy;
        var paymentText = string.IsNullOrWhiteSpace(settlement.Session.PaymentInstructions)
            ? "QR thanh toán được quản lý trực tiếp trong Sổ tiền."
            : settlement.Session.PaymentInstructions.Trim();

        if (paymentQr is { Length: > 0 })
        {
            ws.Range("G19:H22").Merge().Value = paymentText;
            ws.Cell("G19").Style.Alignment.WrapText = true;
            ws.Cell("G19").Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Range("I19:I22").Merge();
            ApplyCard(ws.Range("G19:I22"));
            try
            {
                using var qrStream = new MemoryStream(paymentQr);
                ws.AddPicture(qrStream)
                    .MoveTo(ws.Cell("I19"), 5, 4)
                    .WithSize(92, 92);
            }
            catch
            {
                // A workbook is still useful if an old stored image cannot be decoded by ClosedXML.
            }
        }
        else
        {
            ws.Range("G19:I22").Merge().Value = paymentText;
            ws.Cell("G19").Style.Alignment.WrapText = true;
            ws.Cell("G19").Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ApplyCard(ws.Range("G19:I22"));
        }

        ws.Range("B24:I25").Merge();
        ws.Cell("B24").Value = fundAppliedAmount > 0
            ? $"Quyết toán: {Money(totalReceived)} thực nhận + {Money(fundAppliedAmount)} bù từ quỹ = {Money(settledAmount)} / {Money(totalDue)} cần thu. Còn thiếu {Money(remainingAmount)}. • 03_SO_QUY ghi rõ bút toán rút/hoàn quỹ."
            : settlement.TotalDueOverride.HasValue
                ? $"Tổng phải thu đã được chỉnh trực tiếp thành {Money(totalDue)}. Mức thu từng người vẫn theo {Money(settlement.AmountPerFullSlot)}/1 slot. • 02_THU_TIEN đối soát từng người • 03_SO_QUY xem bút toán điều chỉnh."
                : "Cách đọc file: 02_THU_TIEN để đối soát từng người • 03_SO_QUY để xem quỹ từ trận nào • 04_NHAT_KY để xem lịch sử nhắc/gửi Zalo.";
        ws.Cell("B24").Style.Font.FontColor = Muted;
        ws.Cell("B24").Style.Alignment.WrapText = true;

        ws.Row(2).Height = 28;
        ws.Row(3).Height = 28;
        ws.Rows(8, 13).Height = 24;
        ws.Rows(19, 22).Height = 24;
        ws.SheetView.FreezeRows(6);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 1);
    }

    private static Dictionary<string, int> BuildPaymentSheet(
        XLWorkbook workbook,
        MatchSettlement settlement,
        IReadOnlyList<PaymentRow> entries)
    {
        var ws = workbook.Worksheets.Add("02_THU_TIEN");
        BaseSheet(ws);
        ws.ShowGridLines = false;
        WriteSheetTitle(ws, "ĐỐI SOÁT THU TIỀN", settlement.Session.Name, "Mỗi dòng giải thích số phải đóng, số thực nhận và phần dư vào quỹ bằng ngôn ngữ kế toán dễ đọc.");

        var startRow = 7;
        var headers = new[] { "STT", "Thành viên", "Slot", "Đơn giá", "Phải đóng", "Thực nhận", "Dư vào quỹ", "Trạng thái", "Giải thích", "Cập nhật", "Nguồn quỹ", "Hệ số" };
        for (var col = 1; col <= headers.Length; col++)
            ws.Cell(startRow, col).Value = headers[col - 1];
        StyleTableHeader(ws.Range(startRow, 1, startRow, headers.Length));

        var paymentRowMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var row = startRow + 1 + index;
            var item = entries[index];
            paymentRowMap[item.Entry.Id] = row;
            var paid = item.Entry.PaymentStatus == SettlementPaymentStatus.Paid;
            ws.Cell(row, 1).Value = index + 1;
            ws.Cell(row, 2).Value = item.Entry.DisplayNameSnapshot;
            ws.Cell(row, 3).Value = item.Entry.SlotFactor == .5m ? "1/2 slot" : "1 slot";
            ws.Cell(row, 4).Value = settlement.AmountPerFullSlot;
            ws.Cell(row, 5).FormulaA1 = $"=ROUND(D{row}*L{row},0)";
            ws.Cell(row, 6).Value = item.Received;
            ws.Cell(row, 7).FormulaA1 = $"=IF(H{row}=\"Đã đóng\",MAX(0,F{row}-E{row}),0)";
            ws.Cell(row, 8).Value = paid ? "Đã đóng" : "Chưa đóng";
            ws.Cell(row, 9).Value = paid
                ? item.Surplus > 0
                    ? $"{Money(item.Received)} nhận - {Money(item.Due)} phải đóng = +{Money(item.Surplus)} vào quỹ"
                    : $"{Money(item.Received)} nhận - {Money(item.Due)} phải đóng = đúng số tiền"
                : $"Chưa đóng - còn phải thu {Money(item.Due)}";
            ws.Cell(row, 10).Value = item.Entry.UpdatedAt.ToOffset(VietnamOffset).DateTime;
            ws.Cell(row, 11).Value = item.Surplus > 0 ? "Có phát sinh trong 03_SO_QUY" : "Không phát sinh";
            ws.Cell(row, 12).Value = item.Entry.SlotFactor;

            ws.Range(row, 4, row, 7).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 10).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            ws.Cell(row, 9).Style.Alignment.WrapText = true;
            ws.Cell(row, 11).Style.Font.FontColor = item.Surplus > 0 ? Green : Muted;
            ws.Cell(row, 8).Style.Fill.BackgroundColor = paid ? GreenSoft : RedSoft;
            ws.Cell(row, 8).Style.Font.FontColor = paid ? Green : Red;
            ws.Cell(row, 8).Style.Font.Bold = true;
        }

        var lastRow = Math.Max(startRow + 1, startRow + entries.Count);
        if (entries.Count > 0)
        {
            var totalRow = lastRow + 2;
            ws.Cell(totalRow, 4).Value = "TỔNG";
            ws.Cell(totalRow, 4).Style.Font.Bold = true;
            ws.Cell(totalRow, 5).FormulaA1 = $"=SUM(E{startRow + 1}:E{lastRow})";
            ws.Cell(totalRow, 6).FormulaA1 = $"=SUM(F{startRow + 1}:F{lastRow})";
            ws.Cell(totalRow, 7).FormulaA1 = $"=SUM(G{startRow + 1}:G{lastRow})";
            ws.Range(totalRow, 4, totalRow, 7).Style.Fill.BackgroundColor = Canvas;
            ws.Range(totalRow, 4, totalRow, 7).Style.Font.Bold = true;
            ws.Range(totalRow, 5, totalRow, 7).Style.NumberFormat.Format = MoneyFormat;
        }

        var dataRange = ws.Range(startRow, 1, lastRow, 11);
        ApplyBorders(dataRange);
        dataRange.SetAutoFilter();
        ws.SheetView.FreezeRows(startRow);
        ws.Column(1).Width = 6;
        ws.Column(2).Width = 24;
        ws.Column(3).Width = 10;
        ws.Columns(4, 7).Width = 15;
        ws.Column(8).Width = 13;
        ws.Column(9).Width = 44;
        ws.Column(10).Width = 19;
        ws.Column(11).Width = 24;
        ws.Column(12).Hide();
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        return paymentRowMap;
    }

    private static void BuildFundSheet(
        XLWorkbook workbook,
        IReadOnlyList<GroupFundTransaction> transactions,
        decimal groupFund,
        string currentSessionId,
        IReadOnlyDictionary<string, int> paymentRowMap)
    {
        var ws = workbook.Worksheets.Add("03_SO_QUY");
        BaseSheet(ws);
        ws.ShowGridLines = false;
        WriteSheetTitle(ws, "SỔ QUỸ NHÓM", "Quỹ được truy theo từng trận và từng lần điều chỉnh", "Số dư quỹ không nhập tay: phát sinh đến từ tiền dư, điều chỉnh tổng cần thu và các khoản rút/hoàn quỹ để bù thiếu cho trận.");

        ws.Range("A6:C8").Merge();
        ws.Cell("A6").Value = $"QUỸ HIỆN TẠI\n{Money(groupFund)}";
        ws.Cell("A6").Style.Fill.BackgroundColor = Navy;
        ws.Cell("A6").Style.Font.FontColor = XLColor.White;
        ws.Cell("A6").Style.Font.Bold = true;
        ws.Cell("A6").Style.Font.FontSize = 16;
        ws.Cell("A6").Style.Alignment.WrapText = true;
        ws.Cell("A6").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell("A6").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var grouped = transactions
            .GroupBy(item => new
            {
                item.SettlementId,
                Name = item.Settlement.Session.Name,
                item.Settlement.Session.StartTime
            })
            .Select(group => new
            {
                group.Key.SettlementId,
                group.Key.Name,
                group.Key.StartTime,
                Amount = group.Sum(item => item.Amount),
                Count = group.Count()
            })
            .OrderByDescending(item => item.StartTime)
            .ThenBy(item => item.Name)
            .ToList();

        ws.Range("E6:H6").Merge().Value = "QUỸ ĐẾN TỪ TRẬN NÀO?";
        ws.Cell("E6").Style.Font.Bold = true;
        ws.Cell("E6").Style.Font.FontColor = Navy;
        var summaryRow = 7;
        foreach (var match in grouped.Take(5))
        {
            ws.Cell(summaryRow, 5).Value = match.StartTime is { } start
                ? start.ToOffset(VietnamOffset).ToString("dd/MM/yyyy", Vi)
                : "—";
            ws.Cell(summaryRow, 6).Value = match.Name;
            ws.Cell(summaryRow, 7).Value = match.Amount;
            ws.Cell(summaryRow, 7).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(summaryRow, 8).Value = $"{match.Count} phát sinh";
            summaryRow++;
        }
        if (grouped.Count > 5)
        {
            ws.Cell(summaryRow, 6).Value = $"… và {grouped.Count - 5} trận khác - xem bảng chi tiết bên dưới";
            ws.Cell(summaryRow, 6).Style.Font.FontColor = Muted;
        }

        var startRow = Math.Max(14, summaryRow + 2);
        var headers = new[] { "Thời gian", "Trận", "Thành viên", "Phát sinh quỹ", "Dư trước", "Dư sau", "Giải thích", "Quỹ lũy kế", "Đối chiếu" };
        for (var col = 1; col <= headers.Length; col++)
            ws.Cell(startRow, col).Value = headers[col - 1];
        StyleTableHeader(ws.Range(startRow, 1, startRow, headers.Length));

        for (var index = 0; index < transactions.Count; index++)
        {
            var row = startRow + 1 + index;
            var item = transactions[index];
            ws.Cell(row, 1).Value = item.CreatedAt.ToOffset(VietnamOffset).DateTime;
            ws.Cell(row, 2).Value = item.Settlement.Session.Name;
            ws.Cell(row, 3).Value = item.DisplayNameSnapshot;
            ws.Cell(row, 4).Value = item.Amount;
            ws.Cell(row, 5).Value = item.SurplusBefore;
            ws.Cell(row, 6).Value = item.SurplusAfter;
            ws.Cell(row, 7).Value = item.Kind switch
            {
                GroupFundTransactionKind.SettlementFundCoverageAdjustment => item.Amount < 0
                    ? $"Rút {Money(Math.Abs(item.Amount))} từ quỹ để bù thiếu cho trận; mức bù {Money(item.SurplusBefore)} → {Money(item.SurplusAfter)}"
                    : $"Hoàn {Money(item.Amount)} về quỹ; mức bù {Money(item.SurplusBefore)} → {Money(item.SurplusAfter)}",
                GroupFundTransactionKind.SettlementTotalDueAdjustment => item.Amount >= 0
                    ? $"Điều chỉnh tổng cần thu làm dư trận thay đổi {Money(item.SurplusBefore)} → {Money(item.SurplusAfter)}, quỹ tăng {Money(item.Amount)}"
                    : $"Điều chỉnh tổng cần thu làm dư trận thay đổi {Money(item.SurplusBefore)} → {Money(item.SurplusAfter)}, quỹ giảm {Money(Math.Abs(item.Amount))}",
                _ => item.Amount >= 0
                    ? $"Dư thay đổi từ {Money(item.SurplusBefore)} → {Money(item.SurplusAfter)}, quỹ tăng {Money(item.Amount)}"
                    : $"Điều chỉnh dư từ {Money(item.SurplusBefore)} → {Money(item.SurplusAfter)}, quỹ giảm {Money(Math.Abs(item.Amount))}"
            };
            ws.Cell(row, 8).FormulaA1 = $"=SUM($D${startRow + 1}:D{row})";
            if (item.Kind == GroupFundTransactionKind.SettlementFundCoverageAdjustment)
            {
                ws.Cell(row, 9).Value = item.Amount < 0 ? "Bù thiếu từ quỹ" : "Hoàn quỹ";
                ws.Cell(row, 9).Style.Font.FontColor = item.Amount < 0 ? Red : Green;
            }
            else if (item.Kind == GroupFundTransactionKind.SettlementTotalDueAdjustment)
            {
                ws.Cell(row, 9).Value = "Điều chỉnh tổng cần thu";
                ws.Cell(row, 9).Style.Font.FontColor = Muted;
            }
            else if (item.SettlementId == currentSessionId && paymentRowMap.TryGetValue(item.SettlementEntryId, out var paymentRow))
            {
                ws.Cell(row, 9).FormulaA1 = $"=HYPERLINK(\"#'02_THU_TIEN'!B{paymentRow}\",\"Xem dòng thu tiền\")";
            }
            else
            {
                ws.Cell(row, 9).Value = "Phát sinh từ trận trước";
                ws.Cell(row, 9).Style.Font.FontColor = Muted;
            }

            ws.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            ws.Range(row, 4, row, 6).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 8).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 4).Style.Font.FontColor = item.Amount >= 0 ? Green : Red;
            ws.Cell(row, 4).Style.Font.Bold = true;
            ws.Cell(row, 7).Style.Alignment.WrapText = true;
        }

        var lastRow = Math.Max(startRow + 1, startRow + transactions.Count);
        if (transactions.Count == 0)
        {
            ws.Range(startRow + 1, 1, startRow + 2, 9).Merge().Value = "Chưa có phát sinh quỹ. Khi có người đóng dư, hệ thống sẽ ghi từng lần tăng/giảm tại đây.";
            ws.Cell(startRow + 1, 1).Style.Font.FontColor = Muted;
        }
        else
        {
            ApplyBorders(ws.Range(startRow, 1, lastRow, 9));
            ws.Range(startRow, 1, lastRow, 9).SetAutoFilter();
        }
        ws.SheetView.FreezeRows(startRow);
        ws.Column(1).Width = 19;
        ws.Column(2).Width = 25;
        ws.Column(3).Width = 23;
        ws.Columns(4, 6).Width = 15;
        ws.Column(7).Width = 52;
        ws.Column(8).Width = 16;
        ws.Column(9).Width = 20;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
    }

    private static void BuildMessageSheet(XLWorkbook workbook, MatchSettlement settlement)
    {
        var ws = workbook.Worksheets.Add("04_NHAT_KY");
        BaseSheet(ws);
        ws.ShowGridLines = false;
        WriteSheetTitle(ws, "NHẬT KÝ ZALO", settlement.Session.Name, "Dùng để kiểm tra đã nhắc, gửi QR hoặc gửi ảnh tổng kết lúc nào. Đây là lịch sử thao tác, không làm thay đổi số tiền.");

        var startRow = 7;
        var headers = new[] { "Tạo lúc", "Hành động", "Gửi cho", "Hẹn gửi", "Trạng thái", "Đã gửi lúc", "Có QR", "Nội dung / ghi chú" };
        for (var col = 1; col <= headers.Length; col++)
            ws.Cell(startRow, col).Value = headers[col - 1];
        StyleTableHeader(ws.Range(startRow, 1, startRow, headers.Length));

        var messages = settlement.Messages.OrderByDescending(item => item.CreatedAt).ToList();
        for (var index = 0; index < messages.Count; index++)
        {
            var row = startRow + 1 + index;
            var item = messages[index];
            ws.Cell(row, 1).Value = item.CreatedAt.ToOffset(VietnamOffset).DateTime;
            ws.Cell(row, 2).Value = ActionLabel(item.Kind);
            ws.Cell(row, 3).Value = AudienceLabel(item.Audience);
            ws.Cell(row, 4).Value = item.DueAt.ToOffset(VietnamOffset).DateTime;
            ws.Cell(row, 5).Value = MessageStatusLabel(item.Status);
            if (item.SentAt is { } sentAt)
                ws.Cell(row, 6).Value = sentAt.ToOffset(VietnamOffset).DateTime;
            else
                ws.Cell(row, 6).Value = "—";
            ws.Cell(row, 7).Value = item.IncludePaymentQr ? "Có" : "Không";
            ws.Cell(row, 8).Value = !string.IsNullOrWhiteSpace(item.LastError)
                ? $"{item.Message ?? ""}\nLỗi: {item.LastError}".Trim()
                : item.Message ?? (item.Kind == SettlementActionKind.Summary ? "Ảnh tổng kết sổ tiền" : "—");
            ws.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            ws.Cell(row, 4).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            if (item.SentAt is not null) ws.Cell(row, 6).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            ws.Cell(row, 8).Style.Alignment.WrapText = true;
            ws.Cell(row, 5).Style.Font.FontColor = item.Status == SettlementMessageStatus.Sent ? Green : item.Status == SettlementMessageStatus.Failed ? Red : Navy;
        }

        var lastRow = Math.Max(startRow + 1, startRow + messages.Count);
        if (messages.Count > 0)
        {
            ApplyBorders(ws.Range(startRow, 1, lastRow, 8));
            ws.Range(startRow, 1, lastRow, 8).SetAutoFilter();
        }
        ws.SheetView.FreezeRows(startRow);
        ws.Column(1).Width = 19;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 18;
        ws.Column(4).Width = 19;
        ws.Column(5).Width = 15;
        ws.Column(6).Width = 19;
        ws.Column(7).Width = 10;
        ws.Column(8).Width = 55;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
    }

    private static void BuildSourceSheet(
        XLWorkbook workbook,
        MatchSettlement settlement,
        IReadOnlyList<PaymentRow> entries,
        IReadOnlyList<GroupFundTransaction> transactions)
    {
        var ws = workbook.Worksheets.Add("99_NGUON_DU_LIEU");
        BaseSheet(ws);
        ws.Cell("A1").Value = "DỮ LIỆU ĐỐI SOÁT NỘI BỘ";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 16;
        ws.Cell("A3").Value = "Quy tắc dễ hiểu";
        ws.Cell("B3").Value = "Phải đóng từng người = đơn giá full slot × số slot. Tổng cần thu của trận có thể được chỉnh riêng. Dư/quỹ = tiền dư từng người cộng/trừ bút toán điều chỉnh tổng cần thu.";
        ws.Cell("B3").Style.Alignment.WrapText = true;
        ws.Cell("A4").Value = "TotalDueOverride";
        if (settlement.TotalDueOverride is { } totalDueOverride)
            ws.Cell("B4").Value = totalDueOverride;

        var row = 6;
        ws.Cell(row, 1).Value = "SettlementId";
        ws.Cell(row, 2).Value = "SessionId";
        ws.Cell(row, 3).Value = "EntryId";
        ws.Cell(row, 4).Value = "Tên";
        ws.Cell(row, 5).Value = "SlotFactor";
        ws.Cell(row, 6).Value = "PaymentStatus";
        ws.Cell(row, 7).Value = "AmountDue";
        ws.Cell(row, 8).Value = "ActualPaidAmount";
        ws.Cell(row, 9).Value = "Surplus";
        StyleTableHeader(ws.Range(row, 1, row, 9));
        foreach (var item in entries)
        {
            row++;
            ws.Cell(row, 1).Value = settlement.Id;
            ws.Cell(row, 2).Value = settlement.SessionId;
            ws.Cell(row, 3).Value = item.Entry.Id;
            ws.Cell(row, 4).Value = item.Entry.DisplayNameSnapshot;
            ws.Cell(row, 5).Value = item.Entry.SlotFactor;
            ws.Cell(row, 6).Value = item.Entry.PaymentStatus.ToString();
            ws.Cell(row, 7).Value = item.Due;
            ws.Cell(row, 8).Value = item.Received;
            ws.Cell(row, 9).Value = item.Surplus;
        }

        row += 3;
        ws.Cell(row, 1).Value = "FundTransactionId";
        ws.Cell(row, 2).Value = "SettlementId";
        ws.Cell(row, 3).Value = "EntryId";
        ws.Cell(row, 4).Value = "Tên";
        ws.Cell(row, 5).Value = "Amount";
        ws.Cell(row, 6).Value = "SurplusBefore";
        ws.Cell(row, 7).Value = "SurplusAfter";
        ws.Cell(row, 8).Value = "CreatedAt";
        StyleTableHeader(ws.Range(row, 1, row, 8));
        foreach (var item in transactions)
        {
            row++;
            ws.Cell(row, 1).Value = item.Id;
            ws.Cell(row, 2).Value = item.SettlementId;
            ws.Cell(row, 3).Value = item.SettlementEntryId;
            ws.Cell(row, 4).Value = item.DisplayNameSnapshot;
            ws.Cell(row, 5).Value = item.Amount;
            ws.Cell(row, 6).Value = item.SurplusBefore;
            ws.Cell(row, 7).Value = item.SurplusAfter;
            ws.Cell(row, 8).Value = item.CreatedAt.ToOffset(VietnamOffset).DateTime;
        }

        ws.Columns().AdjustToContents(1, 60);
        ws.Visibility = XLWorksheetVisibility.Hidden;
    }

    private static void BaseSheet(IXLWorksheet ws)
    {
        ws.Style.Font.FontName = "Aptos";
        ws.Style.Font.FontSize = 10.5;
        ws.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void WriteSheetTitle(IXLWorksheet ws, string title, string subtitle, string description)
    {
        ws.Range("A1:I2").Merge().Value = title;
        ws.Cell("A1").Style.Fill.BackgroundColor = Navy;
        ws.Cell("A1").Style.Font.FontColor = XLColor.White;
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 18;
        ws.Cell("A1").Style.Alignment.Indent = 1;
        ws.Range("A3:I3").Merge().Value = subtitle;
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A3").Style.Font.FontSize = 14;
        ws.Cell("A3").Style.Font.FontColor = Navy;
        ws.Range("A4:I5").Merge().Value = description;
        ws.Cell("A4").Style.Font.FontColor = Muted;
        ws.Cell("A4").Style.Alignment.WrapText = true;
    }

    private static void WriteKpi(IXLWorksheet ws, string rangeAddress, string label, decimal value, XLColor valueColor)
    {
        var range = ws.Range(rangeAddress).Merge();
        range.Value = $"{label}\n{Money(value)}";
        range.Style.Alignment.WrapText = true;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = valueColor;
        ApplyCard(range);
    }

    private static void ApplyCard(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = XLColor.White;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = Border;
    }

    private static void StyleTableHeader(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = Navy;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        range.Style.Border.BottomBorderColor = Navy;
    }

    private static void ApplyBorders(IXLRange range)
    {
        range.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        range.Style.Border.InsideBorderColor = Border;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = Border;
    }

    private static string ActionLabel(SettlementActionKind kind) => kind switch
    {
        SettlementActionKind.Reminder => "Nhắc đóng tiền",
        SettlementActionKind.Thanks => "Cảm ơn",
        SettlementActionKind.Summary => "Ảnh tổng kết",
        SettlementActionKind.PaymentQr => "Gửi QR thanh toán",
        _ => "Tin nhắn"
    };

    private static string AudienceLabel(SettlementAudience audience) => audience switch
    {
        SettlementAudience.Unpaid => "Người chưa đóng",
        SettlementAudience.Paid => "Người đã đóng",
        SettlementAudience.Selected => "Người được chọn",
        _ => "Cả nhóm"
    };

    private static string MessageStatusLabel(SettlementMessageStatus status) => status switch
    {
        SettlementMessageStatus.Pending => "Đang chờ",
        SettlementMessageStatus.Sending => "Đang gửi",
        SettlementMessageStatus.Sent => "Đã gửi",
        SettlementMessageStatus.Skipped => "Đã bỏ qua",
        SettlementMessageStatus.Failed => "Gửi lỗi",
        _ => status.ToString()
    };

    private static string Money(decimal value) => $"{value.ToString("N0", Vi)} ₫";

    private static string BuildFileName(MatchSession session)
    {
        var local = (session.StartTime ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);
        var cleanName = Regex.Replace(session.Name.Trim(), @"[^\p{L}\p{N}]+", "_").Trim('_');
        if (cleanName.Length > 50) cleanName = cleanName[..50].TrimEnd('_');
        if (cleanName.Length == 0) cleanName = "Tran";
        return $"ZYRO_SoTien_{cleanName}_{local:dd-MM-yyyy_HHmm}.xlsx";
    }

    private static string? TryReadBotImageAssetId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        const string marker = "/api/public/bot-images/";
        var index = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var id = Uri.UnescapeDataString(uri.AbsolutePath[(index + marker.Length)..]).Trim('/');
        return id.Length == 0 ? null : id;
    }

    private sealed record PaymentRow(SettlementEntry Entry, decimal Due, decimal Received, decimal Surplus);
}
