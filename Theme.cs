using System.Windows;
using Microsoft.Win32;

namespace RescueDesk;

/// <summary>
/// Follows the Windows light/dark app setting. Standard controls get it from WPF's Fluent theme
/// (ThemeMode in App.xaml); our own colours come from Themes/Light.xaml or Themes/Dark.xaml.
/// </summary>
static class Theme
{
    const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static ResourceDictionary? _palette;

    public static void Start(Application app)
    {
        ApplyPalette(app);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) app.Dispatcher.BeginInvoke(() => ApplyPalette(app));
        };
    }

    static bool IsDark => Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is 0;

    static void ApplyPalette(Application app)
    {
        var palette = new ResourceDictionary { Source = new Uri($"Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var dictionaries = app.Resources.MergedDictionaries;
        if (_palette != null) dictionaries.Remove(_palette);
        dictionaries.Add(palette);
        _palette = palette;
    }
}
