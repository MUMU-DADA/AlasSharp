#requires -Version 7.0
<#
Publish the standalone loopback server from the project-local .NET toolchain.
The automation repository, Python environment and device backend remain explicit
runtime inputs; this script only publishes Alas.Server and optional prebuilt UI.
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$IncludeUi,
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sdkDirectory = Join-Path $repo '.runtime/dotnet'
$dotnet = Join-Path $sdkDirectory 'dotnet.exe'
$packageDirectory = Join-Path $repo '.runtime/nuget/packages'
$packageSource = Join-Path $repo '.runtime/nuget/source'
$sdkVersion = '10.0.401'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'Project-local SDK is missing; prepare it with tools/build_ui.ps1 -Bootstrap.'
}
if (-not $Output) { $Output = Join-Path $repo '.runtime/server-publish' }
$output = [IO.Path]::GetFullPath($Output)
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repo '.runtime'))
if ([IO.Path]::GetDirectoryName($output) -ne $outputRoot -and
    -not $output.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Server output must stay under the project .runtime directory.'
}

function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE)." }
}

function Assert-NoLinks([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Server publication cannot use links.'
    }
    Get-ChildItem -LiteralPath $Path -Recurse -Force | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Server publication cannot use links.' }
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
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:PATH = $sdkDirectory + [IO.Path]::PathSeparator + $env:PATH
    $buildDirectory = Join-Path $repo '.runtime/server-build'
    New-Item -ItemType Directory -Force -Path $buildDirectory, $packageSource | Out-Null
    $nugetConfig = Join-Path $buildDirectory 'NuGet.config'
    Set-Content -LiteralPath $nugetConfig -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear />
<add key="project-cache" value="../nuget/source" />
</packageSources></configuration>
"@
    if ((& $dotnet --version) -ne $sdkVersion) { throw "Expected local SDK $sdkVersion." }
    $flags = @('--configfile', $nugetConfig, '-p:NuGetAudit=false')
    $publishArgs = @('publish', 'src/Alas.Server/Alas.Server.csproj', '-c', 'Release', '-r', $Runtime,
        '--self-contained', $SelfContained.ToString().ToLowerInvariant(), '-p:DebugType=None',
        '-p:DebugSymbols=false', '-o', $output) + $flags
    if (Test-Path -LiteralPath $output) {
        Assert-NoLinks $output
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    Invoke-Dotnet (@('restore', 'src/Alas.Server/Alas.Server.csproj', '-r', $Runtime) + $flags)
    Invoke-Dotnet $publishArgs
    Assert-NoLinks $output
    if ($IncludeUi) {
        $source = Join-Path $repo '.runtime/ui-publish/browser/wwwroot'
        if (-not (Test-Path -LiteralPath (Join-Path $source 'index.html') -PathType Leaf)) {
            throw 'Browser publication is missing; run tools/build_ui.ps1 -Publish first.'
        }
        $ui = Join-Path $output 'ui'
        Copy-Item -LiteralPath $source -Destination $ui -Recurse -Force
        Assert-NoLinks $ui
    }
    Write-Output "PASS: Alas.Server published for $Runtime under .runtime; UI included=$IncludeUi."
    Write-Output 'Runtime inputs remain explicit: --root, --repo, --data, --tools and optional --ui-root ui.'
}
finally { Pop-Location }
