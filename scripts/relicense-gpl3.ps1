# DiscForge — relicense the source headers from the old proprietary notice to GPL-3.0-or-later.
# Run once from the repository root, by the copyright holder:
#   powershell -ExecutionPolicy Bypass -File scripts\relicense-gpl3.ps1
#
# v2: reads and writes EXPLICIT UTF-8. (Windows PowerShell 5.1 decodes BOM-less files as the ANSI
# codepage, which mangles the em-dash in the header and made v1 match nothing while reporting
# success.) Each file's BOM state is preserved, and the script now FAILS LOUDLY if headers remain.

$ErrorActionPreference = 'Stop'

if (-not (Test-Path 'LICENSE') -or -not (Select-String -Path 'LICENSE' -Pattern 'GNU GENERAL PUBLIC LICENSE' -Quiet)) {
    Write-Error 'LICENSE at the repo root is not the GPL-3.0 text — commit the new LICENSE first.'
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$emDash = [char]0x2014

$old = @(
    "// DiscForge $emDash proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.",
    '// Not open source. No permission is granted to copy, fork or redistribute.',
    '// See LICENSE at the root of this repository.'
)
$new = @(
    "// DiscForge $emDash Copyright (C) 2026 MaTRIX TeAm.",
    '// SPDX-License-Identifier: GPL-3.0-or-later',
    '// This program is free software: you can redistribute it and/or modify it under the terms of the',
    '// GNU General Public License as published by the Free Software Foundation, either version 3 of',
    '// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;',
    '// see the GNU General Public License (LICENSE at the repository root) for details.'
)

$esc = $old | ForEach-Object { [regex]::Escape($_) }
$pattern = ($esc -join '\r?\n') + '\r?\n'

$changed = 0; $untouched = 0; $stillProprietary = @()
Get-ChildItem -Recurse -Filter *.cs -Path src, tests, tools |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.FullName -notmatch '\\discforge_sync\\' } |
    ForEach-Object {
        $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = $utf8NoBom.GetString($(if ($hasBom) { $bytes[3..($bytes.Length-1)] } else { $bytes }))

        if ($text -match $pattern) {
            $eol = if ($text -match "`r`n") { "`r`n" } else { "`n" }
            $replacement = (($new -join $eol) + $eol)
            # Instance Replace(input, replacement, count) — the static 4-arg form would treat the
            # count as RegexOptions, not a count.
            $rx = New-Object regex $pattern
            $text = $rx.Replace($text, $replacement.Replace('$', '$$'), 1)
            $enc = if ($hasBom) { $utf8Bom } else { $utf8NoBom }
            [System.IO.File]::WriteAllText($_.FullName, $text, $enc)
            $changed++
        } else {
            $untouched++
            if ($text.Contains('proprietary. Copyright (c) 2026 MaTRIX TeAm')) {
                $stillProprietary += $_.FullName
            }
        }
    }

Write-Host "Relicensed $changed file(s); $untouched had no proprietary header (already converted or new)."

if ($stillProprietary.Count -gt 0) {
    Write-Host ''
    Write-Host "ERROR: $($stillProprietary.Count) file(s) STILL contain the proprietary notice but did not match the" -ForegroundColor Red
    Write-Host '       expected 3-line header (edited header? unexpected encoding?). First few:' -ForegroundColor Red
    $stillProprietary | Select-Object -First 5 | ForEach-Object { Write-Host "       $_" -ForegroundColor Red }
    exit 1
}

if ($changed -eq 0) {
    Write-Host 'Nothing changed and no proprietary notices remain — the tree is already fully relicensed.'
} else {
    Write-Host 'Review with: git diff --stat   then commit.'
}
