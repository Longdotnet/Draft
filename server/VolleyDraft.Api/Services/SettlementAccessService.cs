using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record SettlementAccessAuthorization(
    string AdminUserId,
    string SessionId,
    string SessionName,
    DateTimeOffset? StartTime,
    string ZaloUserId,
    string? DisplayName,
    DateTimeOffset ExpiresAt);

public sealed record SettlementAccessRedemption(
    SettlementAccessChallengeStatusResponse Response,
    string? AccessToken,
    DateTimeOffset? AccessExpiresAt);

public sealed class SettlementAccessService
{
    private readonly VolleyDraftDbContext db;
    private readonly Func<string, string, string, Task<ServiceResult<ZaloGroupRoleAuthorization>>> roleResolver;

    public SettlementAccessService(VolleyDraftDbContext db, ZaloIntegrationService integration)
        : this(db, integration.GetGroupRoleAuthorizationAsync)
    {
    }

    internal SettlementAccessService(
        VolleyDraftDbContext db,
        Func<string, string, string, Task<ServiceResult<ZaloGroupRoleAuthorization>>> roleResolver)
    {
        this.db = db;
        this.roleResolver = roleResolver;
    }

    public const string CookieName = "vd_settlement_access";
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan RoleRefreshInterval = TimeSpan.FromMinutes(5);
    private const int PerRequesterChallengeLimit = 8;
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<ServiceResult<SettlementAccessChallengeResponse>> CreateChallengeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => await CreateChallengeAsync(sessionId, "unknown", cancellationToken);

