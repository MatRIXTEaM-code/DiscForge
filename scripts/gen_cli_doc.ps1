# DiscForge - regenerate docs\CLI.md from the tool's own help output, so the command
# reference can never go stale. Builds nothing; it just runs the already-built dforge.
#
# Usage:  .\scripts\gen_cli_doc.ps1
#         .\scripts\gen_cli_doc.ps1 -Check   # non-zero exit if docs\CLI.md is stale

param([switch]$Check)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root "docs\CLI.md"

# Find a built dforge (Release preferred), else fall back to one on PATH.
$dll = Join-Path $root "src\DiscForge.Cli\bin\Release\net8.0\dforge.dll"
if (Test-Path $dll) { $help = & dotnet $dll 2>$null }
elseif (Get-Command dforge -ErrorAction SilentlyContinue) { $help = & dforge 2>$null }
else { Write-Host "No built dforge found - run .\build.ps1 first." -ForegroundColor Yellow; exit 1 }

$lines = @($help)
$start = ($lines | Select-String -SimpleMatch "commands:" | Select-Object -First 1).LineNumber
$cmds  = if ($start) { $lines[$start..($lines.Count - 1)] } else { $lines }
$ncmd  = ($cmds | Where-Object { $_ -match '^  [a-z0-9]' }).Count

$doc = New-Object System.Collections.Generic.List[string]
$doc.Add("<!-- GENERATED FILE - regenerate with:  scripts\gen_cli_doc.ps1  (captures 'dforge' help) -->")
$doc.Add("")
$doc.Add("# DiscForge - ``dforge`` command reference")
$doc.Add("")
$doc.Add("``dforge`` is the cross-platform command-line tool (Core builds and runs anywhere .NET 8")
$doc.Add("does). It exposes the same Core engine as the GUI. This reference is generated verbatim")
$doc.Add("from the tool's own help output, so it never drifts: **$ncmd commands**.")
$doc.Add("")
$doc.Add("Run ``dforge`` with no arguments to print this list, or ``dforge <command>`` with no further")
$doc.Add("arguments to see that command's usage.")
$doc.Add("")
$doc.Add('```')
foreach ($l in $cmds) { $doc.Add(($l -replace '\s+$', '')) }
$doc.Add('```')

$text = ($doc -join "`n").TrimEnd() + "`n"

if ($Check) {
    $current = if (Test-Path $out) { [System.IO.File]::ReadAllText($out) } else { "" }
    if ($current.TrimEnd() -ne $text.TrimEnd()) { Write-Host "docs\CLI.md is stale - run: .\scripts\gen_cli_doc.ps1" -ForegroundColor Yellow; exit 1 }
    Write-Host "docs\CLI.md is up to date."; exit 0
}

[System.IO.File]::WriteAllText($out, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote docs\CLI.md: $ncmd commands."
