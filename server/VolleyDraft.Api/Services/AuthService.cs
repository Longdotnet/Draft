using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class AuthService(
    VolleyDraftDbContext db,
    PasswordService passwordService,
    JwtTokenService jwtTokenService)
{
    public async Task<ServiceResult<AuthResponse>> RegisterAsync(RegisterRequest request)
    {
        var displayName = request.DisplayName.Trim();
        var email = request.Email.Trim().ToLowerInvariant();

        if (displayName.Length == 0 || email.Length == 0 || request.Password.Length < 6)
        {
            return ServiceResult<AuthResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Display name, email, and a password of at least 6 characters are required.");
        }

        var exists = await db.Users.AnyAsync(user => user.Email == email);
        if (exists)
        {
            return ServiceResult<AuthResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Email is already registered.");
        }

        var user = new User
        {
            DisplayName = displayName,
            Email = email,
            PasswordHash = passwordService.Hash(request.Password)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return ServiceResult<AuthResponse>.Created(ToAuthResponse(user));
    }

    public async Task<ServiceResult<AuthResponse>> LoginAsync(LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(item => item.Email == email);

        if (user is null || !passwordService.Verify(request.Password, user.PasswordHash))
        {
            return ServiceResult<AuthResponse>.Failure(
                StatusCodes.Status401Unauthorized,
                "Invalid email or password.");
        }

        return ServiceResult<AuthResponse>.Success(ToAuthResponse(user));
    }

    public async Task<ServiceResult<UserDto>> MeAsync(string userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            return ServiceResult<UserDto>.Failure(StatusCodes.Status401Unauthorized, "Unauthorized.");
        }

        return ServiceResult<UserDto>.Success(ToUserDto(user));
    }

    private AuthResponse ToAuthResponse(User user)
    {
        return new AuthResponse(jwtTokenService.CreateToken(user), ToUserDto(user));
    }

    private static UserDto ToUserDto(User user)
    {
        return new UserDto(user.Id, user.DisplayName, user.Email);
    }
}
