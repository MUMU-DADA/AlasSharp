#requires -Version 7.0
<#
根目录增量构建脚本。只构建，不启动窗口、不访问设备、不发布。

  ./build.ps1                                      # 增量构建主解决方案 Alas.sln（Release）
  ./build.ps1 -Configuration Debug                 # 换构建配置
  ./build.ps1 -Project src/Alas.DataTool/Alas.DataTool.csproj   # 只构建单个项目（内循环最快）
  ./build.ps1 -Ui                                  # 追加构建共享 UI 解决方案 Alas.UI.slnx
  ./build.ps1 -Restore                             # 强制还原
  ./build.ps1 -Clean                               # 非增量：先清理再重建

为什么默认是增量的（重复构建只花几秒）：
  1. 不清理任何输出，交给 MSBuild 自己的最新检查，没改动的项目不重编；
  2. 显式保留 MSBuild 节点复用与 Roslyn 编译器服务，省掉重复的进程启动与 JIT；
  3. 还原只在真有项目缺少或过期 obj/project.assets.json 时才执行，其余情况走 --no-restore。

发布与验收不在本脚本范围内：共享 UI 的发布与 Headless 验收用 tools/build_ui.ps1，
服务端发布用 tools/publish_server.ps1。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $Project = '',
    [switch] $Ui,
    [switch] $Clean,
    [switch] $Restore
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
if (-not $repo) { $repo = (Get-Location).Path }
$dotnet = ''

$environmentNames = @('DOTNET_ROOT', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
    'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_NOLOGO',
    'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', 'DOTNET_CLI_USE_MSBUILD_SERVER',
    'MSBUILDDISABLENODEREUSE', 'PATH')
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

# dotnet 的输出要留在管道里给调用方看，所以耗时通过脚本级变量单独回报，避免混进返回值。
$script:lastElapsed = [TimeSpan]::Zero
function Invoke-Dotnet([string[]] $Arguments) {
    $started = Get-Date
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) 失败（退出码 $LASTEXITCODE）。" }
    $script:lastElapsed = (Get-Date) - $started
}

function Format-Elapsed([TimeSpan] $Value) {
    if ($Value.TotalSeconds -lt 1) { return "$([int]$Value.TotalMilliseconds) ms" }
    return "$([Math]::Round($Value.TotalSeconds, 1)) s"
}

