using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace WotLK.Launcher.UI.V2.Localization;

internal sealed class LauncherLocalizationBridge : IDisposable
{
    private static readonly DependencyProperty[] TranslatableProperties =
    [
        TextBlock.TextProperty,
        Run.TextProperty,
        ContentControl.ContentProperty,
        HeaderedContentControl.HeaderProperty,
        FrameworkElement.ToolTipProperty,
        AutomationProperties.NameProperty,
        Window.TitleProperty
    ];

    private readonly Window _window;
    private readonly HashSet<DependencyObject> _knownObjects = [];
    private readonly Dictionary<PropertyKey, string> _originalValues = [];
    private readonly List<PropertySubscription> _propertySubscriptions = [];
    private readonly List<GeneratorSubscription> _generatorSubscriptions = [];
    private int _translationDepth;
    private int _disposeState;

    internal LauncherLocalizationBridge(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.Loaded += Window_Loaded;
        LauncherLocalization.LocaleChanged += LauncherLocalization_LocaleChanged;
        if (_window.IsLoaded)
        {
            DiscoverAndTranslate(_window);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _window.Loaded -= Window_Loaded;
        LauncherLocalization.LocaleChanged -= LauncherLocalization_LocaleChanged;
        foreach (PropertySubscription subscription in _propertySubscriptions)
        {
            subscription.Descriptor.RemoveValueChanged(
                subscription.Target,
                subscription.Handler);
        }

        foreach (GeneratorSubscription subscription in _generatorSubscriptions)
        {
            subscription.Generator.StatusChanged -= subscription.Handler;
        }

        _propertySubscriptions.Clear();
        _generatorSubscriptions.Clear();
        _knownObjects.Clear();
        _originalValues.Clear();
    }

    internal void Refresh() => DiscoverAndTranslate(_window);

    private void Window_Loaded(object sender, RoutedEventArgs e) =>
        DiscoverAndTranslate(_window);

    private void LauncherLocalization_LocaleChanged(object? sender, EventArgs e)
    {
        if (_window.Dispatcher.CheckAccess())
        {
            TranslateKnownObjects();
            DiscoverAndTranslate(_window);
            return;
        }

        if (!_window.Dispatcher.HasShutdownStarted
            && !_window.Dispatcher.HasShutdownFinished)
        {
            _ = _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                TranslateKnownObjects();
                DiscoverAndTranslate(_window);
            }));
        }
    }

    private void DiscoverAndTranslate(DependencyObject root)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        Stack<DependencyObject> pending = new();
        HashSet<DependencyObject> visited = [];
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            bool isNew = _knownObjects.Add(current);
            if (isNew)
            {
                TrackProperties(current);
                TrackGeneratedItems(current);
            }

            TranslateObject(current);
            PushChildren(current, pending);
        }
    }

    private void TrackProperties(DependencyObject target)
    {
        foreach (DependencyProperty property in TranslatableProperties)
        {
            if (!IsPropertyOwnedByTarget(property, target))
            {
                continue;
            }

            DependencyPropertyDescriptor? descriptor =
                DependencyPropertyDescriptor.FromProperty(property, target.GetType());
            if (descriptor is null)
            {
                continue;
            }

            EventHandler handler = (_, _) =>
            {
                if (Volatile.Read(ref _disposeState) == 0
                    && Volatile.Read(ref _translationDepth) == 0
                    && !IsCurrentTranslation(target, property))
                {
                    CaptureOriginalValue(target, property);
                    TranslateProperty(target, property);
                }
            };
            descriptor.AddValueChanged(target, handler);
            _propertySubscriptions.Add(new PropertySubscription(target, descriptor, handler));
            CaptureOriginalValue(target, property);
        }
    }

    private void TrackGeneratedItems(DependencyObject target)
    {
        if (target is not ItemsControl itemsControl)
        {
            return;
        }

        ItemContainerGenerator generator = itemsControl.ItemContainerGenerator;
        EventHandler handler = (_, _) =>
        {
            if (generator.Status != GeneratorStatus.ContainersGenerated)
            {
                return;
            }

            foreach (object item in itemsControl.Items)
            {
                if (generator.ContainerFromItem(item) is DependencyObject container)
                {
                    DiscoverAndTranslate(container);
                }
            }
        };
        generator.StatusChanged += handler;
        _generatorSubscriptions.Add(new GeneratorSubscription(generator, handler));
    }

    private void TranslateKnownObjects()
    {
        foreach (DependencyObject target in _knownObjects.ToArray())
        {
            TranslateObject(target);
        }
    }

    private void TranslateObject(DependencyObject target)
    {
        foreach (DependencyProperty property in TranslatableProperties)
        {
            if (IsPropertyOwnedByTarget(property, target))
            {
                TranslateProperty(target, property);
            }
        }
    }

    private void TranslateProperty(DependencyObject target, DependencyProperty property)
    {
        if (target.GetValue(property) is not string source
            || string.IsNullOrEmpty(source))
        {
            return;
        }

        PropertyKey key = new(target, property);
        string original = _originalValues.TryGetValue(key, out string? captured)
            ? captured
            : source;
        string translated = LauncherLocalization.IsEnglish
            ? LauncherLocalization.TranslateFromFrench(original)
            : original;
        if (string.Equals(source, translated, StringComparison.Ordinal))
        {
            return;
        }

        Interlocked.Increment(ref _translationDepth);
        try
        {
            target.SetCurrentValue(property, translated);
        }
        finally
        {
            Interlocked.Decrement(ref _translationDepth);
        }
    }

    private bool IsCurrentTranslation(
        DependencyObject target,
        DependencyProperty property)
    {
        PropertyKey key = new(target, property);
        if (!_originalValues.TryGetValue(key, out string? original)
            || target.GetValue(property) is not string current)
        {
            return false;
        }

        string expected = LauncherLocalization.IsEnglish
            ? LauncherLocalization.TranslateFromFrench(original)
            : original;
        return string.Equals(current, expected, StringComparison.Ordinal);
    }

    private void CaptureOriginalValue(
        DependencyObject target,
        DependencyProperty property)
    {
        if (target.GetValue(property) is string source)
        {
            _originalValues[new PropertyKey(target, property)] = source;
        }
    }

    private static bool IsPropertyOwnedByTarget(
        DependencyProperty property,
        DependencyObject target)
    {
        return property switch
        {
            _ when property == TextBlock.TextProperty => target is TextBlock,
            _ when property == Run.TextProperty => target is Run,
            _ when property == ContentControl.ContentProperty => target is ContentControl,
            _ when property == HeaderedContentControl.HeaderProperty =>
                target is HeaderedContentControl,
            _ when property == FrameworkElement.ToolTipProperty => target is FrameworkElement,
            _ when property == AutomationProperties.NameProperty => target is UIElement,
            _ when property == Window.TitleProperty => target is Window,
            _ => false
        };
    }

    private static void PushChildren(
        DependencyObject target,
        Stack<DependencyObject> pending)
    {
        if (target is FrameworkElement element)
        {
            if (element.ContextMenu is not null)
            {
                pending.Push(element.ContextMenu);
            }

            if (element.ToolTip is DependencyObject toolTip)
            {
                pending.Push(toolTip);
            }
        }

        int visualChildren = 0;
        try
        {
            visualChildren = VisualTreeHelper.GetChildrenCount(target);
        }
        catch (InvalidOperationException)
        {
            // Content elements can exist in the logical tree without a visual peer.
        }

        for (int index = 0; index < visualChildren; index++)
        {
            pending.Push(VisualTreeHelper.GetChild(target, index));
        }

        foreach (object child in LogicalTreeHelper.GetChildren(target))
        {
            if (child is DependencyObject dependencyObject)
            {
                pending.Push(dependencyObject);
            }
        }
    }

    private sealed record PropertySubscription(
        DependencyObject Target,
        DependencyPropertyDescriptor Descriptor,
        EventHandler Handler);

    private sealed record GeneratorSubscription(
        ItemContainerGenerator Generator,
        EventHandler Handler);

    private readonly record struct PropertyKey(
        DependencyObject Target,
        DependencyProperty Property);
}
