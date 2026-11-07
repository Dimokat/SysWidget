# Writes copies of SysWidget.cs and Setup.cs into obj\ with the version filled in, so every build
# reports its real version. Version comes from $env:VERSION (the release workflow sets it from the tag),
# otherwise from the latest git tag; if neither is available the sources are used as they are.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$v = $env:VERSION
if (-not $v) { try { $v = git -C $root describe --tags --abbrev=0 2>$null } catch { $v = '' } }
$v = "$v".Trim().TrimStart('v')
$ok = $v -match '^\d+\.\d+\.\d+$'
New-Item "$root\obj" -ItemType Directory -Force | Out-Null
foreach ($f in 'SysWidget.cs', 'Setup.cs') {
    $s = [IO.File]::ReadAllText("$root\$f")
    if ($ok) {
        $s = $s -replace 'AssemblyVersion\("[^"]*"\)', "AssemblyVersion(""$v.0"")"
        $s = $s -replace 'AssemblyFileVersion\("[^"]*"\)', "AssemblyFileVersion(""$v.0"")"
        if ($f -eq 'Setup.cs') { $s = $s -replace 'Version = "[^"]*"', "Version = ""$v""" }
    }
    [IO.File]::WriteAllText("$root\obj\$f", $s, (New-Object Text.UTF8Encoding($true)))
}
# Every translation in lang\ is built into both programs as a resource named lang.<code>.txt.
$rsp = @(Get-ChildItem "$root\lang" -Filter *.txt | Sort-Object Name | ForEach-Object { "/resource:lang\$($_.Name),lang.$($_.Name)" })
[IO.File]::WriteAllLines("$root\obj\lang.rsp", [string[]]$rsp)
if ($ok) { "Version $v" } else { 'Version: as in the sources (no git tag found)' }