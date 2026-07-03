# 停止 Seeing.Agent 本地开发相关进程（WebUI / Gateway Server / ChannelHost）
# 用法:
#   pwsh -File scripts/stop-dev.ps1
#   pwsh -File scripts/stop-dev.ps1 -IncludeGatewayServer

[CmdletBinding()]
param(
    [switch]$IncludeGatewayServer,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$patterns = @(
    'Seeing\.Agent\.WebUI',
    'Seeing\.Gateway\.ChannelHost'
)

if ($IncludeGatewayServer) {
    $patterns += 'Seeing\.Gateway\.Server'
}

function Get-MatchingDotNetProcesses {
    param([string[]]$RegexPatterns)

    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $cmd = $_.CommandLine
            if ([string]::IsNullOrWhiteSpace($cmd)) { return $false }
            foreach ($pattern in $RegexPatterns) {
                if ($cmd -match $pattern) { return $true }
            }
            return $false
        }
}

function Get-MatchingProcessesByName {
    param([string[]]$ProcessNames)

    foreach ($name in $ProcessNames) {
        Get-Process -Name $name -ErrorAction SilentlyContinue
    }
}

Write-Host "Stopping Seeing.Agent dev processes..." -ForegroundColor Cyan
Write-Host "Repo: $repoRoot"

$dotnetProcesses = @(Get-MatchingDotNetProcesses -RegexPatterns $patterns)
$namedProcesses = @(
    Get-MatchingProcessesByName -ProcessNames @(
        'Seeing.Agent.WebUI',
        'Seeing.Gateway.Server',
        'Seeing.Gateway.ChannelHost'
    )
)

$targets = @($dotnetProcesses + $namedProcesses) |
    Sort-Object ProcessId -Unique

if ($targets.Count -eq 0) {
    Write-Host "No matching processes found." -ForegroundColor Yellow
    exit 0
}

foreach ($proc in $targets) {
    $cmd = if ($proc.CommandLine) { $proc.CommandLine } else { $proc.ProcessName }
    Write-Host "Stopping PID $($proc.ProcessId): $cmd" -ForegroundColor Gray

    try {
        if ($Force) {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
        }
        else {
            Stop-Process -Id $proc.ProcessId -ErrorAction Stop
        }
    }
    catch {
        Write-Warning "Failed to stop PID $($proc.ProcessId): $($_.Exception.Message)"
    }
}

Start-Sleep -Milliseconds 500

$remaining = @(Get-MatchingDotNetProcesses -RegexPatterns $patterns)
if ($remaining.Count -gt 0 -and -not $Force) {
    Write-Host "Some dotnet processes still running. Retrying with -Force..." -ForegroundColor Yellow
    foreach ($proc in $remaining) {
        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Done." -ForegroundColor Green
