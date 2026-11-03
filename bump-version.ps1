# Imposta la versione in Version.cs.
#   bump-version.ps1 1.2.0    -> versione esplicita
#   bump-version.ps1 patch    -> 1.0.0 -> 1.0.1   (anche: minor, major)
#   bump-version.ps1 show     -> stampa la versione attuale
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$file = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Version.cs'
$src = [IO.File]::ReadAllText($file)
$cur = [version]([regex]::Match($src, 'Text = "(\d+\.\d+\.\d+)"').Groups[1].Value)

if ($Version -eq 'show') { Write-Output "$cur"; return }

switch ($Version) {
    'major' { $new = "{0}.0.0" -f ($cur.Major + 1) }
    'minor' { $new = "{0}.{1}.0" -f $cur.Major, ($cur.Minor + 1) }
    'patch' { $new = "{0}.{1}.{2}" -f $cur.Major, $cur.Minor, ($cur.Build + 1) }
    default {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida: '$Version' (usa X.Y.Z, patch, minor o major)" }
        $new = $Version
    }
}
if ([version]$new -lt $cur) { Write-Warning "La nuova versione $new è più vecchia di quella attuale $cur" }

$src = [regex]::Replace($src, 'Version\("\d+\.\d+\.\d+\.\d+"\)', "Version(`"$new.0`")")
$src = [regex]::Replace($src, 'Text = "\d+\.\d+\.\d+"', "Text = `"$new`"")
[IO.File]::WriteAllText($file, $src, (New-Object Text.UTF8Encoding $false))
Write-Output "Versione: $cur -> $new"
