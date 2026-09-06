using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Runtime;

/// <summary>Exact, bounded desktop shortcuts shared by policy and assisted focus checks.</summary>
public static class DesktopNavigation
{
    public static bool IsGlobalNavigation(AgentAction action) => action is HotkeyAction keys &&
        (keys.Keys is [AgentKey.WIN] ||
         keys.Keys is [AgentKey.WIN, AgentKey.I or AgentKey.E or AgentKey.D or AgentKey.M] ||
         keys.Keys is [AgentKey.ALT, AgentKey.TAB]);

    public static bool IsWindowClose(AgentAction action) => action is HotkeyAction keys &&
        (keys.Keys is [AgentKey.ALT, AgentKey.F4] || keys.Keys is [AgentKey.CTRL, AgentKey.W]);

    public static bool IsSave(AgentAction action) => action is HotkeyAction { Keys: [AgentKey.CTRL, AgentKey.S] };

    public static bool IsDesktopShell(ForegroundIdentity foreground) =>
        foreground.WindowClass is { } windowClass &&
        (windowClass.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
         windowClass.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
         windowClass.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
         windowClass.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase));

    public static bool IsOrdinaryWindow(ForegroundIdentity foreground)
    {
        if (IsDesktopShell(foreground) || string.IsNullOrWhiteSpace(foreground.ProcessName)) return false;
        // Explorer is also the desktop shell. Only an identified folder window may receive close/save.
        return !foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
            foreground.WindowClass is "CabinetWClass" or "ExploreWClass";
    }
}
