namespace WotLK.Launcher.Account;

internal readonly record struct AvatarCropLayout(
    double Zoom,
    double OffsetX,
    double OffsetY,
    double BaseDisplayWidth,
    double BaseDisplayHeight,
    double MaximumOffsetX,
    double MaximumOffsetY,
    AvatarNormalizedCrop Crop,
    double RelativeCropWidth,
    double RelativeCropHeight);

internal static class AvatarCropGeometry
{
    internal const double DefaultViewportSize = 360;
    internal const double AbsoluteMaximumZoom = 2.4;
    internal const int ServerMinimumCropPixels = 256;

    internal static double GetMaximumZoom(int orientedWidth, int orientedHeight)
    {
        ValidateDimensions(orientedWidth, orientedHeight);
        return Math.Max(
            1,
            Math.Min(
                AbsoluteMaximumZoom,
                Math.Min(orientedWidth, orientedHeight) / (double)ServerMinimumCropPixels));
    }

    internal static AvatarCropLayout Calculate(
        int orientedWidth,
        int orientedHeight,
        double zoom,
        double offsetX,
        double offsetY,
        double viewportSize = DefaultViewportSize)
    {
        ValidateDimensions(orientedWidth, orientedHeight);
        if (!double.IsFinite(viewportSize) || viewportSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportSize));
        }

        double maximumZoom = GetMaximumZoom(orientedWidth, orientedHeight);
        double safeZoom = Math.Clamp(
            double.IsFinite(zoom) ? zoom : 1,
            1,
            maximumZoom);
        double minimumDimension = Math.Min(orientedWidth, orientedHeight);
        double baseScale = viewportSize / minimumDimension;
        double baseWidth = orientedWidth * baseScale;
        double baseHeight = orientedHeight * baseScale;
        double renderedWidth = baseWidth * safeZoom;
        double renderedHeight = baseHeight * safeZoom;
        double maximumOffsetX = Math.Max(0, (renderedWidth - viewportSize) / 2);
        double maximumOffsetY = Math.Max(0, (renderedHeight - viewportSize) / 2);
        double safeOffsetX = Math.Clamp(
            double.IsFinite(offsetX) ? offsetX : 0,
            -maximumOffsetX,
            maximumOffsetX);
        double safeOffsetY = Math.Clamp(
            double.IsFinite(offsetY) ? offsetY : 0,
            -maximumOffsetY,
            maximumOffsetY);
        double displayScale = baseScale * safeZoom;
        double cropPixelSize = viewportSize / displayScale;
        double leftPixels = ((renderedWidth - viewportSize) / 2 - safeOffsetX) / displayScale;
        double topPixels = ((renderedHeight - viewportSize) / 2 - safeOffsetY) / displayScale;
        double cropX = Math.Clamp(leftPixels / orientedWidth, 0, 1);
        double cropY = Math.Clamp(topPixels / orientedHeight, 0, 1);
        double cropSize = Math.Clamp(cropPixelSize / minimumDimension, 0, 1);

        return new AvatarCropLayout(
            safeZoom,
            safeOffsetX,
            safeOffsetY,
            baseWidth,
            baseHeight,
            maximumOffsetX,
            maximumOffsetY,
            new AvatarNormalizedCrop(cropX, cropY, cropSize),
            cropPixelSize / orientedWidth,
            cropPixelSize / orientedHeight);
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
    }
}
