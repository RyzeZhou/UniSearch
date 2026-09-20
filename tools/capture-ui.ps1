# 用 PrintWindow 抓 UniSearch 窗口，无视遮挡与前台锁。
#
# 为什么不用 CopyFromScreen：
#   1) SetForegroundWindow 会被 Windows 前台锁拒绝，截到的常常是压在上面的别的窗口；
#   2) 屏幕锁定 / 无显示设备时它直接抛 "The handle is invalid"。
# PrintWindow(PW_RENDERFULLCONTENT) 从窗口自己的 DC 取内容，两者都不受影响。
#
# 必须 SetProcessDPIAware()：否则 GetWindowRect 返回的是虚拟化后的 DIP 尺寸（760x520），
# 而窗口物理尺寸是 1330x910（175% 缩放），只会截到左上角 57%。
param(
    [string]$Out = 'D:\tools\UniSearch\ui_capture.png',
    [int]$WaitMs = 0
)
if ($WaitMs -gt 0) { Start-Sleep -Milliseconds $WaitMs }

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
public class UICap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
  public static Bitmap Grab(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, hh = r.B - r.T;
    var bmp = new Bitmap(w, hh);
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc();
      PrintWindow(h, dc, 2);            // PW_RENDERFULLCONTENT：WPF/DX 合成窗口必须带
      g.ReleaseHdc(dc);
    }
    return bmp;
  }
}
'@
[UICap]::SetProcessDPIAware() | Out-Null

$p = Get-Process UniSearch -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'NO_PROCESS'; exit 1 }

$bmp = [UICap]::Grab($p.MainWindowHandle)
$bmp.Save($Out)

# 客观断言：图标列有彩色 = shell 图标取到了；正文区有深色 = 文字行画出来了
$colored = 0; $dark = 0
for ($y = 150; $y -lt $bmp.Height - 60; $y += 2) {
    for ($x = 30; $x -lt [Math]::Min(80, $bmp.Width); $x++) {
        $c = $bmp.GetPixel($x, $y)
        if ([Math]::Abs([int]$c.R - [int]$c.B) -gt 40 -or [Math]::Abs([int]$c.G - [int]$c.B) -gt 40) { $colored++ }
    }
    for ($x = 90; $x -lt $bmp.Width - 30; $x += 2) {
        $c = $bmp.GetPixel($x, $y)
        if ($c.R -lt 110 -and $c.G -lt 110 -and $c.B -lt 110) { $dark++ }
    }
}
Write-Output ("pid=" + $p.Id + " visible=" + [UICap]::IsWindowVisible($p.MainWindowHandle) +
              " size=" + $bmp.Width + "x" + $bmp.Height + " iconColoredPix=" + $colored + " textDarkPix=" + $dark)
Write-Output ("saved " + $Out)
