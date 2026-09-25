#requires -Version 7.0
<#
Verify publication safety, caller environment restoration and an offline detached
HTTP start. The test uses only a temporary runtime directory and never opens a UI
window or contacts a device.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtime = Join-Path $repo '.runtime'
$publish = Join-Path $repo 'tools/publish_server.ps1'
$dotnet = Join-Path $runtime 'dotnet/dotnet.exe'
$environmentNames = @(
    'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
    'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', 'PATH'
)

function Get-AvailablePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Get-Http([string]$Uri) {
    try {
        return Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 3
    }
    catch {
        if ($_.Exception.Response) { return $_.Exception.Response }
        return $null
    }
}

function Stop-ProcessSafe([Diagnostics.Process]$Process) {
    if ($null -eq $Process) { return }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
        $Process.WaitForExit(5000)
    }
    $Process.Dispose()
}

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'Project-local SDK is missing.'
}
if (-not (Test-Path -LiteralPath $publish -PathType Leaf)) {
    throw 'Publish script is missing.'
}

$sentinel = Join-Path $runtime 'nuget/publish-safety-sentinel.txt'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sentinel) | Out-Null
Set-Content -LiteralPath $sentinel -Value 'keep-me' -NoNewline
$dangerous = Join-Path $runtime 'nuget'
$rejected = $false
try {
    & $publish -Output $dangerous
}
catch {
    $rejected = $true
}
if (-not $rejected) { throw 'Dangerous output path was accepted.' }
if ((Get-Content -LiteralPath $sentinel -Raw) -ne 'keep-me') {
    throw 'Dangerous output check did not preserve the sentinel.'
}

$publishRoot = Join-Path $runtime 'server-publish/verify'
$runRoot = Join-Path $runtime 'server-publish/http-root'
if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $runRoot 'data') | Out-Null

$saved = @{}
foreach ($name in $environmentNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$sentinelValues = @{
    DOTNET_ROOT = 'caller-dotnet-root'
    DOTNET_CLI_HOME = 'caller-dotnet-home'
    NUGET_PACKAGES = 'caller-nuget-packages'
    NUGET_HTTP_CACHE_PATH = 'caller-nuget-http-cache'
    DOTNET_CLI_TELEMETRY_OPTOUT = 'caller-telemetry'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'caller-skip-first-use'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'caller-workload-notify'
}
foreach ($name in $sentinelValues.Keys) { [Environment]::SetEnvironmentVariable($name, $sentinelValues[$name], 'Process') }
$originalPath = $saved['PATH']
[Environment]::SetEnvironmentVariable('PATH', 'caller-path' + [IO.Path]::PathSeparator + $originalPath, 'Process')
try {
    & $publish -Runtime 'win-x64' -Output $publishRoot
    if ($LASTEXITCODE -ne 0) { throw "Publish script failed (exit $LASTEXITCODE)." }
}
finally {
    $environmentError = $null
    foreach ($name in $environmentNames) {
        if ($name -eq 'PATH') { continue }
        if ([Environment]::GetEnvironmentVariable($name, 'Process') -ne $sentinelValues[$name]) {
            $environmentError = "Publish script did not restore $name."
            break
        }
    }
    $expectedPath = 'caller-path' + [IO.Path]::PathSeparator + $originalPath
    if ($null -eq $environmentError -and
        [Environment]::GetEnvironmentVariable('PATH', 'Process') -ne $expectedPath) {
        $environmentError = 'Publish script did not preserve the caller PATH during the test.'
    }
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
    if ($null -ne $environmentError) { throw $environmentError }
}

$serverDll = Join-Path $publishRoot 'Alas.Server.dll'
if (-not (Test-Path -LiteralPath $serverDll -PathType Leaf)) { throw 'Published server DLL is missing.' }
$uiIndex = Join-Path $publishRoot 'ui/index.html'
if (-not (Test-Path -LiteralPath $uiIndex -PathType Leaf)) { throw 'Published Web UI is missing.' }
$port = Get-AvailablePort
$base = "http://127.0.0.1:$port"
$start = [Diagnostics.ProcessStartInfo]::new($dotnet)
$start.WorkingDirectory = $runRoot
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @($serverDll, '--root', $runRoot, '--port', [string]$port)) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $state = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $state = Get-Http "$base/api/state"
        if ($state -and $state.StatusCode -eq 200) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $state -or $state.StatusCode -ne 200) { throw 'Detached published server did not answer HTTP.' }
    $page = Get-Http "$base/"
    if (-not $page -or $page.StatusCode -ne 200 -or
        -not $page.Content.Contains('<html', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Detached published server did not serve its Web UI by default.'
    }
}
finally {
    Stop-ProcessSafe $process
}

# The everyday build entry must publish the same Web UI and leave only product entries.
& (Join-Path $repo 'build.ps1') -Project 'src/Alas.Server/Alas.Server.csproj' -Publish | Out-Null
if ($LASTEXITCODE -ne 0) { throw "General build publication failed (exit $LASTEXITCODE)." }
$generalRoot = Join-Path $runtime 'publish/Alas.Server'
if (-not (Test-Path -LiteralPath (Join-Path $generalRoot 'ui/index.html') -PathType Leaf)) {
    throw 'General build publication is missing the Web UI.'
}
if (Test-Path -LiteralPath (Join-Path $generalRoot 'tools/control_ui.html')) {
    throw 'General build publication retained the retired control page.'
}
$generalPort = Get-AvailablePort
$generalStart = [Diagnostics.ProcessStartInfo]::new($dotnet)
$generalStart.WorkingDirectory = $runRoot
$generalStart.UseShellExecute = $false
$generalStart.CreateNoWindow = $true
$generalStart.RedirectStandardOutput = $true
$generalStart.RedirectStandardError = $true
foreach ($argument in @((Join-Path $generalRoot 'Alas.Server.dll'), '--root', $runRoot,
        '--port', [string]$generalPort)) { $generalStart.ArgumentList.Add($argument) }
$generalProcess = [Diagnostics.Process]::Start($generalStart)
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $page = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $page = Get-Http "http://127.0.0.1:$generalPort/"
        if ($page -and $page.StatusCode -eq 200) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $page -or $page.StatusCode -ne 200 -or
        -not $page.Content.Contains('<html', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'General build publication did not serve its Web UI.'
    }
}
finally { Stop-ProcessSafe $generalProcess }

$evidence = Join-Path $runtime 'server-publish/verify-evidence.txt'
Set-Content -LiteralPath $evidence -Encoding utf8 -Value @(
    'publish_output_boundary=PASS'
    'sentinel_preserved=PASS'
    'caller_environment_restored=PASS'
    'offline_publish=PASS'
    'detached_http_start=PASS'
    'web_ui_default=PASS'
    'general_build_web_ui=PASS'
    'device_access=NOT_USED'
)
Write-Output 'PASS: both Server publishers serve the shared Web UI; output boundary and environment restoration verified.'
