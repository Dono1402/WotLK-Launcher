using System.Globalization;
using System.Windows.Data;

namespace WotLK.Launcher.UI.V2.Presentation;

// Presentation only: preserve the bound copy while placing its two title lines independently.
public sealed class HeroTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? string.Empty;
        int split = text.IndexOf(' ');
        return (parameter as string) switch
        {
            "First" => split < 0 ? text : text[..split],
            "Rest" => split < 0 ? string.Empty : text[(split + 1)..],
            "Tracked" => string.Join("\u2009", text.ToCharArray()),
            "Realm" => culture.TextInfo.ToTitleCase(text.ToLower(culture)),
            "Sentence" => text.Length == 0 || char.IsPunctuation(text[^1]) ? text : text + ".",
            _ => text
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
