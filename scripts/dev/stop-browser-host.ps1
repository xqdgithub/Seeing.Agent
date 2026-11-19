# 停止浏览器宿主（开发用）
#
# 共享宿主是独立进程，会一直锁住 bin 下的 Seeing.Agent.*.dll，
# 导致改代码后 dotnet build 无法覆盖。构建前跑一次即可。
#
# 只结束“发现文件里的 hostPid 且启动时间匹配”的进程：
# PID 被复用时不杀，避免误伤无关程序。

$browserDir = Join-Path $env:USERPROFILE ".seeing\browser"
if (-not (Test-Path $browserDir)) {
    Write-Output "没有浏览器宿主记录（$browserDir）"
    exit 0
}

$stopped = 0
Get-ChildItem $browserDir -Filter "host-*.json" -ErrorAction SilentlyContinue | ForEach-Object {
    $file = $_
    try {
        $info = Get-Content $file.FullName -Raw | ConvertFrom-Json
        $hostPid = [int]$info.hostPid
        $proc = Get-Process -Id $hostPid -ErrorAction SilentlyContinue
        if ($null -ne $proc) {
            $expected = [DateTimeOffset]$info.hostStartedAt
            $actual = [DateTimeOffset]$proc.StartTime
            if ([Math]::Abs(($actual - $expected).TotalSeconds) -lt 2) {
                Stop-Process -Id $hostPid -Force -ErrorAction SilentlyContinue
                Write-Output "已停止浏览器宿主 PID $hostPid（$($file.Name)）"
                $script:stopped++
            }
            else {
                Write-Output "跳过 PID $hostPid：启动时间不匹配（PID 可能已被复用）"
            }
        }
        else {
            Write-Output "宿主 PID $hostPid 已不存在，清理记录"
        }
    }
    catch {
        Write-Output "读取 $($file.Name) 失败：$($_.Exception.Message)"
    }
    finally {
        Remove-Item $file.FullName -Force -ErrorAction SilentlyContinue
    }
}

if ($stopped -eq 0) {
    Write-Output "没有需要停止的浏览器宿主"
}
