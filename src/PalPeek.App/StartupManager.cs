using Microsoft.Win32;

namespace PalPeek;

public sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PalPeek";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 开机启动设置。");
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "PalPeek.exe");
            key.SetValue(ValueName, $"\"{executable}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
