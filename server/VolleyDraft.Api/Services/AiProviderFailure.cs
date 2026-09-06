using System.Net;
using System.Text.Json;

namespace VolleyDraft.Api.Services;

public enum AiProviderFailureKind
{
    NotConfigured,
    AuthenticationFailed,
    QuotaExceeded,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    ModelOrEndpointUnavailable,
    InvalidRequest,
    InvalidResponse,
    NetworkFailure,
    Cancelled,
    Unknown
}

public sealed record AiProviderFailure(
    AiProviderFailureKind Kind,
    int? StatusCode = null,
    string? ProviderCode = null,
    bool Retryable = false)
{
    public string ToUserMessage() => Kind switch
    {
        AiProviderFailureKind.NotConfigured =>
            "Phần AI của NPC chưa được cấu hình đầy đủ ở hệ thống. Các chức năng dùng dữ liệu và lệnh rõ ràng vẫn hoạt động bình thường.",
        AiProviderFailureKind.AuthenticationFailed =>
            "Phần AI của NPC đang lỗi xác thực/cấu hình phía hệ thống. Đây không phải do câu hỏi của bạn; các chức năng không cần AI vẫn dùng được.",
        AiProviderFailureKind.QuotaExceeded =>
            "AI của NPC hiện đã hết hạn mức sử dụng. Phần chat cần AI tạm thời không chạy, còn các lệnh bóng chuyền xử lý bằng dữ liệu hệ thống vẫn dùng được.",
        AiProviderFailureKind.RateLimited =>
            "Dịch vụ AI đang giới hạn số lượt gọi trong thời gian ngắn. NPC vẫn xử lý được các lệnh rõ ràng không cần AI; phần chat AI thử lại sau một chút nhé.",
        AiProviderFailureKind.Timeout =>
            "Dịch vụ AI phản hồi quá lâu nên NPC đã dừng chờ để tránh treo tin nhắn. Các chức năng không cần AI vẫn dùng bình thường.",
        AiProviderFailureKind.ProviderUnavailable =>
            "Nhà cung cấp AI đang tạm lỗi hoặc bảo trì. NPC vẫn dùng được những chức năng không phụ thuộc AI.",
        AiProviderFailureKind.ModelOrEndpointUnavailable =>
            "Model hoặc đường dẫn AI đang không khả dụng ở phía hệ thống. Các chức năng không cần AI vẫn hoạt động.",
        AiProviderFailureKind.InvalidRequest =>
            "Phần AI của NPC đang gặp lỗi cấu hình/yêu cầu phía hệ thống nên chưa xử lý được câu chat này. Các lệnh không cần AI vẫn dùng bình thường.",
        AiProviderFailureKind.InvalidResponse =>
            "Dịch vụ AI có phản hồi nhưng dữ liệu trả về không đúng định dạng NPC cần. Các chức năng không cần AI vẫn hoạt động.",
        AiProviderFailureKind.NetworkFailure =>
            "Máy chủ NPC hiện không kết nối được tới nhà cung cấp AI. Các chức năng không cần AI vẫn dùng bình thường.",
        AiProviderFailureKind.Cancelled =>
            "Yêu cầu AI đã bị dừng trước khi hoàn tất. Các chức năng không cần AI vẫn hoạt động.",
        _ =>
            "Phần AI của NPC đang gặp lỗi chưa xác định. Các chức năng không cần AI vẫn dùng được; lỗi đã được ghi nhận để kiểm tra."
    };

    public static AiProviderFailure FromHttp(HttpStatusCode statusCode, string? responseBody)
    {
        var numeric = (int)statusCode;
        var code = ExtractProviderCode(responseBody);
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(AiProviderFailureKind.AuthenticationFailed, numeric, code);
        if (numeric == 402)
            return new(AiProviderFailureKind.QuotaExceeded, numeric, code);
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return LooksLikeQuotaFailure(responseBody)
                ? new(AiProviderFailureKind.QuotaExceeded, numeric, code)
                : new(AiProviderFailureKind.RateLimited, numeric, code, Retryable: true);
        }
        if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
            return new(AiProviderFailureKind.Timeout, numeric, code, Retryable: true);
        if (statusCode == HttpStatusCode.NotFound)
            return new(AiProviderFailureKind.ModelOrEndpointUnavailable, numeric, code);
        if (numeric is 400 or 405 or 409 or 415 or 422)
            return new(AiProviderFailureKind.InvalidRequest, numeric, code);
        if (numeric >= 500)
            return new(AiProviderFailureKind.ProviderUnavailable, numeric, code, Retryable: true);
        return new(AiProviderFailureKind.Unknown, numeric, code);
    }

    public static AiProviderFailure FromException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TaskCanceledException or OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? new(AiProviderFailureKind.Cancelled)
                : new(AiProviderFailureKind.Timeout, Retryable: true);
        }
        if (exception is HttpRequestException)
            return new(AiProviderFailureKind.NetworkFailure, Retryable: true);
        if (exception is JsonException or InvalidOperationException)
            return new(AiProviderFailureKind.InvalidResponse);
        return new(AiProviderFailureKind.Unknown);
    }

    internal static bool LooksLikeQuotaFailure(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var normalized = body.ToLowerInvariant();
        string[] markers =
        [
            "insufficient_quota",
            "quota exceeded",
            "quota_exceeded",
            "out of credits",
            "no credits",
            "credit balance",
            "billing",
            "payment required",
            "usage limit reached"
        ];
        return markers.Any(normalized.Contains);
    }

    internal static string? ExtractProviderCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyName in new[] { "code", "type" })
                {
                    if (error.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                        return SanitizeCode(value.GetString());
                }
            }
            foreach (var propertyName in new[] { "code", "type" })
            {
                if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                    return SanitizeCode(value.GetString());
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static string? SanitizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.').Take(80).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? null : safe;
    }
}
