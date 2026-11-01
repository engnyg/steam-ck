# Steam CDK 序號兌換
# 使用方式（一行執行）:
#   irm steamfn.com | iex
#
# 或下載後執行:
#   .\activate-cdk.ps1

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ([System.Management.Automation.PSTypeName]'Win32').Type) {
    Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int  GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
}
"@
}

# ─── GUI 輸入視窗 ────────────────────────────────────────────────────────────
$form                 = New-Object System.Windows.Forms.Form
$form.Text            = 'Steam CDK 序號兌換'
$form.Size            = New-Object System.Drawing.Size(420, 160)
$form.StartPosition   = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimizeBox     = $false
$form.TopMost         = $true

$label          = New-Object System.Windows.Forms.Label
$label.Text     = '輸入 Steam CDK 序號（格式：XXXXX-XXXXX-XXXXX）'
$label.Location = New-Object System.Drawing.Point(12, 14)
$label.Size     = New-Object System.Drawing.Size(390, 18)
$form.Controls.Add($label)

$textBox                  = New-Object System.Windows.Forms.TextBox
$textBox.Location         = New-Object System.Drawing.Point(12, 38)
$textBox.Size             = New-Object System.Drawing.Size(384, 24)
$textBox.Font             = New-Object System.Drawing.Font('Consolas', 12)
$textBox.CharacterCasing  = 'Upper'
$textBox.PlaceholderText  = 'XXXXX-XXXXX-XXXXX'
$form.Controls.Add($textBox)

$btnOK              = New-Object System.Windows.Forms.Button
$btnOK.Text         = '啟動序號'
$btnOK.Location     = New-Object System.Drawing.Point(220, 76)
$btnOK.Size         = New-Object System.Drawing.Size(92, 30)
$btnOK.BackColor    = [System.Drawing.Color]::FromArgb(0, 120, 212)
$btnOK.ForeColor    = [System.Drawing.Color]::White
$btnOK.FlatStyle    = 'Flat'
$btnOK.DialogResult = [System.Windows.Forms.DialogResult]::OK
$form.AcceptButton  = $btnOK
$form.Controls.Add($btnOK)

$btnCancel              = New-Object System.Windows.Forms.Button
$btnCancel.Text         = '取消'
$btnCancel.Location     = New-Object System.Drawing.Point(316, 76)
$btnCancel.Size         = New-Object System.Drawing.Size(80, 30)
$btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
$form.CancelButton      = $btnCancel
$form.Controls.Add($btnCancel)

$form.Add_Shown({ $textBox.Focus() })
$dlgResult = $form.ShowDialog()

if ($dlgResult -ne [System.Windows.Forms.DialogResult]::OK) { return }

$key = $textBox.Text.Trim().ToUpper()
if ([string]::IsNullOrWhiteSpace($key)) { return }

# ─── 複製到剪貼簿 + 開啟 Steam 兌換頁 ───────────────────────────────────────
[System.Windows.Forms.Clipboard]::SetText($key)
Start-Process 'steam://open/activateproduct'

# ─── 等 Steam 視窗出現，自動填入 ────────────────────────────────────────────
$hwnd    = [IntPtr]::Zero
$elapsed = 0

while ($elapsed -lt 15) {
    Start-Sleep -Milliseconds 800
    $elapsed += 0.8
    $script:hw = [IntPtr]::Zero

    $cb = [Win32+EnumProc]{
        param([IntPtr]$h, [IntPtr]$p)
        if ([Win32]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Win32]::GetWindowText($h, $sb, 256)
            if ($sb.ToString() -match 'Steam' -and $sb.ToString() -match 'Activate|Product|序號|啟動|兌換') {
                $script:hw = $h; return $false
            }
        }
        return $true
    }
    [Win32]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    if ($script:hw -ne [IntPtr]::Zero) { $hwnd = $script:hw; break }
}

if ($hwnd -ne [IntPtr]::Zero) {
    [Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('^v')
    [System.Windows.Forms.MessageBox]::Show(
        "序號已填入！`n請按「下一步 → 我同意 → 完成」完成兌換。",
        'Steam CDK', 'OK', 'Information') | Out-Null
} else {
    [System.Windows.Forms.MessageBox]::Show(
        "Steam 兌換頁已開啟。`n請在序號欄按 Ctrl+V 貼上。",
        'Steam CDK', 'OK', 'Warning') | Out-Null
}
