#!/usr/bin/env bash
#
# Liest <Version> aus NetScanner.csproj, erstellt das Tag vX.Y.Z und pusht es.
# Der Tag-Push triggert die GitHub-Action (release.yml) -> Build + Release.
#
# Aufruf:  bash scripts/release.sh          (interaktiv, mit Sicherheitsfragen)
#          bash scripts/release.sh --yes     (ohne Rueckfragen, fuer ganz Faule)
#
set -euo pipefail

# Immer aus dem Projekt-Root arbeiten, egal von wo aufgerufen.
cd "$(dirname "$0")/.."

CSPROJ="NetScanner.csproj"
AUTO=0
[[ "${1:-}" == "--yes" || "${1:-}" == "-y" ]] && AUTO=1

ask() {  # ask "Frage" "Default(Y/N)"  -> 0 = ja
  local prompt="$1" def="${2:-N}"
  [[ "$AUTO" == 1 ]] && return 0
  local hint="[y/N]"; [[ "$def" == "Y" ]] && hint="[Y/n]"
  read -rp "$prompt $hint " a
  a="${a:-$def}"
  [[ "$a" == [yY] ]]
}

# 1) Version aus dem <Version>-Element ziehen (PackageReference-Attribute matchen nicht).
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -1 | tr -d '[:space:]')"
if [[ -z "$VERSION" ]]; then
  echo "FEHLER: keine <Version> in $CSPROJ gefunden." >&2
  exit 1
fi
TAG="v$VERSION"
echo "Version aus $CSPROJ:  $VERSION   ->   Tag $TAG"

# 2) Uncommittete Aenderungen? (Tag wuerde auf den letzten Commit zeigen, nicht auf sie.)
if [[ -n "$(git status --porcelain)" ]]; then
  echo "Achtung: es gibt uncommittete Aenderungen:"
  git status --short
  ask "Trotzdem fortfahren?" "N" || { echo "Abgebrochen."; exit 1; }
fi

# 3) Noch nicht gepushte Commits? Dann zuerst den Branch pushen,
#    sonst kennt GitHub den getaggten Commit evtl. nicht.
if git rev-parse '@{u}' >/dev/null 2>&1; then
  if [[ -n "$(git log '@{u}..HEAD' --oneline)" ]]; then
    echo "Es gibt lokale Commits, die noch nicht gepusht sind."
    if ask "Erst 'git push' ausfuehren?" "Y"; then git push; fi
  fi
fi

# 4) Tag schon vorhanden? -> auf Wunsch loeschen und neu auf HEAD setzen.
if git rev-parse "$TAG" >/dev/null 2>&1; then
  echo "Tag $TAG existiert bereits."
  ask "Altes Tag (lokal + remote) loeschen und neu auf HEAD setzen?" "N" \
    || { echo "Abgebrochen — Version in $CSPROJ erhoehen oder Tag manuell pflegen."; exit 1; }
  git tag -d "$TAG"
  git push origin ":refs/tags/$TAG" 2>/dev/null || true   # remote ggf. nicht vorhanden -> egal
fi

# 5) Annotiertes Tag setzen und pushen.
git tag -a "$TAG" -m "Release $TAG"
git push origin "$TAG"

echo ""
echo "OK: Tag $TAG gepusht. Die GitHub-Action baut jetzt das Release."

# Bequemer Actions-Link (best effort aus der Remote-URL).
REMOTE="$(git remote get-url origin 2>/dev/null || true)"
SLUG="$(printf '%s' "$REMOTE" | sed -E 's#(git@github.com:|https://github.com/)##; s#\.git$##')"
[[ -n "$SLUG" ]] && echo "     https://github.com/$SLUG/actions"
