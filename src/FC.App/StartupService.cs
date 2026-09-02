using Microsoft.Win32;

namespace FC;

public sealed class StartupService(StateStore store)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FC";

    public async Task SetEnabledAsync(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        await store.MutateAsync(s => s.RunAtStartup = enabled);
    }

    public bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }
}
