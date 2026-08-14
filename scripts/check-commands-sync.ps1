# check-commands-sync.ps1 — fail the build if any dforge command is missing from docs/COMMANDS.md.
# Runs `dforge` (no args), extracts the command tokens from its help, and confirms each is documented.
# Wire into CI so the help block and the command reference cannot drift apart.
#   pwsh scripts/check-commands-sync.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dll  = Get-ChildItem -Path (Join-Path $repo 'src/DiscForge.Cli') -Recurse -Filter 'dforge.dll' |
        Where-Object { $_.FullName -match '[\\/]net8\.0[\\/]' } | Select-Object -First 1
if (-not $dll) { Write-Error 'Build the CLI first: dotnet build src/DiscForge.Cli -f net8.0'; exit 2 }

$help = & dotnet $dll.FullName 2>$null |
        Select-String -Pattern '^\s\s([a-z][a-z0-9-]+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Sort-Object -Unique
$docs = Select-String -Path (Join-Path $repo 'docs/COMMANDS.md') -Pattern '^\-\s`([a-z][a-z0-9-]+)' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique

$missing = $help | Where-Object { $docs -notcontains $_ }
if ($missing) {
    Write-Host 'COMMANDS.md is missing these commands shown in help:' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" }
    Write-Host 'Regenerate docs/COMMANDS.md from the CLI help.'
    exit 1
}
Write-Host "OK: every dforge command is documented in COMMANDS.md ($($help.Count) commands)." -ForegroundColor Green
