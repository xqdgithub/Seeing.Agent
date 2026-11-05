# 从 wwwroot/ai.svg 的原始几何生成应用图标（PNG 多尺寸 + ICO）。
# SVG 内容为两条 32 半径（线宽 64）的圆头斜线，颜色 #1890ff 与 #1C1C1E，
# 坐标取自 ai.svg 的路径中心线，因此这里直接按几何绘制，保真度与原图一致。
param(
    [string]$OutDir = "samples/Seeing.Agent.Desktop/Assets"
)

Add-Type -AssemblyName System.Drawing

$size = 1024
# 中心线端点（由 SVG 轮廓路径 ± 半线宽还原）
$blue  = @{ A = @(519.088, 102.752); B = @(897.744, 914.784); Color = [System.Drawing.ColorTranslator]::FromHtml("#1890ff") }
$black = @{ A = @(521.184, 129.808); B = @(142.496, 941.840); Color = [System.Drawing.ColorTranslator]::FromHtml("#1C1C1E") }
$strokeWidth = 64

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

function New-LogoBitmap([int]$px) {
    # 超采样 4 倍后缩小，保证小尺寸边缘平滑
    $scale = 4
    $big = $px * $scale
    $bmp = New-Object System.Drawing.Bitmap $big, $big, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $big / $size
    $w = $strokeWidth * $k

    foreach ($line in @($black, $blue)) {
        $pen = New-Object System.Drawing.Pen $line.Color, $w
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($pen,
            [single]($line.A[0] * $k), [single]($line.A[1] * $k),
            [single]($line.B[0] * $k), [single]($line.B[1] * $k))
        $pen.Dispose()
    }
    $g.Dispose()

    if ($scale -ne 1) {
        $final = New-Object System.Drawing.Bitmap $px, $px, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g2 = [System.Drawing.Graphics]::FromImage($final)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g2.Clear([System.Drawing.Color]::Transparent)
        $g2.DrawImage($bmp, 0, 0, $px, $px)
        $g2.Dispose()
        $bmp.Dispose()
        return $final
    }
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256, 512)
$pngBytes = @{}

foreach ($s in $sizes) {
    $bmp = New-LogoBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes[$s] = $ms.ToArray()
    if ($s -le 512) {
        [System.IO.File]::WriteAllBytes((Join-Path $OutDir "seeing-$s.png"), $pngBytes[$s])
    }
    $ms.Dispose()
    $bmp.Dispose()
}

# 单文件 used by Photino/Linux 与文档：1024 透明 PNG
$logo = New-LogoBitmap 512
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

Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize
