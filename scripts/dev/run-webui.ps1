# 开发启动 WebUI：先停掉上一轮的浏览器宿主，避免 bin 下的 dll 被锁。
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
pwsh -File (Join-Path $PSScriptRoot "stop-browser-host.ps1")
dotnet run --project (Join-Path $repo "samples/Seeing.Agent.WebUI") @args
