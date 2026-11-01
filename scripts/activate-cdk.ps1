# OSU activate-cdk.ps1
# 用法：irm https://cdn.ourdomain.com/activate | iex
#
# 流程：
#   1. 接收 LY 金鑰輸入（GUI 對話框）
#   2. 從 Steam Registry 取得 InstallPath
#   3. 下載 xinput1_4.dll 至 Steam 目錄
#   4. 寫入 HKCU\Software\OSU\CDKey
#   5. 寫入 %APPDATA%\OSU\applist.json
#   6. 啟動 Steam

$ErrorActionPreference = "Stop"

# ── 常數 ─────────────────────────────────────────────────────────────────
$CDN_BASE   = "https://cdn.ourdomain.com"
$DLL_URL    = "$CDN_BASE/xinput1_4.dll"
$REG_OSU    = "HKCU:\Software\OSU"
$APPLIST_DIR = "$env:APPDATA\OSU"

# ── GUI 金鑰輸入 ─────────────────────────────────────────────────────────
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Show-KeyDialog {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "OSU - 啟動金鑰"
    $form.Size = New-Object System.Drawing.Size(420, 180)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "請輸入 LY 金鑰："
    $label.Location = New-Object System.Drawing.Point(20, 20)
    $label.AutoSize = $true
    $form.Controls.Add($label)

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.Location = New-Object System.Drawing.Point(20, 50)
    $textBox.Size = New-Object System.Drawing.Size(360, 25)
    $textBox.Font = New-Object System.Drawing.Font("Consolas", 11)
    $textBox.Text = (Get-Clipboard -ErrorAction SilentlyContinue) -replace '[^\w\-]', ''
    $form.Controls.Add($textBox)

    $btnOK = New-Object System.Windows.Forms.Button
    $btnOK.Text = "啟動"
    $btnOK.Location = New-Object System.Drawing.Point(220, 95)
    $btnOK.Size = New-Object System.Drawing.Size(80, 30)
    $btnOK.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $btnOK
    $form.Controls.Add($btnOK)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "取消"
    $btnCancel.Location = New-Object System.Drawing.Point(310, 95)
    $btnCancel.Size = New-Object System.Drawing.Size(70, 30)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $btnCancel
    $form.Controls.Add($btnCancel)

    $result = $form.ShowDialog()
    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        return $textBox.Text.Trim().ToUpper()
    }
    return $null
}

# ── 金鑰格式驗證 ─────────────────────────────────────────────────────────
function Test-KeyFormat {
    param([string]$Key)
    return $Key -match '^LY-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}$'
}

# ── 取得 Steam 安裝路徑 ──────────────────────────────────────────────────
function Get-SteamPath {
    $paths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )
    foreach ($p in $paths) {
        try {
            $val = (Get-ItemProperty -Path $p -ErrorAction SilentlyContinue).InstallPath
            if ($val -and (Test-Path $val)) { return $val }
        } catch {}
    }
    # 常見預設路徑
    $default = "C:\Program Files (x86)\Steam"
    if (Test-Path $default) { return $default }
    return $null
}

# ── 主流程 ────────────────────────────────────────────────────────────────

Write-Host "=== OSU 啟動工具 ===" -ForegroundColor Cyan

# 1. 金鑰輸入
$cdkey = Show-KeyDialog
if (-not $cdkey) {
    Write-Host "[取消] 使用者取消操作。" -ForegroundColor Yellow
    exit 0
}
if (-not (Test-KeyFormat $cdkey)) {
    Write-Host "[錯誤] 金鑰格式不正確：$cdkey" -ForegroundColor Red
    Write-Host "  格式：LY-XXXXX-XXXXX-XXXXX-XXXXX" -ForegroundColor Gray
    pause
    exit 1
}
Write-Host "[OK] 金鑰格式正確：$cdkey"

# 2. 取得 Steam 路徑
$steamPath = Get-SteamPath
if (-not $steamPath) {
    Write-Host "[錯誤] 找不到 Steam 安裝路徑。" -ForegroundColor Red
    pause
    exit 1
}
Write-Host "[OK] Steam 路徑：$steamPath"

# 3. 關閉 Steam（若正在執行）
$steamProc = Get-Process -Name "steam" -ErrorAction SilentlyContinue
if ($steamProc) {
    Write-Host "[INFO] 正在關閉 Steam..." -ForegroundColor Yellow
    Stop-Process -Name "steam" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

# 4. 下載 xinput1_4.dll
$dllDest = Join-Path $steamPath "xinput1_4.dll"
Write-Host "[下載] xinput1_4.dll -> $dllDest"
try {
    Invoke-WebRequest -Uri $DLL_URL -OutFile $dllDest -UseBasicParsing
    Write-Host "[OK] DLL 部署完成。"
} catch {
    Write-Host "[錯誤] 下載 DLL 失敗：$_" -ForegroundColor Red
    pause
    exit 1
}

# 5. 寫入 Registry CDKey
if (-not (Test-Path $REG_OSU)) {
    New-Item -Path $REG_OSU -Force | Out-Null
}
Set-ItemProperty -Path $REG_OSU -Name "CDKey" -Value $cdkey -Type String
Write-Host "[OK] CDKey 已寫入 Registry。"

# 6. 寫入 applist.json（預設空白，Core 會從 manifest 更新）
if (-not (Test-Path $APPLIST_DIR)) {
    New-Item -ItemType Directory -Path $APPLIST_DIR -Force | Out-Null
}
$applistPath = Join-Path $APPLIST_DIR "applist.json"
if (-not (Test-Path $applistPath)) {
    '{"apps":[]}' | Out-File -FilePath $applistPath -Encoding utf8 -Force
    Write-Host "[OK] applist.json 已建立。"
}

# 7. 設定 Windows Defender 排除（避免誤報）
try {
    $exPaths = @($steamPath, $dllDest)
    foreach ($ep in $exPaths) {
        Add-MpPreference -ExclusionPath $ep -ErrorAction SilentlyContinue
    }
    Write-Host "[OK] Defender 排除已設定。"
} catch {
    Write-Host "[略過] Defender 排除設定失敗（可能需系統管理員）。" -ForegroundColor Yellow
}

# 8. 啟動 Steam
$steamExe = Join-Path $steamPath "steam.exe"
if (Test-Path $steamExe) {
    Write-Host "[啟動] Steam..." -ForegroundColor Green
    Start-Process $steamExe
} else {
    Write-Host "[警告] 找不到 steam.exe，請手動啟動 Steam。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 完成！===" -ForegroundColor Green
Write-Host "Steam 啟動後 xinput1_4.dll 會自動載入並驗證金鑰。" -ForegroundColor Gray
Start-Sleep -Seconds 3
