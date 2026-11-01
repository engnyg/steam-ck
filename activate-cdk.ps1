#!/usr/bin/env pwsh
# activate-cdk.ps1
# 透過 Steam 啟動 CDK（遊戲序號）並兌換遊戲
#
# 用法:
#   .\activate-cdk.ps1 -Key "XXXXX-XXXXX-XXXXX"
#   .\activate-cdk.ps1                           ← 互動模式，提示輸入
#   .\activate-cdk.ps1 -Key "XXXXX-XXXXX-XXXXX" -NoPause
#
# 說明:
#   1. 驗證 CDK 格式（Steam 序號通常 3～5 組，每組 5 碼）
#   2. 將 CDK 複製到剪貼簿
#   3. 開啟 Steam「啟動產品」對話框
#   4. 在 Steam 視窗出現後，按 Ctrl+V 貼上序號再按「下一步」

param(
    [string] $Key     = "",
    [switch] $NoPause
)

$ErrorActionPreference = 'Stop'

# ─── Banner ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ╔══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║   Steam CDK 遊戲序號啟動工具            ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ─── 格式驗證函式 ─────────────────────────────────────────────────────────────
function Test-CdkFormat {
    param([string]$k)
    # Steam CDK: 3～5 組，每組 5 碼，字元為 A-Z / 0-9
    return $k -match '^[A-Z0-9]{5}(-[A-Z0-9]{5}){2,4}$'
}

# ─── Steam 安裝路徑偵測 ────────────────────────────────────────────────────────
function Get-SteamPath {
    # 1. 登錄檔（最可靠）
    try {
        $exe = Get-ItemPropertyValue `
            -Path "HKCU:\SOFTWARE\Valve\Steam" `
            -Name "SteamExe" -ErrorAction Stop
        if ($exe -and (Test-Path $exe)) { return $exe }
    } catch {}

    # 2. 常見安裝路徑
    $candidates = @(
        "$env:ProgramFiles(x86)\Steam\steam.exe",
        "$env:ProgramFiles\Steam\steam.exe",
        "C:\Steam\steam.exe"
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

# ─── 主流程 ────────────────────────────────────────────────────────────────────

# 互動模式：若未傳入 Key 則提示輸入
if ([string]::IsNullOrWhiteSpace($Key)) {
    Write-Host "  輸入遊戲序號（格式：XXXXX-XXXXX-XXXXX）" -ForegroundColor White
    $Key = (Read-Host "  CDK").Trim()
}

$Key = $Key.Trim().ToUpper()

if ([string]::IsNullOrWhiteSpace($Key)) {
    Write-Host "[ERROR] 未輸入序號。" -ForegroundColor Red
    exit 1
}

# ─── 格式驗證 ──────────────────────────────────────────────────────────────────
if (-not (Test-CdkFormat $Key)) {
    Write-Warning "CDK 格式不符。Steam 序號通常為 XXXXX-XXXXX-XXXXX（3～5 組，每組 5 碼）。"
    $ans = Read-Host "  仍要繼續嗎？(Y/N)"
    if ($ans -notmatch '^[Yy]') {
        Write-Host "[INFO] 已取消。" -ForegroundColor Yellow
        exit 0
    }
}

# 遮蔽顯示（只顯示第一組）
$groups  = $Key -split '-'
$display = $groups[0] + (($groups | Select-Object -Skip 1 | ForEach-Object { '-*****' }) -join '')
Write-Host "[INFO] 序號：$display" -ForegroundColor Cyan

# ─── Steam 狀態 ────────────────────────────────────────────────────────────────
$steamExe = Get-SteamPath
if ($steamExe) {
    Write-Host "[OK]   找到 Steam：$steamExe" -ForegroundColor Green
} else {
    Write-Warning "未找到 Steam 安裝路徑，將使用 URL 協定嘗試開啟。"
}

# ─── 複製到剪貼簿 ──────────────────────────────────────────────────────────────
try {
    Set-Clipboard -Value $Key
    Write-Host "[OK]   CDK 已複製到剪貼簿" -ForegroundColor Green
} catch {
    Write-Warning "無法複製到剪貼簿（$_）。請手動貼上序號。"
}

# ─── 開啟 Steam「啟動產品」對話框 ────────────────────────────────────────────
Write-Host "[INFO] 開啟 Steam 啟動產品對話框..." -ForegroundColor Cyan
Start-Process "steam://open/activateproduct"

# ─── 完成提示 ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[DONE] Steam 啟動視窗已開啟！" -ForegroundColor Green
Write-Host ""
Write-Host "  後續步驟：" -ForegroundColor White
Write-Host "  1. 在 Steam 對話框中按 " -NoNewline -ForegroundColor White
Write-Host "Ctrl+V" -NoNewline -ForegroundColor Yellow
Write-Host " 貼上序號" -ForegroundColor White
Write-Host "  2. 按「下一步」→「我同意」→「完成」" -ForegroundColor White
Write-Host "  3. 遊戲將出現在你的 Steam 遊戲庫中" -ForegroundColor White
Write-Host ""

if (-not $NoPause) {
    Write-Host "  按任意鍵關閉..." -ForegroundColor DarkGray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
