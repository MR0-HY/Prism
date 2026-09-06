using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopAgent.Windows.Diagnostics;

internal static class RuntimeDiagnostic
{
    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        string? runtime = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName == "coreclr.dll")?.FileName;
        bool packaged = runtime is not null && Path.GetDirectoryName(runtime) == AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        await File.WriteAllTextAsync(Path.Combine(directory, "runtime.json"), JsonSerializer.Serialize(new
        { atUtc = DateTimeOffset.UtcNow, runtime, framework = RuntimeInformation.FrameworkDescription, packaged,
            windowsShown = 0, modelCalls = 0, inputEvents = 0, hotkeysRegistered = false }, new JsonSerializerOptions { WriteIndented = true }));
        return packaged ? 0 : 1;
    }
}
