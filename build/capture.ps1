# Screenshot helper for docs: launches BitBroom (Debug build), optionally on a
# specific tab with --autoscan, brings it to the foreground and captures the window.
param(
    [int]$Tab = 0,
    [switch]$AutoScan,
    [int]$WaitSeconds = 7,
    [int]$SettleMs = 800,
    [string]$OutFile = "shot.png",
    [string]$Exe = "$PSScriptRoot\..\src\BitBroom.App\bin\Debug\net10.0-windows\BitBroom.exe"
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DpiHelper {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[DpiHelper]::SetProcessDPIAware() | Out-Null

$args = @("--tab", $Tab)
if ($AutoScan) { $args += "--autoscan" }
$proc = Start-Process -FilePath $Exe -ArgumentList $args -PassThru

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

# Wait for the window to exist FIRST, so timing below is relative to window show.
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    $proc.Refresh()
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 250
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "no main window"; exit 1 }

# Alt-key trick unlocks SetForegroundWindow from a background process.
[DpiHelper]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
[DpiHelper]::SetForegroundWindow($hwnd) | Out-Null
[DpiHelper]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
[DpiHelper]::ShowWindow($hwnd, 9) | Out-Null
[DpiHelper]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds $SettleMs
Start-Sleep -Seconds $WaitSeconds

# The splash window may have been the handle we grabbed; it is destroyed once the
# main window shows. Re-acquire the current main window handle before capturing.
$proc.Refresh()
if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle }
[DpiHelper]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
[DpiHelper]::SetForegroundWindow($hwnd) | Out-Null
[DpiHelper]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 400

$rect = New-Object DpiHelper+RECT
[DpiHelper]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { Write-Error "bad window rect"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($width, $height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Output "saved $OutFile ($width x $height)"
