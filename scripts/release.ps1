<#
  Liest <Version> aus NetScanner.csproj, erstellt das Tag vX.Y.Z und pusht es.
  Der Tag-Push triggert die GitHub-Action (release.yml) -> Build + Release.

  Aufruf:  powershell -ExecutionPolicy Bypass -File scripts\release.ps1
           ... -Yes    (ohne Rueckfragen)
#>
param([switch]$Yes)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$csproj = 'NetScanner.csproj'

function Confirm-Step($prompt, $default = 'N') {
  if ($Yes) { return $true }
  $hint = if ($default -eq 'Y') { '[Y/n]' } else { '[y/N]' }
  $a = Read-Host "$prompt $hint"
  if ([string]::IsNullOrWhiteSpace($a)) { $a = $default }
  return $a -match '^[yY]'
}

# 1) Version aus dem <Version>-Element.
$xml = [xml](Get-Content $csproj)
$version = @($xml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { Write-Error "Keine <Version> in $csproj gefunden."; exit 1 }
$version = "$version".Trim()
$tag = "v$version"
Write-Host "Version aus ${csproj}:  $version   ->   Tag $tag"

# 2) Uncommittete Aenderungen?
if (git status --porcelain) {
  Write-Host "Achtung: es gibt uncommittete Aenderungen:"
  git status --short
  if (-not (Confirm-Step "Trotzdem fortfahren?" 'N')) { Write-Host "Abgebrochen."; exit 1 }
}

# 3) Nicht gepushte Commits? -> erst Branch pushen.
git rev-parse '@{u}' 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
  $ahead = git log '@{u}..HEAD' --oneline
  if ($ahead) {
    Write-Host "Es gibt lokale Commits, die noch nicht gepusht sind."
    if (Confirm-Step "Erst 'git push' ausfuehren?" 'Y') { git push }
  }
}

# 4) Tag vorhanden? -> auf Wunsch neu setzen.
git rev-parse $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
  Write-Host "Tag $tag existiert bereits."
  if (-not (Confirm-Step "Altes Tag (lokal + remote) loeschen und neu auf HEAD setzen?" 'N')) {
    Write-Host "Abgebrochen — Version in $csproj erhoehen oder Tag manuell pflegen."; exit 1
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
