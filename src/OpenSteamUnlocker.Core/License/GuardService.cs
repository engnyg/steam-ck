using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenSteamUnlocker.Core.License;

public sealed class GuardService : IDisposable
{
    private readonly ILicenseService _license;
    private readonly Func<string?> _getKey;
    private readonly Func<Task> _onViolation;
    private readonly Timer _heartbeat;
    private readonly Timer _debugCheck;

    public GuardService(
        ILicenseService license,
        Func<string?> getKey,
        Func<Task> onViolation)
    {
        _license = license;
        _getKey = getKey;
        _onViolation = onViolation;

        _heartbeat = new Timer(HeartbeatCallback, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        _debugCheck = new Timer(DebugCheckCallback, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll")]
    private static extern bool CheckRemoteDebuggerPresent(nint hProcess, out bool isDebuggerPresent);

    public static bool IsBeingDebugged()
    {
        if (Debugger.IsAttached) return true;

        try { if (IsDebuggerPresent()) return true; } catch { }

        try
        {
            CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out var remote);
            if (remote) return true;
        }
        catch { }

        return false;
    }

    private void DebugCheckCallback(object? state)
    {
        if (IsBeingDebugged())
            _onViolation().ConfigureAwait(false);
    }

    private async void HeartbeatCallback(object? state)
    {
        var key = _getKey();
        if (string.IsNullOrEmpty(key)) return;

        try
        {
            var result = await _license.VerifyAsync(key);
            if (!result.IsValid)
                await _onViolation();
        }
        catch { }
    }

    public void Dispose()
    {
        _heartbeat.Dispose();
        _debugCheck.Dispose();
    }
}
