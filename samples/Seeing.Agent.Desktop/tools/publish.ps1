# 发布 Seeing 桌面版（框架依赖）。
#
# 为什么需要脚本而不是直接 dotnet publish：
#   samples/Seeing.Agent.WebUI/obj/<Config>/net10.0/ 下会同时存在多个 RID 分支
#   （win-x64 / linux-x64 / osx-arm64 / ref / refint），它们共用同一组 .cache
#   文件。当先后用不同 -r 构建时，MSBuild 的增量判断会读到交叉污染的缓存，
#   出现「源码改了但引用程序集没更新」的假成功，随后在宿主侧报
#   CS1501「方法没有采用 N 个参数的重载」这类诡异错误。
#
#   本脚本先清理受影响项目的 obj/bin，再发布，代价是每次全量编译（约 1-2 分钟），
#   换来结果确定。

param(
    [ValidateSet("win-x64", "linux-x64", "osx-arm64", "osx-x64")]
    [string]$Runtime = "win-x64",

    [string]$OutputDir,

    # 跳过清理（确定 obj 干净时可用，例如刚 clone 的仓库）
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"

# 脚本位于 <repo>/samples/Seeing.Agent.Desktop/tools/，上溯三级到仓库根
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
Set-Location $repoRoot

if (-not $OutputDir) {
    $ridName = $Runtime -replace '^win-', 'win-' -replace '^osx-', 'osx-' -replace '^linux-', 'linux-'
    $OutputDir = Join-Path $repoRoot "artifacts/seeing-$ridName"
}

$targets = @(
    "samples/Seeing.Agent.WebUI/obj",
    "samples/Seeing.Agent.WebUI/bin",
    "samples/Seeing.Agent.WebUI.Files/obj",
    "samples/Seeing.Agent.WebUI.Files/bin",
    "samples/Seeing.Agent.Desktop/obj",
    "samples/Seeing.Agent.Desktop/bin"
)

if (-not $NoClean) {
    Write-Host "清理受影响项目的 obj/bin ..." -ForegroundColor Cyan
    foreach ($t in $targets) {
        $full = Join-Path $repoRoot $t
        if (Test-Path $full) {
            Remove-Item -Recurse -Force $full
        }
    }
}

Write-Host "发布 Seeing ($Runtime) → $OutputDir" -ForegroundColor Cyan

# 不加 --self-contained true：见 samples/Seeing.Agent.Desktop/README.md 的说明，
# 自包含会触发 NETSDK1150（WebUI 是 OutputType=Exe）。
& dotnet publish "samples/Seeing.Agent.Desktop" `
    -c Release `
    -r $Runtime `
    -o $OutputDir `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "发布失败（退出码 $LASTEXITCODE）"
}

$exe = Join-Path $OutputDir "Seeing.exe"
if (Test-Path $exe) {
    $sizeMb = [math]::Round(
        ((Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host "完成：$exe（总大小 ${sizeMb} MB）" -ForegroundColor Green
} else {
    Write-Host "发布完成，但未找到 Seeing.exe" -ForegroundColor Yellow
}
