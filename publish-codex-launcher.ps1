[CmdletBinding()]
param(
    [string]$OutputDirectory = 'dist\codex-proxy',
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else { [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory)) }
$target = Join-Path $output 'CodexProxy.exe'
foreach ($process in @(Get-Process -Name CodexProxy -ErrorAction SilentlyContinue)) {
    try {
        if ($process.Path -and [string]::Equals($process.Path, $target, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'This launcher is running. Choose another output directory. No process was stopped.'
        }
    } finally { $process.Dispose() }
}
if (Test-Path -LiteralPath $target) {
    $probe = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $probe.Dispose()
}
$publish = Join-Path $root ('artifacts\publish\' + [Guid]::NewGuid().ToString('N'))
[string[]]$restoreArgs = @()
if ($NoRestore) { $restoreArgs += '--no-restore' }
dotnet publish (Join-Path $root 'src\PsiSplit.CodexLauncher\PsiSplit.CodexLauncher.csproj') @restoreArgs `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -p:UseSharedCompilation=false -p:NuGetAudit=false -nr:false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'CodexProxy.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'src\PsiSplit.Core\Resources\Psiphon\LICENSE.txt') -Destination (Join-Path $output 'Psiphon-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\codex-proxy.md') -Destination (Join-Path $output 'README.md') -Force
Write-Host "Published: $target"
Write-Host 'No existing application, connection or Windows proxy setting was changed.'
