[CmdletBinding()]
param(
    [string]$CorePath = "$env:LOCALAPPDATA\Temp\psiphon-tunnel-core.exe",
    [string]$ConfigPath = "$env:LOCALAPPDATA\Psiphon3\psiphon.config",
    [string]$ServerListPath = "$env:LOCALAPPDATA\Psiphon3\server_list.dat"
)

# Read-only import from an existing official installation. Never runs the GUI,
# modifies its files/registry, or copies its datastore or account authorizations.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root 'src\PsiSplit.Core\Resources\Psiphon'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$sourceConfig = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$bootstrap = [ordered]@{}
$publicFields = @(
    'ClientVersion', 'PropagationChannelId', 'SponsorId',
    'RemoteServerListSignaturePublicKey', 'RemoteServerListURLs',
    'ObfuscatedServerListRootURLs', 'ServerEntrySignaturePublicKey',
    'UseIndistinguishableTLS', 'EgressRegion'
)
foreach ($name in $publicFields) {
    if ($null -ne $sourceConfig.$name) { $bootstrap[$name] = $sourceConfig.$name }
}
$bootstrap['ClientPlatform'] = 'Windows'
foreach ($name in @('ClientVersion', 'PropagationChannelId', 'SponsorId', 'RemoteServerListSignaturePublicKey')) {
    if ([string]::IsNullOrWhiteSpace($bootstrap[$name])) { throw "Missing public bootstrap field: $name" }
}

$coreDestination = Join-Path $destination 'psiphon-tunnel-core.exe'
if ([IO.Path]::GetFullPath($CorePath) -ne [IO.Path]::GetFullPath($coreDestination)) {
    Copy-Item -LiteralPath $CorePath -Destination $coreDestination -Force
}
$serverDestination = Join-Path $destination 'server_list.dat'
if ([IO.Path]::GetFullPath($ServerListPath) -ne [IO.Path]::GetFullPath($serverDestination)) {
    Copy-Item -LiteralPath $ServerListPath -Destination $serverDestination -Force
}
$bootstrap | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $destination 'bootstrap.json') -Encoding utf8

# --version exits immediately: it does not start a tunnel or change settings.
$version = (& $coreDestination --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read tunnel core version.' }
$hashes = [ordered]@{}
foreach ($file in @('psiphon-tunnel-core.exe', 'server_list.dat', 'bootstrap.json', 'LICENSE.txt')) {
    $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $destination $file) -Algorithm SHA256).Hash
}
[ordered]@{
    version = $version
    source = 'https://github.com/Psiphon-Labs/psiphon-tunnel-core'
    files = $hashes
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Host "Imported public bootstrap and tunnel core into $destination"
