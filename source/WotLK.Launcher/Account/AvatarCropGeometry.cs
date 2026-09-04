namespace WotLK.Launcher.Account;

internal readonly record struct AvatarPixelCrop(int X, int Y, int Size);

internal readonly record struct AvatarCropLayout(
    double Zoom,
    double OffsetX,
    double OffsetY,
    double MaximumOffsetX,
    double MaximumOffsetY,
    AvatarPixelCrop PixelCrop,
    AvatarNormalizedCrop Crop);

internal static class AvatarCropGeometry
{
    internal const double DefaultViewportSize = 360;
    internal const double AbsoluteMaximumZoom = 2.4;
    internal const double InitialInteractiveZoom = 1.12;
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

    internal static double GetInitialZoom(int orientedWidth, int orientedHeight) =>
        Math.Min(InitialInteractiveZoom, GetMaximumZoom(orientedWidth, orientedHeight));

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
        double rawCropPixelSize = viewportSize / displayScale;
        double leftPixels = ((renderedWidth - viewportSize) / 2 - safeOffsetX) / displayScale;
        double topPixels = ((renderedHeight - viewportSize) / 2 - safeOffsetY) / displayScale;
        int cropPixelSize = Math.Clamp(
            (int)Math.Round(rawCropPixelSize, MidpointRounding.AwayFromZero),
            1,
            (int)minimumDimension);
        int cropPixelX = Math.Clamp(
            (int)Math.Round(leftPixels, MidpointRounding.AwayFromZero),
            0,
            orientedWidth - cropPixelSize);
        int cropPixelY = Math.Clamp(
            (int)Math.Round(topPixels, MidpointRounding.AwayFromZero),
            0,
            orientedHeight - cropPixelSize);
        double cropX = cropPixelX / (double)orientedWidth;
        double cropY = cropPixelY / (double)orientedHeight;
        double cropSize = cropPixelSize / minimumDimension;

        return new AvatarCropLayout(
            safeZoom,
            safeOffsetX,
            safeOffsetY,
            maximumOffsetX,
            maximumOffsetY,
            new AvatarPixelCrop(cropPixelX, cropPixelY, cropPixelSize),
            new AvatarNormalizedCrop(cropX, cropY, cropSize));
    }

    internal static AvatarCropLayout ZoomAt(
        int orientedWidth,
        int orientedHeight,
        double currentZoom,
        double currentOffsetX,
        double currentOffsetY,
        double requestedZoom,
        double focusX,
        double focusY,
        double viewportSize = DefaultViewportSize)
    {
        AvatarCropLayout current = Calculate(
            orientedWidth,
            orientedHeight,
            currentZoom,
            currentOffsetX,
            currentOffsetY,
            viewportSize);
        double nextZoom = Math.Clamp(
            double.IsFinite(requestedZoom) ? requestedZoom : current.Zoom,
            1,
            GetMaximumZoom(orientedWidth, orientedHeight));
        double safeFocusX = Math.Clamp(
            double.IsFinite(focusX) ? focusX : viewportSize / 2,
            0,
            viewportSize);
        double safeFocusY = Math.Clamp(
            double.IsFinite(focusY) ? focusY : viewportSize / 2,
            0,
            viewportSize);
        double minimumDimension = Math.Min(orientedWidth, orientedHeight);
        double baseScale = viewportSize / minimumDimension;
        double baseWidth = orientedWidth * baseScale;
        double baseHeight = orientedHeight * baseScale;
        double currentScale = baseScale * current.Zoom;
        double sourceFocusX = (
            safeFocusX
            + (baseWidth * current.Zoom - viewportSize) / 2
            - current.OffsetX) / currentScale;
        double sourceFocusY = (
            safeFocusY
            + (baseHeight * current.Zoom - viewportSize) / 2
            - current.OffsetY) / currentScale;
        double nextOffsetX = safeFocusX
            + (baseWidth * nextZoom - viewportSize) / 2
            - sourceFocusX * baseScale * nextZoom;
        double nextOffsetY = safeFocusY
            + (baseHeight * nextZoom - viewportSize) / 2
            - sourceFocusY * baseScale * nextZoom;

        return Calculate(
            orientedWidth,
            orientedHeight,
            nextZoom,
            nextOffsetX,
            nextOffsetY,
            viewportSize);
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
    }
}
