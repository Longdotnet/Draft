using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBotImageService(VolleyDraftDbContext db)
{
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxStoredDimension = 1600;
    private const int JpegQuality = 82;
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

        byte[] optimized;
        try
        {
            optimized = OptimizeForDelivery(stream.ToArray());
        }
        catch (InvalidOperationException)
        {
            return ServiceResult<ZaloBotImageAssetResponse>.Failure(StatusCodes.Status400BadRequest, "Dữ liệu ảnh không hợp lệ hoặc không được hỗ trợ.");
        }

        var originalName = Path.GetFileNameWithoutExtension(file.FileName).Trim();
        if (string.IsNullOrWhiteSpace(originalName))
            originalName = "zalo-image";
        if (originalName.Length > 150)
            originalName = originalName[..150];
        var fileName = $"{originalName}.jpg";

        var asset = new ZaloBotImageAsset
        {
            AdminUserId = adminUserId,
            FileName = fileName,
            ContentType = "image/jpeg",
            Size = optimized.LongLength,
            Data = optimized,
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

    internal static byte[] OptimizeForDelivery(byte[] sourceBytes)
    {
        using var source = SKBitmap.Decode(sourceBytes)
            ?? throw new InvalidOperationException("Image could not be decoded.");
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("Image dimensions are invalid.");

        var scale = Math.Min(1d, (double)MaxStoredDimension / Math.Max(source.Width, source.Height));
        var targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var surface = SKSurface.Create(
            new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("Image surface could not be created.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium
        };
        canvas.DrawBitmap(source, new SKRect(0, 0, targetWidth, targetHeight), paint);

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            ?? throw new InvalidOperationException("Image could not be encoded.");
        return encoded.ToArray();
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
