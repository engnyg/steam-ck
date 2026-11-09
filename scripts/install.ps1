# OSU install.ps1
# 用法：irm https://cdn.ourdomain.com/install | iex

$ErrorActionPreference = "Stop"

$CDN_BASE        = "https://cdn.ourdomain.com"
$DLL_URL         = "$CDN_BASE/xinput1_4.dll"
$REG_OSU         = "HKCU:\Software\OSU"
$APPDATA_OSU     = "$env:APPDATA\OSU"

# ── GUI ──────────────────────────────────────────────────────────────────────
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

function Show-KeyDialog {
    $form              = New-Object System.Windows.Forms.Form
    $form.Text         = "OSU 安裝 - 輸入啟動金鑰"
    $form.Size         = New-Object System.Drawing.Size(440, 190)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox  = $false
    $form.MinimizeBox  = $false
    $form.TopMost      = $true

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text     = "請輸入 LY 啟動金鑰（格式：LY-XXXXX-XXXXX-XXXXX-XXXXX）："
    $lbl.Location = New-Object System.Drawing.Point(16, 16)
    $lbl.AutoSize = $true
    $form.Controls.Add($lbl)

    $txt = New-Object System.Windows.Forms.TextBox
    $txt.Location  = New-Object System.Drawing.Point(16, 46)
    $txt.Size      = New-Object System.Drawing.Size(392, 26)
    $txt.Font      = New-Object System.Drawing.Font("Consolas", 11)
    $txt.MaxLength = 29
    $clip = Get-Clipboard -ErrorAction SilentlyContinue
    if ($clip -match 'LY-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}') {
        $txt.Text = $clip.Trim().ToUpper()
    }
    $form.Controls.Add($txt)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text         = "安裝"
    $ok.Location     = New-Object System.Drawing.Point(240, 110)
    $ok.Size         = New-Object System.Drawing.Size(80, 32)
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $ok
    $form.Controls.Add($ok)

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text         = "取消"
    $cancel.Location     = New-Object System.Drawing.Point(328, 110)
    $cancel.Size         = New-Object System.Drawing.Size(80, 32)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton   = $cancel
    $form.Controls.Add($cancel)

    if ($form.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        return $txt.Text.Trim().ToUpper()
    }
    return $null
}

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
Write-Host "  ╔══════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║      OSU  安裝程式           ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# 1. 金鑰輸入
$cdkey = Show-KeyDialog
if (-not $cdkey) {
    Write-Host "[取消] 已取消安裝。" -ForegroundColor Yellow
    exit 0
}
if ($cdkey -notmatch '^LY-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}$') {
    [System.Windows.Forms.MessageBox]::Show(
        "金鑰格式不正確。`n格式：LY-XXXXX-XXXXX-XXXXX-XXXXX",
        "OSU 安裝", "OK", "Error") | Out-Null
    exit 1
}
Write-Host "[OK] 金鑰：$cdkey" -ForegroundColor Green

# 2. Steam 路徑
$steamPath = Get-SteamPath
if (-not $steamPath) {
    [System.Windows.Forms.MessageBox]::Show(
        "找不到 Steam 安裝路徑，請確認 Steam 已安裝。",
        "OSU 安裝", "OK", "Error") | Out-Null
    exit 1
}
Write-Host "[OK] Steam：$steamPath" -ForegroundColor Green

# 3. 關閉 Steam
if (Get-Process -Name "steam" -ErrorAction SilentlyContinue) {
    Write-Host "[INFO] 正在關閉 Steam..." -ForegroundColor Yellow
    Stop-Process -Name "steam" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

# 4. 下載 xinput1_4.dll
$dllDest = Join-Path $steamPath "xinput1_4.dll"
Write-Host "[下載] xinput1_4.dll..."
try {
    Invoke-WebRequest -Uri $DLL_URL -OutFile $dllDest -UseBasicParsing
    Write-Host "[OK] DLL 部署完成。" -ForegroundColor Green
} catch {
    Write-Host "[錯誤] 下載失敗：$_" -ForegroundColor Red
    pause; exit 1
}

# 5. Registry
if (-not (Test-Path $REG_OSU)) { New-Item -Path $REG_OSU -Force | Out-Null }
Set-ItemProperty -Path $REG_OSU -Name "CDKey" -Value $cdkey -Type String
Write-Host "[OK] CDKey 已寫入 Registry。" -ForegroundColor Green

# 6. %APPDATA%\OSU\applist.json
if (-not (Test-Path $APPDATA_OSU)) {
    New-Item -ItemType Directory -Path $APPDATA_OSU -Force | Out-Null
}
$applistPath = Join-Path $APPDATA_OSU "applist.json"
if (-not (Test-Path $applistPath)) {
    '{"apps":[]}' | Out-File -FilePath $applistPath -Encoding utf8 -Force
}
Write-Host "[OK] 資料目錄就緒：$APPDATA_OSU" -ForegroundColor Green

# 7. Defender 排除
try { Add-MpPreference -ExclusionPath $dllDest  -ErrorAction SilentlyContinue } catch {}
try { Add-MpPreference -ExclusionPath $steamPath -ErrorAction SilentlyContinue } catch {}
Write-Host "[OK] Defender 排除已設定。" -ForegroundColor Green

# 8. 啟動 Steam
$steamExe = Join-Path $steamPath "steam.exe"
if (Test-Path $steamExe) {
    Write-Host "[啟動] 正在啟動 Steam..." -ForegroundColor Cyan
    Start-Process $steamExe
} else {
    Write-Host "[警告] 找不到 steam.exe，請手動啟動。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  安裝完成！Steam 啟動後會自動載入並驗證金鑰。" -ForegroundColor Green
Write-Host ""
Start-Sleep -Seconds 4
