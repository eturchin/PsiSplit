[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $problems = [Collections.Generic.List[string]]::new()
    $privateValues = [Collections.Generic.List[string]]::new()
    foreach ($value in @($env:USERPROFILE, $env:COMPUTERNAME, $root)) {
        if ($value -and $value.Length -gt 5) { $privateValues.Add($value) }
    }
    $configuredEmail = git config --global --get user.email 2>$null
    if ($configuredEmail -and $configuredEmail -notmatch '@users\.noreply\.github\.com$') {
        $privateValues.Add($configuredEmail)
    }
    $files = @(git -c core.quotepath=false ls-files --cached)
    if ($LASTEXITCODE -ne 0) { throw 'Run this check from an initialized Git checkout.' }
    if ($files.Count -eq 0) { throw 'No tracked/staged files to check.' }
    $rules = [ordered]@{
        'absolute Windows path' = '(?i)\b[A-Z]:[\\/]'
        'absolute user home path' = '(?<![\w/])/(?:Users|home|mnt|media)/[^\s]+'
        'GitHub credential' = 'gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}'
        'AWS access key' = '(?:AKIA|ASIA)[A-Z0-9]{16}'
        'private key material' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
    }
    $binaryExtensions = @('.exe', '.dll', '.png', '.jpg', '.jpeg', '.ico', '.dat')
    foreach ($relative in $files) {
        if ($relative -match '(^|/)(bin|obj|dist|artifacts|\.idea|\.vs|\.vscode|logs)(/|$)' -or
            $relative -match '(^|/)(config\.json|psiphon\.config|proxy-backup\.json|session\.json|\.env)(\.|$)') {
            $problems.Add("$relative : local/generated state is tracked")
        }
        if ($relative -match '(?i)\.(exe|dll)$' -and $relative -ne 'src/PsiSplit.Core/Resources/Psiphon/psiphon-tunnel-core.exe') {
            $problems.Add("$relative : unexpected binary; publish builds as release assets")
        }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $root $relative))
        $utf8 = [Text.Encoding]::UTF8.GetString($bytes)
        $isBinary = [IO.Path]::GetExtension($relative) -in $binaryExtensions
        foreach ($value in $privateValues) {
            $found = $utf8.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0
            if ($isBinary -and -not $found) {
                $found = [Text.Encoding]::Unicode.GetString($bytes).IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0
            }
            if ($found) { $problems.Add("$relative : local identity or checkout path found") }
        }
        if ($isBinary) { continue }
        foreach ($rule in $rules.GetEnumerator()) {
            if ($utf8 -match $rule.Value) { $problems.Add("$relative : $($rule.Key)") }
        }
    }
    if ($problems.Count -gt 0) {
        # Do not print the matched values: reports must not disclose secrets.
        $problems | ForEach-Object { Write-Host $_ }
        throw "Privacy check failed with $($problems.Count) finding(s)."
    }
    Write-Host "Privacy check passed: $($files.Count) tracked/staged files; no local paths, identities, credentials or runtime state found."
} finally {
    Pop-Location
}
