# Builds SysWidget and runs its checks: the translation files (tools\check-lang.ps1) and the self-test
# (SysWidget.exe --selftest), which goes through every mode, look and language off screen and writes
# obj\selftest.txt. Safe to run while SysWidget is running. Exit code 0 = everything passed.
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    if (-not $NoBuild) { cmd /c build.bat | Out-Host; if ($LASTEXITCODE) { throw 'build failed' } }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-lang.ps1') | Out-Null
    $lang = $LASTEXITCODE
    $report = Join-Path $root 'obj\selftest.txt'
    New-Item (Join-Path $root 'obj') -ItemType Directory -Force | Out-Null
    $p = Start-Process (Join-Path $root 'SysWidget.exe') -ArgumentList '--selftest', "`"$report`"" -Wait -PassThru
    if (Test-Path $report) { Get-Content $report -Encoding UTF8 }
    if ($lang -or $p.ExitCode) { "FAILED (translations: exit $lang, self-test: $($p.ExitCode) failed checks)"; exit 1 }
    'All checks passed.'
} finally { Pop-Location }
