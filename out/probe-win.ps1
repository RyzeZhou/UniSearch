Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public class WQ {
  public delegate bool Proc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Proc p, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static string Dump(uint onlyPid) {
    var sb = new StringBuilder();
    int total = 0;
    EnumWindows((h, l) => {
      total++;
      uint pid; GetWindowThreadProcessId(h, out pid);
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
      bool vis = IsWindowVisible(h);
      bool interesting = cls.ToString() == "#32770" || (onlyPid != 0 && pid == onlyPid);
      if (interesting)
        sb.AppendLine("pid=" + pid + " class=" + cls + " visible=" + vis + " title=" + txt);
      return true;
    }, IntPtr.Zero);
    sb.Insert(0, "total top-level windows=" + total + "\n");
    return sb.ToString();
  }
}
'@
$p = Get-Process UniSearch -ErrorAction SilentlyContinue | Select-Object -First 1
$pid2 = 0
if ($p) { $pid2 = $p.Id }
Write-Output ("UniSearch pid=" + $pid2)
Write-Output ([WQ]::Dump([uint32]$pid2))
