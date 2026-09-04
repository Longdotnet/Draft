namespace VolleyDraft.Api.Services;

public sealed record ServiceResult<T>(bool IsSuccess, T? Value, int StatusCode, string? Error)
{
    public static ServiceResult<T> Success(T value) => new(true, value, StatusCodes.Status200OK, null);
    public static ServiceResult<T> Created(T value) => new(true, value, StatusCodes.Status201Created, null);
    public static ServiceResult<T> Failure(int statusCode, string error) =>
        new(false, default, statusCode, AddActionableIdentityConflictGuidance(statusCode, error));

    private static string AddActionableIdentityConflictGuidance(int statusCode, string error)
    {
        if (statusCode != StatusCodes.Status409Conflict ||
            !error.Contains("Xung đột định danh", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Cách xử lý:", StringComparison.OrdinalIgnoreCase))
        {
            return error;
        }

        return error +
               " Cách xử lý ngay trên Zalo: nếu @mention nhầm tài khoản thì mention lại đúng người. " +
               "Nếu UID đúng nhưng tên hồ sơ đang cũ, trưởng/phó nhóm hoặc operator/admin gửi `@Npc sửa identity @TênĐúng`; " +
               "bot sẽ cho chọn 1 giữ identity cũ, 2 đổi sang tên vừa mention, hoặc 3 huỷ. Bot không tự đổi UID hay tự merge hai hồ sơ.";
    }
}

public static class ServiceResultExtensions
{
    public static IResult ToHttpResult<T>(this ServiceResult<T> result)
    {
        if (result.IsSuccess)
        {
            return result.StatusCode == StatusCodes.Status201Created
                ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
                : Results.Ok(result.Value);
        }

        return Results.Json(
            new { message = result.Error },
            statusCode: result.StatusCode);
    }
}
