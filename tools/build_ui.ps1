#requires -Version 7.0
<#
Windows UI build and native Headless verification. Never launches either UI entry point.
First use: ./tools/build_ui.ps1 -Bootstrap -Publish
Later:     ./tools/build_ui.ps1 -Publish   (project-local SDK, workload and NuGet source only)
#>
[CmdletBinding()]
param([switch]$Bootstrap, [switch]$Publish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sdkVersion = '10.0.401'
$sdkDirectory = Join-Path $repo '.runtime/dotnet'
$dotnet = Join-Path $sdkDirectory 'dotnet.exe'
$packageDirectory = Join-Path $repo '.runtime/nuget/packages'
$packageSource = Join-Path $repo '.runtime/nuget/source'
$variables = @('DOTNET_ROOT', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
    'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', 'PATH')
$saved = @{}
foreach ($key in $variables) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }

function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE)." }
}

function Save-PackageCache {
    Get-ChildItem -LiteralPath $packageDirectory -Recurse -Filter '*.nupkg' | ForEach-Object {
        $destination = Join-Path $packageSource $_.Name
        if (-not (Test-Path -LiteralPath $destination)) {
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
}

function Reset-PublishDirectory([string]$Name) {
    $publishRoot = [IO.Path]::GetFullPath((Join-Path $repo '.runtime/ui-publish'))
    $destination = [IO.Path]::GetFullPath((Join-Path $publishRoot $Name))
    if ([IO.Path]::GetDirectoryName($destination) -ne $publishRoot -or
        $Name -notin @('desktop-win-x64', 'browser')) { throw 'Unexpected UI output directory.' }
    # Do not recurse through redirected directories when cleaning generated files.
    $ancestor = $destination
    while ($ancestor -and $ancestor -ne $repo) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'UI publish directory must not use links.'
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (Test-Path -LiteralPath $destination) {
        if (Get-ChildItem -LiteralPath $destination -Recurse -Force -Attributes ReparsePoint) {
            throw 'UI publish directory contains links.'
        }
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
}

Push-Location $repo
try {
    $env:DOTNET_ROOT = $sdkDirectory
    $env:DOTNET_CLI_HOME = Join-Path $repo '.runtime/dotnet-home'
    $env:NUGET_PACKAGES = $packageDirectory
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.runtime/nuget/http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:PATH = $sdkDirectory + [IO.Path]::PathSeparator + $saved['PATH']
    New-Item -ItemType Directory -Force -Path $packageSource | Out-Null
    $buildDirectory = Join-Path $repo '.runtime/ui-build'
    New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null
    $nugetConfig = Join-Path $buildDirectory 'NuGet.config'
    $onlineSource = if ($Bootstrap) { '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' } else { '' }
    Set-Content -LiteralPath $nugetConfig -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear />
<add key="project-cache" value="../nuget/source" />
$onlineSource
</packageSources></configuration>
"@

    if ($Bootstrap) {
        if (-not (Test-Path -LiteralPath (Join-Path $sdkDirectory "sdk/$sdkVersion"))) {
            $installer = Join-Path $repo '.runtime/dotnet-install.ps1'
            Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
            & $installer -Version $sdkVersion -InstallDir $sdkDirectory -NoPath
            if (-not (Test-Path -LiteralPath (Join-Path $sdkDirectory "sdk/$sdkVersion"))) {
                throw 'Project-local SDK installation failed.'
            }
        }
    }
    if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Run this script with -Bootstrap once to cache the UI toolchain.' }
    $actualVersion = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actualVersion -ne $sdkVersion) {
        throw "Expected local SDK $sdkVersion; found $actualVersion."
    }
    if ($Bootstrap) {
        Invoke-Dotnet @('workload', 'install', 'wasm-tools', '--skip-manifest-update', '--configfile', $nugetConfig)
    }
    # Restores must work without network access after bootstrap. Package security
    # advisories are a separate online audit, not part of this offline build command.
    $sources = @('--configfile', $nugetConfig, '-p:NuGetAudit=false')

    Invoke-Dotnet (@('restore', 'Alas.UI.slnx') + $sources)
    Save-PackageCache
    Invoke-Dotnet @('build', 'Alas.UI.slnx', '-c', 'Release', '--no-restore')

    # Native Headless backend only. Bound shutdown as well as assertions so a hung
    # renderer cannot be mistaken for a successful run or remain in the background.
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('src/Alas.UI.Headless/bin/Release/net10.0/Alas.UI.Headless.dll')
    $start.ArgumentList.Add('.runtime/ui-headless')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Headless verification timed out after 90 seconds.'
        }
        Write-Output $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($errorText) { Write-Output $errorText }
        if ($process.ExitCode -ne 0) { throw "Headless verification failed (exit $($process.ExitCode))." }
    }
    finally { $process.Dispose() }

    if ($Publish) {
        # Prevent old fingerprinted assets or symbols surviving a new publication.
        Reset-PublishDirectory 'desktop-win-x64'
        Reset-PublishDirectory 'browser'
        Invoke-Dotnet (@('restore', 'src/Alas.UI.Desktop/Alas.UI.Desktop.csproj', '-r', 'win-x64', '-p:SelfContained=true') + $sources)
        Invoke-Dotnet @('publish', 'src/Alas.UI.Desktop', '-c', 'Release', '-r', 'win-x64', '--no-restore',
            '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false',
            '-o', '.runtime/ui-publish/desktop-win-x64')
        Invoke-Dotnet (@('restore', 'src/Alas.UI.Browser/Alas.UI.Browser.csproj') + $sources)
        Invoke-Dotnet @('publish', 'src/Alas.UI.Browser', '-c', 'Release', '--no-restore',
            '-p:DebugType=None', '-p:DebugSymbols=false', '-o', '.runtime/ui-publish/browser')
        Save-PackageCache
        & (Join-Path $PSScriptRoot 'diagnostics/verify_ui_artifacts.ps1')
    }
    Write-Output 'PASS: UI build and native Headless verification; no visible window or device access.'
}
finally {
    foreach ($key in $variables) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
    Pop-Location
}
