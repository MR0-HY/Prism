using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace DesktopAgent.Windows.Presentation;

internal static class ThemeManager
{
    private static bool _initialized;
    internal static string Mode { get; private set; } = "system";
    internal static bool IsDark { get; private set; }
    internal static event Action? Changed;
    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Apply(AppearancePreferences.Read().Theme);
        SystemEvents.UserPreferenceChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (Mode == "system") Apply("system"); }));
    }
    internal static void Apply(string mode)
    {
        Mode = mode is "dark" or "light" ? mode : "system";
        IsDark = Mode == "dark" || Mode == "system" && SystemDark();
        string[] keys = ["Page", "Surface", "SurfaceAlt", "Text", "Muted", "Line", "Accent", "AccentSoft", "Danger", "Warning", "Glass"];
        string[] colors = IsDark
            ? ["#15171D", "#20232C", "#292D38", "#F1F3FA", "#A7AFBF", "#3C4250", "#B2A5FF", "#34304C", "#FF9DAB", "#3B3022", "#F21E212A"]
            : ["#F7F8FC", "#FFFFFF", "#F0F2F8", "#252A3C", "#687286", "#E0E4EE", "#6653D9", "#EEEAFE", "#B53D58", "#FFF4DD", "#F5FFFFFF"];
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i] + "Brush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        foreach (Window window in Application.Current.Windows) Chrome(window);
        Changed?.Invoke();
    }
    private static bool SystemDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch { return false; }
    }
    internal static void Chrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        int dark = IsDark ? 1 : 0, corners = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        DwmSetWindowAttribute(hwnd, 33, ref corners, 4);
    }
    internal static void BindText(System.Windows.Controls.TextBlock text, bool muted = false) => text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
