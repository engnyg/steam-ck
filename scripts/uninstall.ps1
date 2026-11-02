# OSU uninstall.ps1
# 用法：irm https://cdn.ourdomain.com/uninstall | iex
#
# 清除項目：
#   1. 關閉 Steam
#   2. 刪除 xinput1_4.dll（Steam 目錄）
#   3. 刪除 Registry HKCU:\Software\OSU
#   4. 刪除 %APPDATA%\OSU\
#   5. 刪除 Steam appcache 內的 OSU payload 快取
#   6. 移除 Windows Defender 排除規則
#   7. 重新啟動 Steam

$ErrorActionPreference = "SilentlyContinue"

$REG_OSU     = "HKCU:\Software\OSU"
$APPLIST_DIR = "$env:APPDATA\OSU"

# ── GUI 確認對話框 ────────────────────────────────────────────────────────────
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Show-ConfirmDialog {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "OSU - 卸載確認"
    $form.Size = New-Object System.Drawing.Size(400, 180)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "確定要卸載 OSU 並移除所有相關檔案？`n`n此操作將停止 Steam 並清除 OSU 金鑰與 DLL。"
    $label.Location = New-Object System.Drawing.Point(20, 20)
    $label.Size = New-Object System.Drawing.Size(355, 55)
    $form.Controls.Add($label)

    $btnYes = New-Object System.Windows.Forms.Button
    $btnYes.Text = "確定卸載"
    $btnYes.Location = New-Object System.Drawing.Point(180, 100)
    $btnYes.Size = New-Object System.Drawing.Size(90, 30)
    $btnYes.BackColor = [System.Drawing.Color]::FromArgb(200, 50, 50)
    $btnYes.ForeColor = [System.Drawing.Color]::White
    $btnYes.FlatStyle = "Flat"
    $btnYes.DialogResult = [System.Windows.Forms.DialogResult]::Yes
    $form.AcceptButton = $btnYes
    $form.Controls.Add($btnYes)

    $btnNo = New-Object System.Windows.Forms.Button
    $btnNo.Text = "取消"
    $btnNo.Location = New-Object System.Drawing.Point(285, 100)
    $btnNo.Size = New-Object System.Drawing.Size(70, 30)
    $btnNo.DialogResult = [System.Windows.Forms.DialogResult]::No
    $form.CancelButton = $btnNo
    $form.Controls.Add($btnNo)

    return $form.ShowDialog()
}

# ── 取得 Steam 安裝路徑 ──────────────────────────────────────────────────────
function Get-SteamPath {
    $paths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )
    foreach ($p in $paths) {
        $val = (Get-ItemProperty -Path $p -ErrorAction SilentlyContinue).InstallPath
        if ($val -and (Test-Path $val)) { return $val }
    }
    $default = "C:\Program Files (x86)\Steam"
    if (Test-Path $default) { return $default }
    return $null
}

# ── 主流程 ────────────────────────────────────────────────────────────────────

Write-Host "=== OSU 卸載工具 ===" -ForegroundColor Cyan

$confirm = Show-ConfirmDialog
if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) {
    Write-Host "[取消] 使用者取消卸載。" -ForegroundColor Yellow
    exit 0
}

# 1. 取得 Steam 路徑
$steamPath = Get-SteamPath
if (-not $steamPath) {
    Write-Host "[警告] 找不到 Steam 安裝路徑，跳過 DLL 刪除。" -ForegroundColor Yellow
}

# 2. 關閉 Steam
$steamRunning = $false
if (Get-Process -Name "steam" -ErrorAction SilentlyContinue) {
    $steamRunning = $true
    Write-Host "[INFO] 正在關閉 Steam..." -ForegroundColor Yellow
    Stop-Process -Name "steam" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

# 3. 刪除 xinput1_4.dll
if ($steamPath) {
    $dllPath = Join-Path $steamPath "xinput1_4.dll"
    if (Test-Path $dllPath) {
        Remove-Item -Path $dllPath -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $dllPath)) {
            Write-Host "[OK] 已刪除：$dllPath"
        } else {
            Write-Host "[錯誤] 無法刪除 $dllPath（檔案可能被鎖定）" -ForegroundColor Red
        }
    } else {
        Write-Host "[略過] xinput1_4.dll 不存在。" -ForegroundColor Gray
    }
}

