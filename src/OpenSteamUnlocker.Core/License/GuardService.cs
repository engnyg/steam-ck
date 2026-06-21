using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenSteamUnlocker.Core.License;

public sealed class GuardService : IDisposable
{
    private static readonly string[] _blockedProcesses =
    [
        "procmon", "procmon64",
        "procexp", "procexp64",
        "wireshark", "tshark", "rawcap",
        "fiddler", "charles",
        "x64dbg", "x32dbg", "ollydbg",
        "ida", "ida64", "idaq", "idaq64",
        "ghidra", "binaryninja",
        "dnspy", "dnspy-x86", "dotpeek", "ilspy",
        "cheatengine", "cheatengine-x86_64",
        "httpdebugger", "httpdebuggerui",
        "resourcehacker", "pe-bear", "pestudio",
        "apimonitor", "apimonitor-x64",
        "hxd", "hxd64",
        "processhacker",
    ];

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

    public static string? DetectBlockedProcess()
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName.ToLowerInvariant()
                        .Replace(" ", "").Replace("-", "");
                    foreach (var blocked in _blockedProcesses)
                    {
                        if (name.Contains(blocked.Replace("-", "")))
                            return proc.ProcessName;
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
        return null;
    }

    public static bool HasThreats() => IsBeingDebugged() || DetectBlockedProcess() is not null;

    private void DebugCheckCallback(object? state)
    {
        if (HasThreats())
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
