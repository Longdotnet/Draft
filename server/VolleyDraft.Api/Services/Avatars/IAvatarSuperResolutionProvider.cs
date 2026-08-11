namespace VolleyDraft.Api.Services.Avatars;

public interface IAvatarSuperResolutionProvider
{
    Task<SuperResolutionImage?> UpscaleAsync(
        byte[] sourceBytes,
        CancellationToken cancellationToken = default);
}

public sealed record SuperResolutionImage(
    byte[] Data,
    int Width,
    int Height,
    string Strategy);
