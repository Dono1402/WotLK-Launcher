namespace WotLK.Launcher.UI.V2.Presentation;

public enum AdaptiveLayoutMode
{
    Stacked,
    Compact,
    Wide
}

public static class AdaptiveLayoutClassifier
{
    public static AdaptiveLayoutMode FromWidth(double width)
    {
        if (width >= 1320)
        {
            return AdaptiveLayoutMode.Wide;
        }

        return width >= 1180
            ? AdaptiveLayoutMode.Compact
            : AdaptiveLayoutMode.Stacked;
    }
}
