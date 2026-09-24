#requires -Version 7.0
<# Checks local UI publication only; no browser, desktop UI or device is started. #>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$publishRoot = Join-Path $repo '.runtime/ui-publish'
$webRoot = Join-Path $publishRoot 'browser/wwwroot'
$privateRoots = @($repo, [Environment]::GetFolderPath('UserProfile')) |
    Where-Object { $_ } | ForEach-Object { $_; $_.Replace('\', '/'); $_.Replace('\', '\\') } |
    Select-Object -Unique

foreach ($relative in @('desktop-win-x64/Alas.UI.Desktop.exe', 'desktop-win-x64/Alas.UI.dll', 'browser/wwwroot/index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $relative) -PathType Leaf)) {
        throw "UI publication is incomplete: $relative"
    }
}

$findings = [Collections.Generic.List[string]]::new()
$count = 0
foreach ($file in Get-ChildItem -LiteralPath $publishRoot -Recurse -File) {
    $inputStream = [IO.File]::OpenRead($file.FullName)
    $decoded = $null
    $memory = [IO.MemoryStream]::new()
    try {
        $decoded = switch ($file.Extension) {
            '.gz' { [IO.Compression.GZipStream]::new($inputStream, [IO.Compression.CompressionMode]::Decompress) }
            '.br' { [IO.Compression.BrotliStream]::new($inputStream, [IO.Compression.CompressionMode]::Decompress) }
            default { $inputStream }
        }
        $decoded.CopyTo($memory)
        $bytes = $memory.ToArray()
    }
    finally {
        if ($decoded) { $decoded.Dispose() }
        $inputStream.Dispose()
        $memory.Dispose()
    }
    $count++
    $containsPrivatePath = $false
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $text = $encoding.GetString($bytes)
        # Match this builder's roots. Emscripten's virtual home directory and
        # public third-party package debug metadata are not local developer paths.
        foreach ($prefix in $privateRoots) {
            if ($text.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $containsPrivatePath = $true
                break
            }
        }
        if ($containsPrivatePath) { break }
    }
    if ($containsPrivatePath) { $findings.Add([IO.Path]::GetRelativePath($publishRoot, $file.FullName)) }
}
if ($findings.Count) {
    # Report filenames only, never the matching personal path or binary context.
    $findings | ForEach-Object { Write-Output "Developer path in UI artifact: $_" }
    throw "UI publication contains $($findings.Count) artifacts with developer paths."
}
if (Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter 'Alas*.pdb') {
    throw 'UI publication contains project debug symbols.'
}
if (Get-ChildItem -LiteralPath $webRoot -Recurse -File | Where-Object { $_.Name -match '^(Alas\.Core|Python\.Runtime)' }) {
    throw 'Browser publication unexpectedly contains automation runtime assemblies.'
}

$html = Get-Content -LiteralPath (Join-Path $webRoot 'index.html') -Raw
if ($html.Contains('#[.')) { throw 'Unresolved static asset fingerprint in browser entry point.' }
$importMap = [regex]::Match($html, '<script type="importmap">(.*?)</script>', 'Singleline')
if (-not $importMap.Success) { throw 'Browser import map is missing.' }
$imports = (ConvertFrom-Json $importMap.Groups[1].Value).imports.PSObject.Properties.Value
$references = @($imports) + @([regex]::Matches($html, '(?:src|href)="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath (Join-Path $webRoot $reference) -PathType Leaf)) {
        throw 'Browser entry point references a missing local asset.'
    }
}
Write-Output "PASS: $count UI artifacts (including decompressed assets) contain no local developer paths; browser entry assets exist."
