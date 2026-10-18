param(
  [string]$Output = "preview.png"
)

$ErrorActionPreference = "Stop"

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class MurmurShot {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr FindFor(uint pid) {
    SetProcessDpiAwarenessContext(new IntPtr(-4));
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint owner;
      GetWindowThreadProcessId(h, out owner);
      if (owner != pid || !IsWindowVisible(h)) return true;
      var text = new StringBuilder(256);
      GetWindowText(h, text, text.Capacity);
      if (text.ToString() == "Murmur") {
        found = h;
        return false;
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@

$process = Get-Process murmur -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -like '*\STT App\app\target\debug\murmur.exe' } |
  Select-Object -First 1
if (-not $process) {
  throw "Murmur preview is not running."
}

$hwnd = [MurmurShot]::FindFor([uint32]$process.Id)
if ($hwnd -eq [IntPtr]::Zero) {
  throw "Murmur window not found."
}

$rect = New-Object MurmurShot+RECT
[MurmurShot]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

Add-Type -AssemblyName System.Drawing
$full = New-Object System.Drawing.Bitmap 2880, 1800
$graphics = [System.Drawing.Graphics]::FromImage($full)
$graphics.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size 2880, 1800))
$graphics.Dispose()

$crop = New-Object System.Drawing.Bitmap $width, $height
$graphics2 = [System.Drawing.Graphics]::FromImage($crop)
$graphics2.DrawImage(
  $full,
  (New-Object System.Drawing.Rectangle 0, 0, $width, $height),
  (New-Object System.Drawing.Rectangle $rect.Left, $rect.Top, $width, $height),
  [System.Drawing.GraphicsUnit]::Pixel
)
$graphics2.Dispose()

$resolved = [System.IO.Path]::GetFullPath($Output)
$crop.Save($resolved, [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose()
$full.Dispose()
Write-Output "$resolved ($width x $height)"
