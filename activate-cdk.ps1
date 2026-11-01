#!/usr/bin/env pwsh
# activate-cdk.ps1
# 輸入 Steam CDK 序號，自動開啟 Steam 兌換頁面並填入序號
#
# 用法:
#   .\activate-cdk.ps1 -Key "XXXXX-XXXXX-XXXXX"
#   .\activate-cdk.ps1                            ← 互動模式，提示輸入

param(
    [string] $Key     = "",
    [switch] $NoPause
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

# ─── Banner ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  Steam CDK 序號兌換工具" -ForegroundColor Cyan
Write-Host "  ─────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host ""

# ─── 格式驗證 ─────────────────────────────────────────────────────────────────
function Test-CdkFormat([string]$k) {
    return $k -match '^[A-Z0-9]{5}(-[A-Z0-9]{5}){2,4}$'
}

# ─── Steam 路徑偵測 ────────────────────────────────────────────────────────────
function Get-SteamExe {
    try {
        $exe = Get-ItemPropertyValue "HKCU:\SOFTWARE\Valve\Steam" "SteamExe" -ErrorAction Stop
        if ($exe -and (Test-Path $exe)) { return $exe }
    } catch {}
    foreach ($p in @(
        "$env:ProgramFiles(x86)\Steam\steam.exe",
        "$env:ProgramFiles\Steam\steam.exe",
        "C:\Steam\steam.exe"
    )) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

# ─── 等待 Steam 兌換視窗 ─────────────────────────────────────────────────────
# 回傳視窗 handle，找不到則回傳 0
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32 {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
"@

function Find-SteamActivateWindow {
    $found = [IntPtr]::Zero
    $callback = [Win32+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        if ([Win32]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Win32]::GetWindowText($hWnd, $sb, 256)
            $title = $sb.ToString()
            if ($title -match 'Steam' -and ($title -match 'Activate|Product|序號|啟動|兌換')) {
                $script:found = $hWnd
                return $false
            }
        }
        return $true
    }
    [Win32]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $script:found
}

# ─── 主流程 ────────────────────────────────────────────────────────────────────

# 互動模式
if ([string]::IsNullOrWhiteSpace($Key)) {
    Write-Host "  請輸入 Steam CDK（格式：XXXXX-XXXXX-XXXXX）" -ForegroundColor White
    $Key = (Read-Host "  CDK").Trim()
}

$Key = $Key.Trim().ToUpper()

if ([string]::IsNullOrWhiteSpace($Key)) {
    Write-Host "[ERROR] 未輸入序號。" -ForegroundColor Red; exit 1
}

# 格式驗證（不符仍可繼續）
if (-not (Test-CdkFormat $Key)) {
    Write-Warning "格式不符（Steam CDK 通常為 XXXXX-XXXXX-XXXXX）。"
    $ans = Read-Host "  仍要繼續嗎？(Y/N)"
    if ($ans -notmatch '^[Yy]') { exit 0 }
}

# 遮蔽顯示
$masked = ($Key -split '-')[0] + (($Key -split '-' | Select-Object -Skip 1 | ForEach-Object { '-*****' }) -join '')
Write-Host "[INFO] 序號：$masked" -ForegroundColor Cyan

# Steam 狀態
$steamExe = Get-SteamExe
if ($steamExe) {
    Write-Host "[OK]   Steam：$steamExe" -ForegroundColor Green
} else {
    Write-Warning "未找到 Steam，將使用 URL 協定開啟。"
}

# 複製到剪貼簿（備用）
try {
    [System.Windows.Forms.Clipboard]::SetText($Key)
    Write-Host "[OK]   CDK 已複製到剪貼簿" -ForegroundColor Green
} catch {
    Write-Warning "無法複製到剪貼簿：$_"
}

# 開啟 Steam 兌換頁面
Write-Host "[INFO] 開啟 Steam 序號兌換頁面..." -ForegroundColor Cyan
Start-Process "steam://open/activateproduct"

# 等待 Steam 兌換視窗出現（最多等 15 秒）
Write-Host "[INFO] 等待 Steam 視窗出現..." -ForegroundColor DarkGray
$hwnd    = [IntPtr]::Zero
$timeout = 15
$waited  = 0
while ($waited -lt $timeout) {
    Start-Sleep -Milliseconds 800
    $waited += 0.8

    $script:found = [IntPtr]::Zero
    $cb = [Win32+EnumWindowsProc]{
        param([IntPtr]$h, [IntPtr]$p)
        if ([Win32]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Win32]::GetWindowText($h, $sb, 256)
            $t = $sb.ToString()
            if ($t -match 'Steam' -and $t -match 'Activate|Product|序號|啟動|兌換') {
                $script:found = $h; return $false
            }
        }
        return $true
    }
    [Win32]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

    if ($script:found -ne [IntPtr]::Zero) {
        $hwnd = $script:found; break
    }
}

if ($hwnd -ne [IntPtr]::Zero) {
    # 將 Steam 視窗帶到前景，然後貼上序號
    [Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait("^v")   # Ctrl+V 貼上
    Write-Host "[OK]   序號已自動貼入 Steam 視窗" -ForegroundColor Green
    Write-Host ""
    Write-Host "  請在 Steam 視窗中按「下一步」→「我同意」→「完成」。" -ForegroundColor White
} else {
    # 找不到視窗（Steam 可能開在不同標題）→ 手動貼上
    Write-Host "[WARN] 未偵測到 Steam 兌換視窗，請手動按 " -NoNewline -ForegroundColor Yellow
    Write-Host "Ctrl+V" -NoNewline -ForegroundColor Cyan
    Write-Host " 貼上序號。" -ForegroundColor Yellow
}

Write-Host ""

if (-not $NoPause) {
    Write-Host "  按任意鍵關閉..." -ForegroundColor DarkGray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
