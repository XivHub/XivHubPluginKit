#!/usr/bin/env bash
# Flags the theme mistakes the kit helpers exist to prevent: coloured text that never wraps
# (UI/HubText.cs), a table with a pixel column width or built without UI/HubTable.cs, a tab bar
# that can truncate instead of scroll (UI/HubTabs.cs), a raw style push outside the theme itself,
# and the ellipsis glyph the game font draws as three centred dots (UI/THEME.md, "Text"). Run
# before every commit that touches UI code.
#
# Usage: tools/theme-lint.sh <plugin dir>
# Prints "file:line: rule" for each hit and exits 1 if anything was found.
set -euo pipefail

dir="${1:?usage: theme-lint.sh <plugin dir>}"
dir="${dir%/}"

if [[ ! -d "$dir" ]]; then
  echo "theme-lint.sh: no such directory: $dir" >&2
  exit 2
fi

files=()
while IFS= read -r -d '' f; do
  files+=("$f")
done < <(find "$dir" \( -path '*/bin/*' -o -path '*/obj/*' \) -prune -o -type f -name '*.cs' -print0)

hits=0
text_colored=0
table_width=0
begin_table=0
begin_tabbar=0
push_style_color=0
ellipsis=0
minus=0

for f in "${files[@]}"; do
  while IFS= read -r line; do
    echo "$f:$line: ImGui.TextColored(HubStyle. doesn't wrap; use HubText"
    text_colored=$((text_colored + 1)); hits=$((hits + 1))
  done < <(grep -n 'ImGui\.TextColored(HubStyle\.' "$f" | cut -d: -f1)

  while IFS= read -r line; do
    echo "$f:$line: TableSetupColumn( with a pixel width; use HubTable.Fit/Stretch"
    table_width=$((table_width + 1)); hits=$((hits + 1))
  done < <(grep -noE 'TableSetupColumn\([^)]*WidthFixed[^)]*,\s*[0-9]+(\.[0-9]+)?\s*\)' "$f" \
             | cut -d: -f1)

  while IFS= read -r line; do
    echo "$f:$line: ImGui.BeginTable(; use HubTable.Begin"
    begin_table=$((begin_table + 1)); hits=$((hits + 1))
  done < <(grep -n 'ImGui\.BeginTable(' "$f" | cut -d: -f1)

  while IFS= read -r line; do
    echo "$f:$line: ImGui.BeginTabBar( without FittingPolicyScroll; use HubTabs.Begin"
    begin_tabbar=$((begin_tabbar + 1)); hits=$((hits + 1))
  done < <(grep -n 'ImGui\.BeginTabBar(' "$f" | grep -v 'FittingPolicyScroll' | cut -d: -f1)

  rel="${f#"$dir"/}"
  if [[ "$rel" != UI/* && "$rel" != */UI/* ]]; then
    while IFS= read -r line; do
      echo "$f:$line: ImGui.PushStyleColor( outside UI/; use a HubStyle role"
      push_style_color=$((push_style_color + 1)); hits=$((hits + 1))
    done < <(grep -n 'ImGui\.PushStyleColor(' "$f" | cut -d: -f1)
  fi

  while IFS= read -r line; do
    content="${line#*:}"
    trimmed="${content#"${content%%[![:space:]]*}"}"
    [[ "$trimmed" == //* ]] && continue
    lineno="${line%%:*}"
    echo "$f:$lineno: U+2026 in a string literal renders as three dots in the game font; end a status line with a full stop instead"
    ellipsis=$((ellipsis + 1)); hits=$((hits + 1))
  done < <(grep -nP '"[^"]*\x{2026}[^"]*"' "$f" || true)

  while IFS= read -r line; do
    content="${line#*:}"
    trimmed="${content#"${content%%[![:space:]]*}"}"
    [[ "$trimmed" == //* ]] && continue
    lineno="${line%%:*}"
    echo "$f:$lineno: U+2212 in a string literal renders as \"=\" in the game font; use ASCII \"-\""
    minus=$((minus + 1)); hits=$((hits + 1))
  done < <(grep -nP '"[^"]*\x{2212}[^"]*"' "$f" || true)
done

echo "---"
echo "ImGui.TextColored(HubStyle.: $text_colored"
echo "TableSetupColumn( pixel width: $table_width"
echo "ImGui.BeginTable(: $begin_table"
echo "ImGui.BeginTabBar( without FittingPolicyScroll: $begin_tabbar"
echo "ImGui.PushStyleColor( outside UI/: $push_style_color"
echo "U+2026 in a string literal: $ellipsis"
echo "U+2212 in a string literal: $minus"

[[ "$hits" -eq 0 ]]
