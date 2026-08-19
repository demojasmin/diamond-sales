using System;
using System.Linq;
using System.Windows;

namespace SolitaireDesk.Services;

/// <summary>
/// Minimal theme service — swaps the color ResourceDictionary at runtime so the
/// design can be previewed in Light or Dark. This is a PREVIEW helper only; no app
/// logic lives here. Component styles reference brushes via DynamicResource, so a
/// swap re-colors the whole UI instantly.
/// </summary>
public static class ThemeManager
{
    public enum Theme { Light, Dark }

    private static Application? _app;
    public static Theme Current { get; private set; } = Theme.Light;

    private const string LightSource = "Themes/LightTheme.xaml";
    private const string DarkSource = "Themes/DarkTheme.xaml";

    public static void Initialize(Application app) => _app = app;

    public static void Toggle() => Apply(Current == Theme.Light ? Theme.Dark : Theme.Light);

    public static void Apply(Theme theme)
    {
        var app = _app ?? Application.Current;
        if (app is null) return;

        var newSource = theme == Theme.Dark ? DarkSource : LightSource;
        var dict = new ResourceDictionary { Source = new Uri(newSource, UriKind.Relative) };

        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source != null &&
            (d.Source.OriginalString.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            int idx = merged.IndexOf(existing);
            merged[idx] = dict;   // keep it first so it stays the base color layer
        }
        else
        {
            merged.Insert(0, dict);
        }

        Current = theme;
    }
}
