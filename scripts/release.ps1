<#
  Erstellt das Tag vX.Y.Z und pusht es. Der Tag-Push triggert die GitHub-Action
  (release.yml) -> Build + Release.

  Versionsquelle ist MinVer: der letzte Tag wird gelesen, der Patch-Stand
  vorgeschlagen. Es gibt bewusst KEINE <Version> mehr in der csproj — der Tag
  ist die einzige Versionsquelle.

  Aufruf:  pwsh -ExecutionPolicy Bypass -File scripts\release.ps1
           ... -Yes            (Patch-Bump ohne Rückfragen)
           ... -Version 1.6.0  (Version explizit vorgeben)
           läuft auch unter Windows PowerShell 5.1

  Diese Datei ist UTF-8 MIT BOM gespeichert. Ohne BOM liest Windows
  PowerShell 5.1 sie als ANSI und macht aus "ü" ein "³".
#>
param([switch]$Yes, [string]$Version)

$ErrorActionPreference = 'Stop'
# Externe Kommandos (git) dürfen mit Exit-Code != 0 zurückkehren, ohne abzubrechen —
# wir prüfen $LASTEXITCODE selbst (z. B. "Tag existiert noch nicht" ist der Normalfall).
# Ohne das würde PowerShell 7.4+ bei 'Stop' auch native Kommandos als Fehler werfen.
$PSNativeCommandUseErrorActionPreference = $false
Set-Location (Join-Path $PSScriptRoot '..')

function Confirm-Step($prompt, $default = 'N') {
  if ($Yes) { return $true }
  $hint = if ($default -eq 'Y') { '[Y/n]' } else { '[y/N]' }
  $a = Read-Host "$prompt $hint"
  if ([string]::IsNullOrWhiteSpace($a)) { $a = $default }
  return $a -match '^[yY]'
}

# 1) Version bestimmen: letzter Tag + Patch-Bump als Vorschlag.
if (-not $Version) {
  $last = git describe --tags --abbrev=0 --match 'v*' 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $last) { $last = 'v0.0.0' }
  $parts = ($last -replace '^v', '') -split '\.'
  $suggest = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
  if ($Yes) {
    $Version = $suggest
  } else {
    Write-Host "Letzter Tag: $last"
    $Version = Read-Host "Neue Version [$suggest]"
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $suggest }
  }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  Write-Error "'$Version' ist keine gültige SemVer-Version (X.Y.Z)."
  exit 1
}
$tag = "v$Version"
Write-Host "Release-Tag: $tag"

# 2) Uncommittete Änderungen?
if (git status --porcelain) {
  Write-Host "Achtung: es gibt uncommittete Änderungen:"
  git status --short
  if (-not (Confirm-Step "Trotzdem fortfahren?" 'N')) { Write-Host "Abgebrochen."; exit 1 }
}

# 3) Nicht gepushte Commits? -> erst Branch pushen.
git rev-parse '@{u}' 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
  $ahead = git log '@{u}..HEAD' --oneline
  if ($ahead) {
    Write-Host "Es gibt lokale Commits, die noch nicht gepusht sind."
    if (Confirm-Step "Erst 'git push' ausführen?" 'Y') { git push }
  }
}

# 4) Tag vorhanden? -> auf Wunsch neu setzen.
git rev-parse $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
  Write-Host "Tag $tag existiert bereits."
  if (-not (Confirm-Step "Altes Tag (lokal + remote) löschen und neu auf HEAD setzen?" 'N')) {
    Write-Host "Abgebrochen - höhere Version wählen oder Tag manuell pflegen."; exit 1
  }
  git tag -d $tag
  git push origin ":refs/tags/$tag" 2>$null
}

# 5) Tag setzen und pushen.
git tag -a $tag -m "Release $tag"
git push origin $tag

Write-Host ""
Write-Host "OK: Tag $tag gepusht. Die GitHub-Action baut jetzt das Release."

$remote = git remote get-url origin 2>$null
if ($remote) {
  $slug = $remote -replace '(git@github\.com:|https://github\.com/)', '' -replace '\.git$', ''
  Write-Host "     https://github.com/$slug/actions"
}