    public async Task<ServiceResult<SettlementAccessChallengeResponse>> CreateChallengeAsync(
        string sessionId,
        string requesterKey,
        CancellationToken cancellationToken = default)
    {
        sessionId = (sessionId ?? string.Empty).Trim();
        requesterKey = string.IsNullOrWhiteSpace(requesterKey) ? "unknown" : requesterKey.Trim();
        if (sessionId.Length == 0)
            return ServiceResult<SettlementAccessChallengeResponse>.Failure(StatusCodes.Status400BadRequest, "Thiếu sessionId.");

        var session = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null || session.ZaloConnection is null ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId) ||
            string.IsNullOrWhiteSpace(session.ZaloConnection.AccountZaloId))
        {
            return ServiceResult<SettlementAccessChallengeResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Trận này chưa được liên kết đầy đủ với nhóm Zalo.");
        }

        if (!await db.MatchSettlements.AsNoTracking().AnyAsync(item => item.SessionId == sessionId, cancellationToken))
        {
            return ServiceResult<SettlementAccessChallengeResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Chưa có sổ tiền cho trận này.");
        }

        var recentCutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        var requesterKeyHash = Hash($"settlement-challenge-v1|{requesterKey}");
        var recentRows = await db.SettlementAccessChallenges.AsNoTracking()
            .Where(item => item.SessionId == sessionId)
            .Select(item => new { item.CreatedAt, item.RequesterKeyHash })
            .ToListAsync(cancellationToken);
        var recent = recentRows.Where(item => item.CreatedAt >= recentCutoff).ToList();
        if (recent.Count(item => item.RequesterKeyHash == requesterKeyHash) >= PerRequesterChallengeLimit)
        {
            return ServiceResult<SettlementAccessChallengeResponse>.Failure(
                StatusCodes.Status429TooManyRequests,
                "Thiết bị này đã tạo quá nhiều mã xác minh. Vui lòng thử lại sau ít phút.");
        }
        var code = await GenerateUniqueCodeAsync(cancellationToken);
        var redemptionSecret = CreateOpaqueToken();
        var now = DateTimeOffset.UtcNow;
        var challenge = new SettlementAccessChallenge
        {
            SessionId = session.Id,
            ZaloConnectionId = session.ZaloConnectionId!,
            GroupIdSnapshot = NormalizeId(session.ZaloGroupId),
            AccountIdSnapshot = NormalizeId(session.ZaloConnection.AccountZaloId),
            RequesterKeyHash = requesterKeyHash,
            CodeHash = Hash(code),
            RedemptionSecretHash = Hash(redemptionSecret),
            Status = SettlementAccessChallengeStatus.Pending,
            ExpiresAt = now.Add(ChallengeLifetime),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SettlementAccessChallenges.Add(challenge);
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<SettlementAccessChallengeResponse>.Created(new(
            challenge.Id,
            redemptionSecret,
            code,
            $"xác minh {code}",
            session.Name,
            challenge.ExpiresAt));
    }

    public async Task<ServiceResult<SettlementAccessVerificationResult>> VerifyFromZaloAsync(
        string accountId,
        string groupId,
        string senderZaloUserId,
        string? senderDisplayName,
        string code,
        CancellationToken cancellationToken = default)
    {
        accountId = NormalizeId(accountId);
        groupId = NormalizeId(groupId);
        senderZaloUserId = NormalizeId(senderZaloUserId);
        code = NormalizeCode(code);
        if (accountId.Length == 0 || groupId.Length == 0 || senderZaloUserId.Length == 0 || code.Length == 0)
            return ServiceResult<SettlementAccessVerificationResult>.Failure(StatusCodes.Status400BadRequest, "Mã xác minh không hợp lệ.");

        var now = DateTimeOffset.UtcNow;
        var codeHash = Hash(code);
        var challenge = await db.SettlementAccessChallenges
            .SingleOrDefaultAsync(item => item.CodeHash == codeHash, cancellationToken);
        if (challenge is null || challenge.ExpiresAt <= now)
        {
            return ServiceResult<SettlementAccessVerificationResult>.Failure(
                StatusCodes.Status404NotFound,
                "Mã xác minh đã hết hạn hoặc không tồn tại. Mở lại link Sổ tiền để lấy mã mới.");
        }
        if (challenge.Status != SettlementAccessChallengeStatus.Pending)
        {
            return ServiceResult<SettlementAccessVerificationResult>.Failure(
                StatusCodes.Status409Conflict,
                "Mã xác minh này đã được sử dụng.");
        }
        if (!string.Equals(challenge.AccountIdSnapshot, accountId, StringComparison.Ordinal) ||
            !string.Equals(challenge.GroupIdSnapshot, groupId, StringComparison.Ordinal))
        {
            return ServiceResult<SettlementAccessVerificationResult>.Failure(
                StatusCodes.Status403Forbidden,
                "Mã này chỉ xác minh được trong đúng nhóm Zalo của trận.");
        }

        var session = await db.MatchSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == challenge.SessionId, cancellationToken);
        if (session is null)
            return ServiceResult<SettlementAccessVerificationResult>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy trận.");

        var authorizedViaConfiguredOperator = IsConfiguredOperator(session, senderZaloUserId);
        var authorized = authorizedViaConfiguredOperator;
        if (!authorized)
        {
            var role = await roleResolver(
                session.AdminUserId,
                session.Id,
                senderZaloUserId);
            if (!role.IsSuccess)
            {
                return ServiceResult<SettlementAccessVerificationResult>.Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "Chưa xác minh được quyền trưởng/phó nhóm từ Zalo. Vui lòng thử lại.");
            }
            authorized = role.Value?.CanOperateBot == true;
        }

        challenge.UpdatedAt = now;
        if (!authorized)
        {
            challenge.Status = SettlementAccessChallengeStatus.Denied;
            challenge.DeniedZaloUserId = senderZaloUserId;
            challenge.DeniedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<SettlementAccessVerificationResult>.Success(new(
                false,
                "Tài khoản Zalo này không phải trưởng/phó nhóm hoặc operator được cấp quyền."));
        }

        challenge.Status = SettlementAccessChallengeStatus.Approved;
        challenge.AuthorizedZaloUserId = senderZaloUserId;
        challenge.AuthorizedDisplayName = CleanDisplayName(senderDisplayName);
        challenge.AuthorizedViaConfiguredOperator = authorizedViaConfiguredOperator;
        challenge.ApprovedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<SettlementAccessVerificationResult>.Success(new(
            true,
            "Đã xác minh. Quay lại trang Sổ tiền, màn hình sẽ tự mở."));
    }

    public async Task<ServiceResult<SettlementAccessRedemption>> RedeemChallengeAsync(
        string challengeId,
        string redemptionSecret,
        CancellationToken cancellationToken = default)
    {
        challengeId = (challengeId ?? string.Empty).Trim();
        redemptionSecret = (redemptionSecret ?? string.Empty).Trim();
        if (challengeId.Length == 0 || redemptionSecret.Length == 0)
            return ServiceResult<SettlementAccessRedemption>.Failure(StatusCodes.Status400BadRequest, "Thiếu thông tin xác minh thiết bị.");

        var challenge = await db.SettlementAccessChallenges
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == challengeId, cancellationToken);
        if (challenge is null)
            return ServiceResult<SettlementAccessRedemption>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy yêu cầu xác minh.");
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(challenge.RedemptionSecretHash),
                Convert.FromHexString(Hash(redemptionSecret))))
        {
            return ServiceResult<SettlementAccessRedemption>.Failure(StatusCodes.Status403Forbidden, "Thiết bị không sở hữu yêu cầu xác minh này.");
        }

        var now = DateTimeOffset.UtcNow;
        if (challenge.ExpiresAt <= now)
        {
            return ServiceResult<SettlementAccessRedemption>.Success(new(
                new(challenge.Status, false, null, "Mã xác minh đã hết hạn. Tạo mã mới để tiếp tục.", challenge.ExpiresAt),
                null,
                null));
        }

        if (challenge.Status == SettlementAccessChallengeStatus.Pending)
        {
            return ServiceResult<SettlementAccessRedemption>.Success(new(
                new(challenge.Status, false, null, "Đang chờ xác minh từ Zalo…", challenge.ExpiresAt),
                null,
                null));
        }
        if (challenge.Status == SettlementAccessChallengeStatus.Denied)
        {
            return ServiceResult<SettlementAccessRedemption>.Success(new(
                new(challenge.Status, false, null, "Tài khoản Zalo vừa xác minh không có quyền thao tác Sổ tiền.", challenge.ExpiresAt),
                null,
                null));
        }

        if (string.IsNullOrWhiteSpace(challenge.AuthorizedZaloUserId))
            return ServiceResult<SettlementAccessRedemption>.Failure(StatusCodes.Status409Conflict, "Yêu cầu xác minh thiếu định danh Zalo.");

        var token = CreateAccessToken(challenge.Id, redemptionSecret);
        var tokenHash = Hash(token);
        var existingAccess = await db.SettlementAccessSessions
            .SingleOrDefaultAsync(item => item.SourceChallengeId == challenge.Id, cancellationToken);
        if (existingAccess is not null)
        {
            if (existingAccess.RevokedAt is not null || existingAccess.ExpiresAt <= now)
            {
                return ServiceResult<SettlementAccessRedemption>.Failure(
                    StatusCodes.Status401Unauthorized,
                    "Phiên xác minh của thiết bị đã hết hạn hoặc bị thu hồi. Vui lòng xác minh lại với Zalo.");
            }
            if (!string.Equals(existingAccess.TokenHash, tokenHash, StringComparison.Ordinal))
            {
                return ServiceResult<SettlementAccessRedemption>.Failure(
                    StatusCodes.Status409Conflict,
                    "Yêu cầu xác minh đã được đổi thông tin. Vui lòng tạo mã mới.");
            }
            return ServiceResult<SettlementAccessRedemption>.Success(new(
                new(challenge.Status, true, challenge.AuthorizedDisplayName, "Thiết bị đã được xác minh.", challenge.ExpiresAt),
                token,
                existingAccess.ExpiresAt));
        }

        var expiresAt = now.Add(AccessLifetime);
        var accessSession = new SettlementAccessSession
        {
            SourceChallengeId = challenge.Id,
            TokenHash = tokenHash,
            ZaloConnectionId = challenge.ZaloConnectionId,
            GroupId = challenge.GroupIdSnapshot,
            ZaloUserId = challenge.AuthorizedZaloUserId,
            DisplayName = challenge.AuthorizedDisplayName,
            AuthorizedViaConfiguredOperator = challenge.AuthorizedViaConfiguredOperator,
            ExpiresAt = expiresAt,
            LastRoleVerifiedAt = challenge.AuthorizedViaConfiguredOperator
                ? DateTimeOffset.UnixEpoch
                : challenge.ApprovedAt ?? now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SettlementAccessSessions.Add(accessSession);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(accessSession).State = EntityState.Detached;
            existingAccess = await db.SettlementAccessSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.SourceChallengeId == challenge.Id, cancellationToken);
            if (existingAccess is null ||
                existingAccess.RevokedAt is not null ||
                existingAccess.ExpiresAt <= now ||
                !string.Equals(existingAccess.TokenHash, tokenHash, StringComparison.Ordinal))
            {
                throw;
            }
            expiresAt = existingAccess.ExpiresAt;
        }

        return ServiceResult<SettlementAccessRedemption>.Success(new(
            new(challenge.Status, true, challenge.AuthorizedDisplayName, "Đã xác minh thiết bị.", challenge.ExpiresAt),
            token,
            expiresAt));
    }

    public async Task<ServiceResult<SettlementAccessAuthorization>> AuthorizeAsync(
        string? rawToken,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return ServiceResult<SettlementAccessAuthorization>.Failure(StatusCodes.Status401Unauthorized, "Thiết bị chưa được xác minh với Zalo.");

        var now = DateTimeOffset.UtcNow;
        var tokenHash = Hash(rawToken.Trim());
        var access = await db.SettlementAccessSessions
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (access is null || access.RevokedAt is not null || access.ExpiresAt <= now)
            return ServiceResult<SettlementAccessAuthorization>.Failure(StatusCodes.Status401Unauthorized, "Phiên xác minh đã hết hạn. Vui lòng xác minh lại với Zalo.");

        var session = await db.MatchSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null || string.IsNullOrWhiteSpace(session.ZaloConnectionId) || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return ServiceResult<SettlementAccessAuthorization>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy trận hoặc liên kết Zalo.");

        if (!string.Equals(access.ZaloConnectionId, session.ZaloConnectionId, StringComparison.Ordinal) ||
            !string.Equals(access.GroupId, NormalizeId(session.ZaloGroupId), StringComparison.Ordinal))
        {
            return ServiceResult<SettlementAccessAuthorization>.Failure(
                StatusCodes.Status403Forbidden,
                "Thiết bị này được xác minh cho một nhóm Zalo khác.");
        }

        var configuredOperator = IsConfiguredOperator(session, access.ZaloUserId);
        var mustRefreshLiveRole = !configuredOperator &&
                                  (access.AuthorizedViaConfiguredOperator ||
                                   now - access.LastRoleVerifiedAt >= RoleRefreshInterval);
        if (mustRefreshLiveRole)
        {
            var role = await roleResolver(session.AdminUserId, session.Id, access.ZaloUserId);
            if (!role.IsSuccess)
            {
                return ServiceResult<SettlementAccessAuthorization>.Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "Chưa kiểm tra lại được quyền Zalo lúc này. Vui lòng thử lại.");
            }
            if (role.Value?.CanOperateBot != true)
            {
                if (access.AuthorizedViaConfiguredOperator)
                {
                    return ServiceResult<SettlementAccessAuthorization>.Failure(
                        StatusCodes.Status403Forbidden,
                        "Tài khoản Zalo này không được cấp operator cho trận này và cũng không còn quyền trưởng/phó nhóm.");
                }
                access.RevokedAt = now;
                access.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<SettlementAccessAuthorization>.Failure(
                    StatusCodes.Status403Forbidden,
                    "Tài khoản Zalo này không còn quyền trưởng/phó nhóm hoặc operator.");
            }
            access.LastRoleVerifiedAt = now;
            access.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return ServiceResult<SettlementAccessAuthorization>.Success(new(
            session.AdminUserId,
            session.Id,
            session.Name,
            session.StartTime,
            access.ZaloUserId,
            access.DisplayName,
            access.ExpiresAt));
    }

    public async Task<ServiceResult<SettlementAccessStatusResponse>> GetStatusAsync(
        string? rawToken,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(rawToken, sessionId, cancellationToken);
        return !authorization.IsSuccess
            ? ServiceResult<SettlementAccessStatusResponse>.Failure(authorization.StatusCode, authorization.Error!)
            : ServiceResult<SettlementAccessStatusResponse>.Success(new(
                true,
                authorization.Value!.SessionId,
                authorization.Value.SessionName,
                authorization.Value.StartTime,
                authorization.Value.ZaloUserId,
                authorization.Value.DisplayName,
                authorization.Value.ExpiresAt));
    }

    private async Task<string> GenerateUniqueCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt += 1)
        {
            var bytes = RandomNumberGenerator.GetBytes(6);
            var chars = bytes.Select(value => CodeAlphabet[value % CodeAlphabet.Length]).ToArray();
            var code = new string(chars);
            var hash = Hash(code);
            if (!await db.SettlementAccessChallenges.AsNoTracking().AnyAsync(item => item.CodeHash == hash, cancellationToken))
                return code;
        }
        throw new InvalidOperationException("Không tạo được mã xác minh duy nhất.");
    }

    private static bool IsConfiguredOperator(MatchSession session, string zaloUserId)
    {
        var normalized = NormalizeId(zaloUserId);
        return ZaloOperatorPermissionCommandService.ParseOperatorIds(session.BotOperatorZaloUserIdsJson)
            .Contains(normalized, StringComparer.Ordinal);
    }

    private static string CreateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string CreateAccessToken(string challengeId, string redemptionSecret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"settlement-access-v1|{challengeId}|{redemptionSecret}"));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string NormalizeCode(string? value) =>
        new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static string NormalizeId(string? value) => (value ?? string.Empty).Trim();

    private static string? CleanDisplayName(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, 160)];
    }
}
