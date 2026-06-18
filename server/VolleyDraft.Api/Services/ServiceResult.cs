namespace VolleyDraft.Api.Services;

public sealed record ServiceResult<T>(bool IsSuccess, T? Value, int StatusCode, string? Error)
{
    public static ServiceResult<T> Success(T value) => new(true, value, StatusCodes.Status200OK, null);
    public static ServiceResult<T> Created(T value) => new(true, value, StatusCodes.Status201Created, null);
    public static ServiceResult<T> Failure(int statusCode, string error) => new(false, default, statusCode, error);
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
