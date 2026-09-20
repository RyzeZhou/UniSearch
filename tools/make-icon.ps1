# 生成 UniSearch 应用图标（多尺寸 .ico）。
#
# 为什么自己画而不是找现成的：仓库里没有任何 .ico，而托盘图标就是这个程序在桌面上的脸面，
# 用默认的 .NET 通用图标一眼就能看出是没做完的东西。
# 自己画的另一个好处是许可证干净 —— 不引入任何来路不明的图形资源。
#
# 图案：蓝色圆角方块 + 白色放大镜（Segoe MDL2 Assets 的 E721）。
# 用 WPF 渲染而不是 GDI+，是为了和程序界面用同一套字形与抗锯齿。
#
# 输出：src/UniSearch.Host/Assets/UniSearch.ico
#
# 两个关键取舍（都踩过）：
#   1) 小尺寸（<=64）必须写 **BMP/DIB 帧**，不能只写 PNG 帧。
#      只有 PNG 帧的 ICO，Windows 外壳能显示，但 GDI+ 的 System.Drawing.Icon
#      一读就抛 "Requested range extends past the end of the array" ——
#      别的工具/库拿到我们的图标会直接失败。128/256 用 PNG（Vista+ 支持，且省体积）。
#   2) PowerShell 5.1 里 `New-Object 类型(参数)` 的跨行写法会解析失败，一律用 `[类型]::new(...)`。
param(
    [string]$Out = "$PSScriptRoot\..\src\UniSearch.Host\Assets\UniSearch.ico"
)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# (尺寸, 是否用 PNG 帧)
$frames = @(
    @{ Size = 16;  Png = $false },
    @{ Size = 20;  Png = $false },
    @{ Size = 24;  Png = $false },
    @{ Size = 32;  Png = $false },
    @{ Size = 40;  Png = $false },
    @{ Size = 48;  Png = $false },
    @{ Size = 64;  Png = $false },
    @{ Size = 128; Png = $true  },
    @{ Size = 256; Png = $true  }
)

$culture = [System.Globalization.CultureInfo]::InvariantCulture
$flow = [System.Windows.FlowDirection]::LeftToRight
$pixelFormat = [System.Windows.Media.PixelFormats]::Pbgra32
$white = [System.Windows.Media.Brushes]::White

$bgBrush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x2D, 0x7F, 0xF9))
$bgBrush.Freeze()
$typeface = [System.Windows.Media.Typeface]::new([System.Windows.Media.FontFamily]::new("Segoe MDL2 Assets"))
$magnifier = ([char]0xE721).ToString()

function New-IconBitmap([int]$size) {
    $dv = [System.Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $radius = [double]$size * 0.22
    $dc.DrawRoundedRectangle($bgBrush, $null, [System.Windows.Rect]::new(0, 0, $size, $size), $radius, $radius)
    $ftArgs = @($magnifier, $culture, $flow, $typeface, ([double]$size * 0.62), $white)
    $ft = New-Object -TypeName System.Windows.Media.FormattedText -ArgumentList $ftArgs
    $cx = ($size - $ft.Width) / 2
    $cy = ($size - $ft.Height) / 2
    $dc.DrawText($ft, [System.Windows.Point]::new($cx, $cy))
    $dc.Close()
    $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, $pixelFormat)
    $rtb.Render($dv)
    return $rtb
}

function ConvertTo-PngFrame($rtb, [int]$size) {
    $enc = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = [System.IO.MemoryStream]::new()
    $enc.Save($ms)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

# ICO 里的 BMP 帧 = BITMAPINFOHEADER + 32bpp 自下而上的 XOR 位图 + 1bpp AND 掩码
function ConvertTo-DibFrame($rtb, [int]$size) {
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $rtb.CopyPixels([System.Windows.Int32Rect]::new(0, 0, $size, $size), $pixels, $stride, 0)

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([UInt32]40)                  # biSize
    $bw.Write([Int32]$size)                # biWidth
    $bw.Write([Int32]($size * 2))          # biHeight = XOR + AND
    $bw.Write([UInt16]1)                   # biPlanes
    $bw.Write([UInt16]32)                  # biBitCount
    $bw.Write([UInt32]0)                   # BI_RGB
    $bw.Write([UInt32]($stride * $size))   # biSizeImage
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    # XOR：WPF 给的是预乘 alpha 的 PBGRA，DIB 要的是直通 BGRA，得反预乘
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $i = $y * $stride + $x * 4
            $b = [int]$pixels[$i]; $g = [int]$pixels[$i + 1]; $r = [int]$pixels[$i + 2]; $a = [int]$pixels[$i + 3]
            if ($a -eq 0) { $b = 0; $g = 0; $r = 0 }
            elseif ($a -ne 255) {
                $b = [Math]::Min(255, [int]($b * 255 / $a))
                $g = [Math]::Min(255, [int]($g * 255 / $a))
                $r = [Math]::Min(255, [int]($r * 255 / $a))
            }
            $bw.Write([Byte]$b); $bw.Write([Byte]$g); $bw.Write([Byte]$r); $bw.Write([Byte]$a)
        }
    }

    # AND 掩码：32bpp 下外壳看 alpha，掩码全 0 即可。行按 4 字节对齐。
    $maskStride = [int]([Math]::Floor(($size + 31) / 32) * 4)
    $bw.Write([byte[]]::new($maskStride * $size))

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

foreach ($f in $frames) {
    $rtb = New-IconBitmap $f.Size
    # 不能写成 `$f.Bytes = if (...) {...} else {...}`：if 当表达式用会把管道输出再收集一层，
    # byte[] 被包成 object[]，最后每帧只剩 1 字节（踩过：整个 ico 只有 159 字节）。
    if ($f.Png) { $f.Bytes = ConvertTo-PngFrame $rtb $f.Size }
    else        { $f.Bytes = ConvertTo-DibFrame $rtb $f.Size }
    "  frame $($f.Size): $($f.Bytes.Length) bytes"
}

# ── 打包成 ICO 容器：ICONDIR(6 字节) + ICONDIRENTRY(16 * N) + 各帧数据 ──
$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$fs = [System.IO.File]::Create($Out)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 256 在 ICO 里记作 0
    $bw.Write([Byte]$dim)            # 宽
    $bw.Write([Byte]$dim)            # 高
    $bw.Write([Byte]0)               # 调色板数
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # planes
    $bw.Write([UInt16]32)            # bpp
    $bw.Write([UInt32]$f.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

$size = (Get-Item $Out).Length
"wrote $Out ($($frames.Count) frames, $size bytes)"
"frames: " + (($frames | ForEach-Object { "$($_.Size)$(if ($_.Png) { 'p' } else { 'b' })" }) -join ", ")
