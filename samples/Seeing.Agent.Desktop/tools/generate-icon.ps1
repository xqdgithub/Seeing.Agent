# 从 WebUI 的品牌主图生成桌面应用图标（PNG 多尺寸 + ICO）。
#
# 数据源：samples/Seeing.Agent.WebUI/wwwroot/seeing-mark.png
#   —— 由源图裁出的首字母 S（透明底、高 1024）。改图标应先更新这张主图，
#      再重跑本脚本；不要手工逐个改 PNG，否则多尺寸之间会漂移。
#
# 为什么用首字母 S 而不是完整字标：完整 SEEING 字标宽高比 ≈4.56，
# 压进 16×16 / 32×32 的正方形会糊成一条不可读的横线（实测 16px 下
# 纯色像素为 0）。S 的近方形比例能撑住小尺寸。
param(
    [string]$OutDir = "samples/Seeing.Agent.Desktop/Assets",
    [string]$SourceMark = "samples/Seeing.Agent.WebUI/wwwroot/seeing-mark.png"
)

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $SourceMark)) {
    throw "找不到品牌主图：$SourceMark"
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$master = New-Object System.Drawing.Bitmap (Resolve-Path $SourceMark).Path
Write-Host ("主图: {0}  {1}x{2}" -f $SourceMark, $master.Width, $master.Height)

# 等比放进正方形，四边留 6% 内边距（与 WebUI 侧栏折叠图标同一口径）
function New-IconBitmap([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap $px, $px, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [int][math]::Round($px * 0.06)
    $avail = $px - 2 * $pad
    $scale = [math]::Min($avail / $master.Width, $avail / $master.Height)
    $w = [int][math]::Max(1, [math]::Round($master.Width * $scale))
    $h = [int][math]::Max(1, [math]::Round($master.Height * $scale))

    $g.DrawImage($master, [int](($px - $w) / 2), [int](($px - $h) / 2), $w, $h)
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256, 512)
$pngBytes = @{}

foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes[$s] = $ms.ToArray()
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir "seeing-$s.png"), $pngBytes[$s])
    $ms.Dispose()
    $bmp.Dispose()
}

# 单文件 used by Photino/Linux 与文档
$logo = New-IconBitmap 512
$logo.Save((Join-Path $OutDir "seeing.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()

# 组装 ICO（Vista+ 支持内嵌 PNG 条目）
$icoSizes = @(16, 24, 32, 48, 64, 128, 256)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$icoSizes.Count)   # image count

$offset = 6 + (16 * $icoSizes.Count)
foreach ($s in $icoSizes) {
    $data = $pngBytes[$s]
    # 256 在 ICO 头里记作 0
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)      # width
    $bw.Write([Byte]$dim)      # height
    $bw.Write([Byte]0)         # palette
    $bw.Write([Byte]0)         # reserved
    $bw.Write([UInt16]1)       # planes
    $bw.Write([UInt16]32)      # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $icoSizes) {
    $bw.Write($pngBytes[$s])
}
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "seeing.ico"), $ms.ToArray())
$ms.Dispose()

$master.Dispose()

Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize
