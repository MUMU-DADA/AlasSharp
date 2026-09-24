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

    # Reuse the WASM workload's bundled Node to verify the browser startup switch.
    $node = Get-ChildItem -LiteralPath (Join-Path $sdkDirectory 'packs') -Directory -Filter 'Microsoft.NET.Runtime.Emscripten.*.Node.*' |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter 'node.exe' } |
        Select-Object -First 1
    if (-not $node) { throw 'WASM workload Node runtime is missing.' }
    & $node.FullName (Join-Path $PSScriptRoot 'diagnostics/verify_ui_launch.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Browser UI-only startup check failed.' }

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

    # 界面类名检查：用了 class 却没有样式定义时（元素会按默认样式渲染、编译与断言都不报），
    # 在这里直接失败，避免"照抄了上游类名却忘了写样式"再次悄悄溜过去。
    # 使用 PowerShell 实现，使 UI 构建不依赖系统 Python 或特定工作树中的 venv。
    # 详细报告版见 tools/check_ui_classes.py（人工排查用），两者判定规则需保持一致。
    $viewRoot = Join-Path $PSScriptRoot '..\src\Alas.UI'
    $allowed = @('nav-item', 'rail-count-badge', 'monitor-action', 'segment-tab', 'field-row',
        'palette-swatch', 'heading', 'home-main', 'home-deck', 'home-editorial', 'topbar-actions')
    $used = @{}; $styled = @{}
    foreach ($file in Get-ChildItem -LiteralPath $viewRoot -Recurse -Filter *.axaml) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($line in ($text -split "`n")) {
            $themed = $line -match 'Theme="\{StaticResource'
            foreach ($m in [regex]::Matches($line, 'Classes="([^"]+)"')) {
                foreach ($name in ($m.Groups[1].Value -split '\s+')) {
                    if (-not $name) { continue }
                    $used[$name] = $true
                    if ($themed) { $styled[$name] = $true }
                }
            }
            foreach ($m in [regex]::Matches($line, 'Classes\.([A-Za-z0-9_-]+)')) { $used[$m.Groups[1].Value] = $true }
        }
        foreach ($m in [regex]::Matches($text, 'Selector="([^"]+)"')) {
            foreach ($c in [regex]::Matches($m.Groups[1].Value, '\.([A-Za-z0-9_-]+)')) { $styled[$c.Groups[1].Value] = $true }
        }
    }
    $unexplained = @($used.Keys | Where-Object { -not $styled.ContainsKey($_) -and $allowed -notcontains $_ } | Sort-Object)
    if ($unexplained.Count -gt 0) {
        throw "UI class check failed; these classes have no style and no reason: $($unexplained -join ', ')"
    }
    Write-Output "UI class check passed ($($used.Count) classes, all styled or explained)."

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