# 解决方案里声明的项目：.sln 是文本行，.slnx 是 XML；目标本身就是项目时直接用它。
function Get-TargetProjects([string] $Target) {
    if ($Target.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) { return @($Target) }
    $directory = Split-Path -Parent $Target
    $text = Get-Content -LiteralPath $Target -Raw
    $paths = if ($Target.EndsWith('.slnx', [StringComparison]::OrdinalIgnoreCase)) {
        [regex]::Matches($text, '<Project\s+Path="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    }
    else {
        [regex]::Matches($text, 'Project\("[^"]+"\)\s*=\s*"[^"]*",\s*"([^"]+\.csproj)"') |
            ForEach-Object { $_.Groups[1].Value }
    }
    @($paths | ForEach-Object { [IO.Path]::GetFullPath($_, $directory) })
}

# 还原是否仍然有效：每个项目都已有 assets 文件，且它不比项目文件与共享输入更旧。
function Test-RestoreUpToDate([string[]] $Projects, [string] $NuGetConfig) {
    $inputs = @($NuGetConfig, (Join-Path $repo 'Directory.Build.props')) |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc }
    $inputTime = if ($inputs) { ($inputs | Measure-Object -Maximum).Maximum } else { $null }
    foreach ($project in $Projects) {
        $assets = Join-Path (Split-Path -Parent $project) 'obj/project.assets.json'
        if (-not (Test-Path -LiteralPath $assets -PathType Leaf)) { return $false }
        $assetsTime = (Get-Item -LiteralPath $assets).LastWriteTimeUtc
        if ($assetsTime -lt (Get-Item -LiteralPath $project).LastWriteTimeUtc) { return $false }
        if ($inputTime -and $assetsTime -lt $inputTime) { return $false }
    }
    return $true
}

Push-Location $repo
try {
    # 工具链：优先项目内 SDK（固定版本、离线包源），没有才退回系统 dotnet。
    $localSdk = Join-Path $repo '.runtime/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localSdk -PathType Leaf) {
        $dotnet = $localSdk
        $sdkDirectory = Join-Path $repo '.runtime/dotnet'
        $env:DOTNET_ROOT = $sdkDirectory
        $env:DOTNET_CLI_HOME = Join-Path $repo '.runtime/dotnet-home'
        $env:NUGET_PACKAGES = Join-Path $repo '.runtime/nuget/packages'
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.runtime/nuget/http-cache'
        $env:PATH = $sdkDirectory + [IO.Path]::PathSeparator + $env:PATH
        New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES,
            $env:NUGET_HTTP_CACHE_PATH | Out-Null
        $toolchain = '项目内工具链 .runtime/dotnet'
    }
    else {
        $command = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $command) {
            throw '找不到 dotnet。请安装 .NET 10 SDK，或先运行 tools/build_ui.ps1 -Bootstrap 准备项目内工具链。'
        }
        $dotnet = $command.Source
        $toolchain = '系统 dotnet'
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    # 增量构建依赖这两项：复用 MSBuild 节点、复用 Roslyn 编译器服务。
    $env:MSBUILDDISABLENODEREUSE = '0'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '1'

    $version = & $dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'dotnet --version 执行失败。' }
    if (-not $version.StartsWith('10.')) {
        throw "需要 .NET 10 SDK，当前是 $version（$toolchain）。"
    }

    $targets = [Collections.Generic.List[string]]::new()
    if ($Project) {
        $resolved = [IO.Path]::GetFullPath($Project, $repo)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "找不到项目文件：$Project" }
        $targets.Add($resolved)
    }
    else {
        $targets.Add((Join-Path $repo 'Alas.sln'))
        if ($Ui) { $targets.Add((Join-Path $repo 'Alas.UI.slnx')) }
    }
    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "找不到构建目标：$target" }
    }

    # 离线包源：根目录 NuGet.config 是本机配置（不入库）；缺失时不指定配置，交给 NuGet 默认行为。
    $nugetConfig = Join-Path $repo 'NuGet.config'
    if (-not (Test-Path -LiteralPath $nugetConfig -PathType Leaf)) { $nugetConfig = '' }

    Write-Output "工具链：$toolchain（SDK $version）"
    Write-Output "配置：$Configuration$(if ($Clean) { '，清理后重建' } else { '，增量' })"

    $restoreTime = [TimeSpan]::Zero
    $buildTime = [TimeSpan]::Zero
    foreach ($target in $targets) {
        $name = [IO.Path]::GetFileName($target)
        $projects = @(Get-TargetProjects $target)
        if ($projects.Count -eq 0) { throw "$name 里没有解析到任何项目。" }

        $needsRestore = $Restore -or $Clean -or -not (Test-RestoreUpToDate $projects $nugetConfig)
        if ($needsRestore) {
            $restoreArguments = @('restore', $target, '-p:NuGetAudit=false')
            if ($nugetConfig) { $restoreArguments += @('--configfile', $nugetConfig) }
            Write-Output "[$name] 还原 $($projects.Count) 个项目…"
            Invoke-Dotnet $restoreArguments
            $restoreTime += $script:lastElapsed
        }
        else {
            Write-Output "[$name] 还原已是最新，跳过（-Restore 可强制）。"
        }

        if ($Clean) {
            Write-Output "[$name] 清理…"
            Invoke-Dotnet @('clean', $target, '-c', $Configuration, '-v:q')
        }

        Write-Output "[$name] 构建…"
        Invoke-Dotnet @('build', $target, '-c', $Configuration, '--no-restore', '-v:m')
        $buildTime += $script:lastElapsed
    }

    Write-Output ('完成：还原 {0}，构建 {1}（{2} 个目标）' -f (Format-Elapsed $restoreTime),
        (Format-Elapsed $buildTime), $targets.Count)
    Write-Output '未改动的项目按 MSBuild 最新检查跳过；需要全量重建时用 -Clean。'
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    Pop-Location
}
