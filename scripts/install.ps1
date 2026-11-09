# OSU install.ps1
# 用法：irm https://cdn.ourdomain.com/install | iex

$ErrorActionPreference = "Stop"

$CDN_BASE = "https://cdn.ourdomain.com"
$DLL_URL  = "$CDN_BASE/xinput1_4.dll"

# ── Steam 路徑 ───────────────────────────────────────────────────────────────
function Get-SteamPath {
    foreach ($p in @(
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )) {
        $v = (Get-ItemProperty -Path $p -ErrorAction SilentlyContinue).InstallPath
        if ($v -and (Test-Path $v)) { return $v }
    }
    $d = "C:\Program Files (x86)\Steam"
    if (Test-Path $d) { return $d }
    return $null
}

# ── 主流程 ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  OSU 安裝中..." -ForegroundColor Cyan

# 1. Steam 路徑
$steamPath = Get-SteamPath
if (-not $steamPath) {
    Write-Host "[錯誤] 找不到 Steam 安裝路徑，請確認 Steam 已安裝。" -ForegroundColor Red
    pause; exit 1
}
Write-Host "[OK] Steam：$steamPath"

# 2. 關閉 Steam
if (Get-Process -Name "steam" -ErrorAction SilentlyContinue) {
    Write-Host "[INFO] 正在關閉 Steam..."
    Stop-Process -Name "steam" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

# 3. 下載 xinput1_4.dll
$dllDest = Join-Path $steamPath "xinput1_4.dll"
Write-Host "[下載] xinput1_4.dll..."
try {
    Invoke-WebRequest -Uri $DLL_URL -OutFile $dllDest -UseBasicParsing

    # 驗證數位簽章
    $sig = Get-AuthenticodeSignature -FilePath $dllDest
    if ($sig.Status -ne "Valid") {
        Remove-Item $dllDest -Force -ErrorAction SilentlyContinue
        Write-Host "[錯誤] DLL 簽章驗證失敗（Status: $($sig.Status)），已刪除。" -ForegroundColor Red
        pause; exit 1
    }
    Write-Host "[OK] DLL 簽章驗證通過（$($sig.SignerCertificate.Subject)）。" -ForegroundColor Green
    Write-Host "[OK] DLL 部署完成。" -ForegroundColor Green
} catch {
    Write-Host "[錯誤] 下載失敗：$_" -ForegroundColor Red
    pause; exit 1
}

# 4. Defender 排除
try { Add-MpPreference -ExclusionPath $dllDest  -ErrorAction SilentlyContinue } catch {}
try { Add-MpPreference -ExclusionPath $steamPath -ErrorAction SilentlyContinue } catch {}

# 5. 啟動 Steam 並開啟啟動頁面
$steamExe = Join-Path $steamPath "steam.exe"
Write-Host "[啟動] 開啟 Steam 啟動頁面..." -ForegroundColor Cyan
Start-Process $steamExe
Start-Sleep -Seconds 4
Start-Process "steam://open/activateproduct"

Write-Host ""
Write-Host "  請在 Steam 的啟動視窗中輸入您的 LY 金鑰。" -ForegroundColor Green
Write-Host ""
Start-Sleep -Seconds 2
