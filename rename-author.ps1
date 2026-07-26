# DiscForge - rename the author / copyright holder across the whole repo.
# Replaces the exact word "Andy" with "MaTRIX TeAm" in all text sources.
#
# Robust version: a plain loop (no fragile pipeline), per-file try/catch, and it
# prints how many files it scanned so you can see it actually recursed.
#
# Usage (from C:\dev\DiscForge):  .\rename-author.ps1   then   .\build.ps1

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

$old  = 'A' + 'ndy'          # split so this script never rewrites its own literal
$new  = 'MaTRIX TeAm'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$exts = @('.cs', '.csproj', '.sln', '.md', '.iss', '.ps1', '.txt', '.json', '.editorconfig')
$self = $MyInvocation.MyCommand.Path

$all = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue)
Write-Host "Scanned $($all.Count) files under $root" -ForegroundColor Cyan

$candidates = 0
$changed = 0
foreach ($f in $all) {
    $p = $f.FullName
    if ($p -eq $self) { continue }
    if ($p -like '*\bin\*' -or $p -like '*\obj\*' -or $p -like '*\dist\*' -or $p -like '*\.git\*') { continue }
    if (-not (($exts -contains $f.Extension.ToLower()) -or ($f.Name -eq 'LICENSE'))) { continue }

    $candidates++
    try {
        $text = [System.IO.File]::ReadAllText($p)
        if ($text.Contains($old)) {
            [System.IO.File]::WriteAllText($p, $text.Replace($old, $new), $utf8)
            $changed++
            Write-Host ("  " + $p.Substring($root.Length + 1))
        }
    }
    catch {
        Write-Host ("  (skipped " + $f.Name + ": " + $_.Exception.Message + ")") -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Text candidates checked: $candidates   Renamed: $changed" -ForegroundColor Green
if ($candidates -lt 50) {
    Write-Host "Only $candidates candidates were found - the recursion did not see the source tree." -ForegroundColor Yellow
    Write-Host "Run this from C:\dev\DiscForge, or try:  pwsh -File .\rename-author.ps1" -ForegroundColor Yellow
} else {
    Write-Host "Now rebuild:  .\build.ps1 -Run" -ForegroundColor Cyan
}
