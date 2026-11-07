# Checks the translation files in lang\ against lang\en.txt.
# Runs on every pull request that touches lang\ (the "Translations" workflow) and before each release.
# Errors fail the check: unknown or duplicate keys, empty texts, placeholders English doesn't have,
# broken { } braces, file names that aren't language codes. Missing keys are only listed: the widget
# shows those lines in English.
param([string]$Dir = (Join-Path (Join-Path $PSScriptRoot '..') 'lang'))
$ErrorActionPreference = 'Stop'

function Read-Lang([string]$path) {
    $map = [ordered]@{}; $errs = @(); $n = 0
    foreach ($line in [IO.File]::ReadAllLines($path, [Text.Encoding]::UTF8)) {
        $n++; $t = $line.Trim().TrimStart([char]0xFEFF)
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -le 0) { $errs += "line ${n}: expected 'key = text'"; continue }
        $k = $t.Substring(0, $eq).Trim(); $v = $t.Substring($eq + 1).Trim()
        if ($k -notmatch '^[a-z0-9_.]+$') { $errs += "line ${n}: '$k' is not a valid key"; continue }
        if ($map.Contains($k)) { $errs += "line ${n}: '$k' appears twice" }
        if ($v -eq '') { $errs += "line ${n}: '$k' has no text" }
        if ($v.Length -gt 400) { $errs += "line ${n}: '$k' is longer than 400 characters" }
        $map[$k] = $v
    }
    @{ Map = $map; Errors = $errs }
}

function Get-Holes([string]$s) {
    @([regex]::Matches($s, '\{(\d+)[^}]*\}') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
}

$en = Read-Lang (Join-Path $Dir 'en.txt')
if ($en.Errors.Count) { $en.Errors | ForEach-Object { "en.txt: $_" }; exit 1 }
$cultures = @([Globalization.CultureInfo]::GetCultures([Globalization.CultureTypes]::AllCultures) | ForEach-Object Name)
$failed = $false

foreach ($f in Get-ChildItem $Dir -Filter *.txt | Where-Object Name -ne 'en.txt' | Sort-Object Name) {
    $code = $f.BaseName; $errs = @()
    if ($cultures -cnotcontains $code) { $errs += "'$code' is not a language code Windows knows (examples: uk, pt-BR, zh-Hans)" }
    $r = Read-Lang $f.FullName; $errs += $r.Errors
    foreach ($k in $r.Map.Keys) {
        if (-not $en.Map.Contains($k)) { $errs += "'$k' is not a key in en.txt"; continue }
        $allowed = Get-Holes $en.Map[$k]; $v = $r.Map[$k].Replace('\n', "`n")
        foreach ($h in (Get-Holes $v)) { if ($allowed -notcontains $h) { $errs += "'$k' uses {$h}, which the English text doesn't have" } }
        $max = ($allowed | Measure-Object -Maximum).Maximum
        $count = if ($null -ne $max) { [int]$max + 1 } else { 1 }
        try { [void][string]::Format([Globalization.CultureInfo]::InvariantCulture, $v, [object[]]::new($count)) }
        catch { $errs += "'$k' has unmatched { } braces" }
    }
    $missing = @($en.Map.Keys | Where-Object { -not $r.Map.Contains($_) })
    $done = $r.Map.Count - @($r.Map.Keys | Where-Object { -not $en.Map.Contains($_) }).Count
    $state = if ($errs.Count) { 'FAIL' } else { 'ok' }
    $note = if ($missing.Count) { "; not translated yet: " + ($missing -join ', ') } else { '' }
    "{0,-8} {1,-4} {2}/{3}{4}" -f $code, $state, $done, $en.Map.Count, $note
    $errs | ForEach-Object { "           $_" }
    if ($errs.Count) { $failed = $true }
}
if ($failed) { exit 1 }
