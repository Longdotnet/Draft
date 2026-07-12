using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBotImageService(VolleyDraftDbContext db)
{
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp"
    };

    public async Task<ServiceResult<IReadOnlyList<ZaloBotImageAssetResponse>>> GetAssetsAsync(
        string adminUserId,
        string publicOrigin,
        CancellationToken cancellationToken = default)
    {
        var assets = await db.ZaloBotImageAssets
            .AsNoTracking()
            .Where(asset => asset.AdminUserId == adminUserId)
            .OrderByDescending(asset => asset.CreatedAt)
            .Select(asset => new ZaloBotImageAssetResponse(
                asset.Id,
                asset.FileName,
                asset.ContentType,
                asset.Size,
                asset.CreatedAt,
                $"{publicOrigin.TrimEnd('/')}/api/public/bot-images/{asset.Id}"))
            .ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<ZaloBotImageAssetResponse>>.Success(assets);
    }

    public async Task<ServiceResult<ZaloBotImageAssetResponse>> UploadAsync(
        string adminUserId,
        IFormFile file,
        string publicOrigin,
        CancellationToken cancellationToken = default)
    {
        if (file.Length <= 0)
        {
            return ServiceResult<ZaloBotImageAssetResponse>.Failure(StatusCodes.Status400BadRequest, "Ảnh rỗng, hãy chọn lại ảnh.");
        }
        if (file.Length > MaxImageBytes)
        {
            return ServiceResult<ZaloBotImageAssetResponse>.Failure(StatusCodes.Status413PayloadTooLarge, "Ảnh không được lớn hơn 10 MB.");
        }
        if (!AllowedContentTypes.Contains(file.ContentType))
        {
            return ServiceResult<ZaloBotImageAssetResponse>.Failure(StatusCodes.Status400BadRequest, "Chỉ hỗ trợ ảnh JPG, PNG hoặc WEBP.");
        }

        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        if (stream.Length > MaxImageBytes)
        {
            return ServiceResult<ZaloBotImageAssetResponse>.Failure(StatusCodes.Status413PayloadTooLarge, "Ảnh không được lớn hơn 10 MB.");
        }

        var asset = new ZaloBotImageAsset
        {
            AdminUserId = adminUserId,
            FileName = Path.GetFileName(file.FileName).Length <= 160
                ? Path.GetFileName(file.FileName)
                : Path.GetFileName(file.FileName)[..160],
            ContentType = file.ContentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
                ? "image/jpeg"
                : file.ContentType,
            Size = stream.Length,
            Data = stream.ToArray(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ZaloBotImageAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<ZaloBotImageAssetResponse>.Created(ToResponse(asset, publicOrigin));
    }

    public async Task<BotImagePayload?> GetPublicAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var asset = await db.ZaloBotImageAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == assetId, cancellationToken);
        return asset is null ? null : new BotImagePayload(asset.Data, asset.ContentType);
    }

    private static ZaloBotImageAssetResponse ToResponse(ZaloBotImageAsset asset, string publicOrigin) => new(
        asset.Id,
        asset.FileName,
        asset.ContentType,
        asset.Size,
        asset.CreatedAt,
        $"{publicOrigin.TrimEnd('/')}/api/public/bot-images/{asset.Id}");
}

public sealed record BotImagePayload(byte[] Data, string ContentType);
