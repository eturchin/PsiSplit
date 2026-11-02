[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = 'dist\standalone',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\PsiSplit.App\PsiSplit.App.csproj'
$publishDir = Join-Path $root ('artifacts\publish\' + [Guid]::NewGuid().ToString('N'))
$distDir = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
$target = Join-Path $distDir 'PsiSplit.exe'

# Refuse to replace a live executable. Never stop/restart anything to publish.
foreach ($process in @(Get-Process -Name PsiSplit -ErrorAction SilentlyContinue)) {
    try {
        if ($process.Path -and [string]::Equals($process.Path, $target, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The output executable is running. Choose another -OutputDirectory. No process was stopped."
        }
    } finally { $process.Dispose() }
}
if (Test-Path -LiteralPath $target) {
    $probe = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $probe.Dispose()
}

[string[]]$restoreArgs = @()
if ($NoRestore) { $restoreArgs += '--no-restore' }
dotnet publish $project @restoreArgs `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:UseSharedCompilation=false `
    -p:NuGetAudit=false `
    -nr:false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Copy-Item -LiteralPath (Join-Path $publishDir 'PsiSplit.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $distDir -Force
Copy-Item -LiteralPath (Join-Path $root 'src\PsiSplit.Core\Resources\Psiphon\LICENSE.txt') -Destination (Join-Path $distDir 'Psiphon-LICENSE.txt') -Force

Write-Host "Published: $target"
Write-Host 'Existing sessions were not stopped. The original dist\PsiSplit.exe was not replaced by the default output.'
