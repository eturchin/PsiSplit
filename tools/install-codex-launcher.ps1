[CmdletBinding()]
param([string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $root 'dist\codex-proxy' }
$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CodexProxy'
$target = Join-Path $install 'CodexProxy.exe'
$files = @('CodexProxy.exe', 'THIRD-PARTY-NOTICES.md', 'Psiphon-LICENSE.txt', 'README.md')
foreach ($name in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) {
        throw "Missing $name. Run publish-codex-launcher.ps1 first."
    }
}
foreach ($process in @(Get-Process -Name CodexProxy -ErrorAction SilentlyContinue)) {
    try {
        if ($process.Path -and [string]::Equals($process.Path, $target, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The installed launcher is running. No application was stopped or replaced.'
        }
    } finally { $process.Dispose() }
}
if (Test-Path -LiteralPath $target) {
    $probe = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $probe.Dispose()
}
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Codex (Proxy).lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex (Proxy).lnk')
)
$shell = New-Object -ComObject WScript.Shell
foreach ($path in $shortcutPaths) {
    if (Test-Path -LiteralPath $path) {
        $existing = $shell.CreateShortcut($path)
        if (-not [string]::Equals($existing.TargetPath, $target, [StringComparison]::OrdinalIgnoreCase) -or $existing.Arguments) {
            throw 'An unrelated shortcut already uses the name Codex (Proxy). No shortcut was replaced.'
        }
    }
}
New-Item -ItemType Directory -Force -Path $install | Out-Null
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $install $name) -Force }
$icon = $target
# Use an icon from the locally installed app, never store it in the repository.
$packages = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'WindowsApps'
$package = Get-ChildItem -LiteralPath $packages -Directory -Filter 'OpenAI.Codex_*_x64__2p2nqsd0c76g0' -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Name.Split('_')[1]) } -Descending | Select-Object -First 1
if ($package) {
    $sourceIcon = Join-Path $package.FullName 'app\resources\icon-chatgpt.ico'
    if (Test-Path -LiteralPath $sourceIcon) {
        $icon = Join-Path $install 'Codex.ico'
        Copy-Item -LiteralPath $sourceIcon -Destination $icon -Force
    }
}
foreach ($path in $shortcutPaths) {
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $install
    $shortcut.IconLocation = "$icon,0"
    $shortcut.Description = 'Start Codex with its own background proxy connection'
    $shortcut.Save()
}
Write-Host 'Installed Codex Proxy and added Codex (Proxy) shortcuts to Desktop and Start menu.'
Write-Host 'No application was launched or stopped. Windows proxy settings and the Codex profile were not changed.'