# 4. 刪除 Registry HKCU:\Software\OSU
if (Test-Path $REG_OSU) {
    Remove-Item -Path $REG_OSU -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] 已刪除 Registry：$REG_OSU"
} else {
    Write-Host "[略過] Registry 項目不存在。" -ForegroundColor Gray
}

# 5. 刪除 %APPDATA%\OSU\
if (Test-Path $APPLIST_DIR) {
    Remove-Item -Path $APPLIST_DIR -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] 已刪除目錄：$APPLIST_DIR"
} else {
    Write-Host "[略過] $APPLIST_DIR 不存在。" -ForegroundColor Gray
}

# 6. 刪除 Steam appcache 內的 OSU payload 快取
#    Loader 下載的加密 payload 放在 appcache/httpcache/3a、3b、3c
if ($steamPath) {
    $cachePaths = @(
        (Join-Path $steamPath "appcache\httpcache\3a"),
        (Join-Path $steamPath "appcache\httpcache\3b"),
        (Join-Path $steamPath "appcache\httpcache\3c")
    )
    foreach ($cp in $cachePaths) {
        if (Test-Path $cp) {
            Remove-Item -Path $cp -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "[OK] 已清除快取：$cp"
        }
    }
}

# 7. 移除 Windows Defender 排除規則
if ($steamPath) {
    try {
        Remove-MpPreference -ExclusionPath $steamPath -ErrorAction SilentlyContinue
        $dllPath = Join-Path $steamPath "xinput1_4.dll"
        Remove-MpPreference -ExclusionPath $dllPath -ErrorAction SilentlyContinue
        Write-Host "[OK] Defender 排除規則已移除。"
    } catch {
        Write-Host "[略過] Defender 排除移除失敗（可能需系統管理員）。" -ForegroundColor Yellow
    }
}

# 8. 詢問是否重新啟動 Steam
Write-Host ""
Write-Host "=== 卸載完成 ===" -ForegroundColor Green

if ($steamRunning) {
    $restartForm = New-Object System.Windows.Forms.Form
    $restartForm.Text = "卸載完成"
    $restartForm.Size = New-Object System.Drawing.Size(340, 140)
    $restartForm.StartPosition = "CenterScreen"
    $restartForm.FormBorderStyle = "FixedDialog"
    $restartForm.MaximizeBox = $false

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = "OSU 已完全移除。`n是否重新啟動 Steam？"
    $lbl.Location = New-Object System.Drawing.Point(20, 20)
    $lbl.AutoSize = $true
    $restartForm.Controls.Add($lbl)

    $btnRestart = New-Object System.Windows.Forms.Button
    $btnRestart.Text = "重新啟動"
    $btnRestart.Location = New-Object System.Drawing.Point(140, 70)
    $btnRestart.Size = New-Object System.Drawing.Size(90, 28)
    $btnRestart.DialogResult = [System.Windows.Forms.DialogResult]::Yes
    $restartForm.Controls.Add($btnRestart)

    $btnClose = New-Object System.Windows.Forms.Button
    $btnClose.Text = "不了"
    $btnClose.Location = New-Object System.Drawing.Point(245, 70)
    $btnClose.Size = New-Object System.Drawing.Size(60, 28)
    $btnClose.DialogResult = [System.Windows.Forms.DialogResult]::No
    $restartForm.CancelButton = $btnClose
    $restartForm.Controls.Add($btnClose)

    if ($restartForm.ShowDialog() -eq [System.Windows.Forms.DialogResult]::Yes) {
        $steamExe = Join-Path $steamPath "steam.exe"
        if (Test-Path $steamExe) {
            Start-Process $steamExe
        }
    }
}
