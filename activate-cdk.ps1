# Steam CDK 序號兌換
# 使用方式:
#   irm steamfn.com | iex
#   .\activate-cdk.ps1

[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'

# ─── 管理員自動提權 ───────────────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    try {
        $scriptText = $MyInvocation.MyCommand.Definition
        if ([string]::IsNullOrWhiteSpace($scriptText)) {
            # irm | iex 模式：把整個腳本 encode 後以系統管理員重新跑
            $scriptText = (irm steamfn.com)   # 重新抓一次
        }
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($scriptText))
        Start-Process powershell -Verb RunAs `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
    } catch {
        Write-Host "[需要管理員權限，請右鍵「以系統管理員身份執行」]" -ForegroundColor Red
        Start-Sleep 5
    }
    exit
}

# ─── Banner ───────────────────────────────────────────────────────────────────
cls
Write-Host ""
Write-Host "  Steam CDK 序號兌換工具" -ForegroundColor Cyan
Write-Host "  ─────────────────────────────────────" -ForegroundColor DarkGray
Write-Host ""

# ─── 輔助函式 ─────────────────────────────────────────────────────────────────
function Remove-IfExists($path) {
    if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}

function ForceStop-Steam {
    Get-Process "steam" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (Get-Process "steam" -ErrorAction SilentlyContinue) {
        Start-Process cmd -ArgumentList "/c taskkill /f /im steam.exe" -WindowStyle Hidden
        Start-Sleep -Seconds 2
    }
}

function Get-FileRobust($url, $dest) {
    Remove-IfExists $dest

    # 方法 1: HttpWebRequest（最快）
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = "Mozilla/5.0"
        $req.AllowAutoRedirect = $true; $req.Timeout = 30000
        $resp = $req.GetResponse()
        $rs   = $resp.GetResponseStream()
        $fs   = [System.IO.FileStream]::new($dest, 'Create', 'Write', 'None', 4MB)
        $buf  = New-Object byte[] 4MB
        while (($n = $rs.Read($buf, 0, $buf.Length)) -gt 0) { $fs.Write($buf, 0, $n) }
        $fs.Flush(); $fs.Close(); $rs.Close(); $resp.Close()
        if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { return $true }
    } catch {}

    # 方法 2: BITS
    try {
        Start-BitsTransfer -Source $url -Destination $dest -Priority Foreground -ErrorAction Stop
        if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { return $true }
    } catch {}

    # 方法 3: WebClient
    try {
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add("User-Agent", "Mozilla/5.0")
        $wc.DownloadFile($url, $dest)
        $wc.Dispose()
        if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { return $true }
    } catch {}

    return $false
}

function Expand-ZipTo($zip, $dest) {
    if (-not (Test-Path $dest)) { New-Item $dest -ItemType Directory -Force | Out-Null }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $z = [IO.Compression.ZipFile]::OpenRead($zip)
        foreach ($e in $z.Entries) {
            $target = Join-Path $dest $e.FullName
            if ([string]::IsNullOrEmpty($e.Name)) {
                if (-not (Test-Path $target)) { New-Item $target -ItemType Directory -Force | Out-Null }
                continue
            }
            $dir = Split-Path $target -Parent
            if (-not (Test-Path $dir)) { New-Item $dir -ItemType Directory -Force | Out-Null }
            try { [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target, $true) } catch {}
        }
        $z.Dispose(); return $true
    } catch {}
    try { Expand-Archive -Path $zip -DestinationPath $dest -Force -ErrorAction Stop; return $true } catch {}
    return $false
}

# ─── Steam 路徑 ───────────────────────────────────────────────────────────────
$steamRegPath = 'HKCU:\Software\Valve\Steam'
$steamPath    = ""

if (Test-Path $steamRegPath) {
    $p = Get-ItemProperty $steamRegPath -ErrorAction SilentlyContinue
    if ($p -and $p.PSObject.Properties['SteamPath']) { $steamPath = $p.SteamPath }
}

if ([string]::IsNullOrWhiteSpace($steamPath) -or -not (Test-Path $steamPath)) {
    Write-Host "[ERROR] 找不到 Steam 安裝路徑，請先安裝 Steam。" -ForegroundColor Red
    Start-Sleep 8; exit
}
Write-Host "[OK]   Steam：$steamPath" -ForegroundColor Green

# ─── 關閉 Steam ───────────────────────────────────────────────────────────────
Write-Host "[INFO] 正在關閉 Steam..." -ForegroundColor Cyan
ForceStop-Steam

# ─── 下載並部署引擎 DLL ───────────────────────────────────────────────────────
$dllNames = @("OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll")
$needDeploy = $dllNames | Where-Object { -not (Test-Path (Join-Path $steamPath $_)) }

if ($needDeploy) {
    Write-Host "[INFO] 正在下載引擎元件..." -ForegroundColor Cyan

    # 從 GitHub Releases 抓最新 ZIP
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $meta    = Invoke-RestMethod "https://api.github.com/repos/OpenSteam001/OpenSteamTool/releases/latest" `
                       -Headers @{ 'User-Agent' = 'Mozilla/5.0' }
        $zipUrl  = ($meta.assets | Where-Object { $_.name -match 'Release.*\.zip$' } | Select-Object -First 1).browser_download_url
    } catch { $zipUrl = $null }

    if (-not $zipUrl) {
        Write-Host "[ERROR] 無法取得下載連結，請檢查網路後重試。" -ForegroundColor Red
        Start-Sleep 8; exit
    }

    $zipPath = Join-Path $env:TEMP "ost_release.zip"
    $ok = Get-FileRobust $zipUrl $zipPath
    if (-not $ok) {
        Write-Host "[ERROR] 下載失敗，請重試。" -ForegroundColor Red
        Start-Sleep 8; exit
    }

    $tmpDir = Join-Path $env:TEMP "ost_extract"
    Remove-IfExists $tmpDir
    Expand-ZipTo $zipPath $tmpDir | Out-Null
    Remove-IfExists $zipPath

    foreach ($dll in $dllNames) {
        $src = Get-ChildItem $tmpDir -Recurse -Filter $dll -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($src) {
            Copy-Item $src.FullName (Join-Path $steamPath $dll) -Force
            Write-Host "[OK]   $dll → Steam/" -ForegroundColor Green
        } else {
            Write-Host "[WARN] 找不到 $dll" -ForegroundColor Yellow
        }
    }
    Remove-IfExists $tmpDir
} else {
    Write-Host "[OK]   引擎 DLL 已就緒" -ForegroundColor Green
}

