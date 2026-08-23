#!/usr/bin/env bash
#
# Erstellt das Tag vX.Y.Z und pusht es. Der Tag-Push triggert die GitHub-Action
# (release.yml) -> Build + Release.
#
# Versionsquelle ist MinVer: der letzte Tag wird gelesen, der Patch-Stand
# vorgeschlagen. Es gibt bewusst KEINE <Version> mehr in der csproj — der Tag
# ist die einzige Versionsquelle, damit Assembly-Version und Release-Name nicht
# auseinanderlaufen können.
#
# Aufruf:  bash scripts/release.sh               (interaktiv)
#          bash scripts/release.sh --yes         (Patch-Bump ohne Rückfragen)
#          bash scripts/release.sh 1.6.0         (Version explizit vorgeben)
#
set -euo pipefail

# Immer aus dem Projekt-Root arbeiten, egal von wo aufgerufen.
cd "$(dirname "$0")/.."

AUTO=0
VERSION=""
for arg in "$@"; do
  case "$arg" in
    --yes|-y) AUTO=1 ;;
    *)        VERSION="$arg" ;;
  esac
done

ask() {  # ask "Frage" "Default(Y/N)"  -> 0 = ja
  local prompt="$1" def="${2:-N}"
  [[ "$AUTO" == 1 ]] && return 0
  local hint="[y/N]"; [[ "$def" == "Y" ]] && hint="[Y/n]"
  read -rp "$prompt $hint " a
  a="${a:-$def}"
  [[ "$a" == [yY] ]]
}

# 1) Version bestimmen: letzter Tag + Patch-Bump als Vorschlag.
if [[ -z "$VERSION" ]]; then
  LAST="$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || echo v0.0.0)"
  LAST="${LAST#v}"
  IFS=. read -r MA MI PA <<< "$LAST"
  SUGGEST="${MA}.${MI}.$((PA + 1))"
  if [[ "$AUTO" == 1 ]]; then
    VERSION="$SUGGEST"
  else
    echo "Letzter Tag: v${LAST}"
    read -rp "Neue Version [${SUGGEST}]: " VERSION
    VERSION="${VERSION:-$SUGGEST}"
  fi
fi

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "FEHLER: '$VERSION' ist keine gültige SemVer-Version (X.Y.Z)." >&2
  exit 1
fi
TAG="v$VERSION"
echo "Release-Tag: $TAG"

# 2) Uncommittete Änderungen? (Tag würde auf den letzten Commit zeigen, nicht auf sie.)
if [[ -n "$(git status --porcelain)" ]]; then
  echo "Achtung: es gibt uncommittete Änderungen:"
  git status --short
  ask "Trotzdem fortfahren?" "N" || { echo "Abgebrochen."; exit 1; }
fi

# 3) Noch nicht gepushte Commits? Dann zuerst den Branch pushen,
#    sonst kennt GitHub den getaggten Commit evtl. nicht.
if git rev-parse '@{u}' >/dev/null 2>&1; then
  if [[ -n "$(git log '@{u}..HEAD' --oneline)" ]]; then
    echo "Es gibt lokale Commits, die noch nicht gepusht sind."
    if ask "Erst 'git push' ausführen?" "Y"; then git push; fi
  fi
fi

# 4) Tag schon vorhanden? -> auf Wunsch löschen und neu auf HEAD setzen.
if git rev-parse "$TAG" >/dev/null 2>&1; then
  echo "Tag $TAG existiert bereits."
  ask "Altes Tag (lokal + remote) löschen und neu auf HEAD setzen?" "N" \
    || { echo "Abgebrochen - höhere Version wählen oder Tag manuell pflegen."; exit 1; }
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