# ─── 寫入 opensteamtool.toml（啟用 CDK 模式）────────────────────────────────
$tomlPath = Join-Path $steamPath "opensteamtool.toml"
$toml = @"
[log]
level = "info"

[manifest]
url = "opensteamtool"

[lua]

[inject]
enabled = true

[cdkey]
enabled = true
"@
Set-Content $tomlPath $toml -Encoding UTF8 -Force
Write-Host "[OK]   CDK 模式已設定" -ForegroundColor Green

# ─── 啟動 Steam ───────────────────────────────────────────────────────────────
Write-Host "[INFO] 正在啟動 Steam..." -ForegroundColor Cyan
$steamExe = Join-Path $steamPath "steam.exe"
$launched = $false
try { Start-Process explorer.exe "`"$steamExe`""; $launched = $true } catch {}
if (-not $launched) { try { Start-Process $steamExe; $launched = $true } catch {} }
if (-not $launched) { Start-Process "steam://" }

Write-Host ""
Write-Host "[DONE] Steam 已啟動，請在 Steam 中進行序號兌換。" -ForegroundColor Green
Write-Host ""

for ($i = 5; $i -ge 1; $i--) {
    Write-Host "`r  視窗將在 $i 秒後關閉..." -NoNewline
    Start-Sleep 1
}

# 關閉本視窗
$proc = Get-CimInstance Win32_Process -Filter "ProcessId = '$PID'" -ErrorAction SilentlyContinue
$parent = if ($proc) { Get-CimInstance Win32_Process -Filter "ProcessId = '$($proc.ParentProcessId)'" -ErrorAction SilentlyContinue } else { $null }
if ($parent -and $parent.ProcessName -match 'powershell|WindowsTerminal|cmd') {
    Stop-Process -Id $parent.ProcessId -Force -ErrorAction SilentlyContinue
}
exit
