# The XIV Hub theme

Every XIV Hub plugin draws in one theme, so a stack of ten windows reads as one
product rather than ten hobby projects. The palette is lifted from xivhub.net;
the crystal in the logo is where the gold comes from.

Seven files, all linked source:

| File | What it is |
| --- | --- |
| `UI/HubColors.cs` | The named palette. The only place a hex literal lives. |
| `UI/HubStyle.cs` | The option table, `Push`/`Pop`, and the semantic helpers. |
| `UI/HubThemeConfig.cs` | The persisted overrides and where they are stored. |
| `UI/HubThemeEditor.cs` | The settings UI, generated from the option table. |
| `UI/HubText.cs` | Coloured text that wraps, and the seven role helpers (`Bad`, `Warn`, and so on). |
| `UI/HubTable.cs` | A themed `BeginTable`, stretch/fit/icon columns, and cell helpers. |
| `UI/HubTabs.cs` | A tab bar that scrolls instead of truncating. |

`UI/HubWindow.cs` (`FitHeight`) is an eighth file, wired only into the plugins that have a window it
applies to; see "Helpers" below.

## Wiring a plugin

Add the seven `<Compile Include>` lines, then three calls:

```csharp
// once, at plugin start
_theme = new HubThemeConfigService(PluginInterface.GetPluginConfigDirectory(),
                                   (msg, ex) => Log.Warning(ex, msg));
HubStyle.Init(_theme);

// around the whole draw
private void Draw()
{
    HubStyle.Push();
    try { _windowSystem.Draw(); }
    finally { HubStyle.Pop(); }
}

// in the config window
HubThemeEditor.Draw(_theme);
```

One wrap point, not one per window: no window class should know the theme
exists. `Push` counts what it pushed and `Pop` pops exactly that — ImGui's style
stack is global, so a miscount corrupts every plugin that draws after this one.

The config lives at `<pluginConfigs>/XivHub/ui-theme.json`, beside the plugin
config directories rather than inside one. The theme belongs to the family, so
changing a colour in one plugin reaches the rest.

## Helpers

Four helpers sit next to the theme so a call site doesn't have to re-derive its rules (wrapping,
`NoSavedSettings`, `FittingPolicyScroll`, `ImGuiHelpers.GlobalScale`) each time. `tools/theme-lint.sh
<plugin dir>` greps a plugin for the raw ImGui calls these replace and exits 1 if it finds any; run
it before every commit that touches UI code.

- **Text**: `HubText.Bad`/`Warn`/`Good`/`Info`/`Faint`/`Muted`/`Accent(text)` draw a `HubStyle` role
  wrapped at the window edge, so a role colour never becomes an unreadable single line running off
  the window; `Colored(color, text)` is the same for a colour outside those seven. `Inline(color,
  text)` does not wrap, for a coloured fragment built with `SameLine` (a number inside a sentence).
- **Tables**: `HubTable.Begin(id, columns, size, extra)` sets `RowBg | Resizable | NoSavedSettings`
  plus whatever `extra` asks for, leaving out `NoSavedSettings` when `extra` has `Hideable` or
  `Reorderable` (the user's hidden and reordered columns should come back; change such a table's
  id when its columns change); `End()` closes it. `Stretch(name, weight, extra)` and `Fit(name, extra)`
  replace `TableSetupColumn` for a name column and a fixed one respectively (see "Tables and tabs"
  below for why a pixel width is wrong; a `Fit` column can't be dragged, which is what keeps it fitted); `Icon(id, size, extra)` is the one legitimate fixed-pixel column,
  scaled by `ImGuiHelpers.GlobalScale`. `Cell(text)`, `Cell(color, text)` and `Number(text)`
  (right-aligned) are the per-cell draws.
- **Tabs**: `HubTabs.Begin(id)` / `End()` is `BeginTabBar`/`EndTabBar` with `FittingPolicyScroll`
  already set.
- **Window height**: `HubWindow.FitHeight(this)`, called at the end of a `Window.Draw()`, caps that
  window at its own content height instead of letting it be dragged taller than anything it draws.
  Wire `UI/HubWindow.cs` only into a plugin that has a window like this; do not call it from a
  window whose child fills `GetContentRegionAvail().Y`, since that child would then size itself off
  the window's last height instead of its own content, and the two would never settle.

## The rule

**Gold marks what the user can act on and what is selected, and its strength
ranks them.**

| Strength | Where |
| --- | --- |
| Solid gold, dark text | `HubStyle.Primary()`: the one irreversible action in a window |
| Brass tint (gold at 26%, 36% hover, 46% pressed) | every other button, like the game's own buttons |
| Gold tint (30%) | the selected tab |
| Gold indicator | the check mark, the slider grab, a separator or scrollbar being dragged |

Structure that is not an action stays on the cool dark ramp: collapsing
headers and selected rows (`HubHeader`), table headers and tabs at rest
(`HubTableHead`), input frames (`HubFrameBg`) with a 1 px hairline at 12% white.
A gold-accented theme fails when structure takes the accent too: the window
turns amber and the buttons stop standing out.

The window sits over the game, so a surface has to clear about 1.2:1 contrast
against `HubWindowBg` to read at all, and a button about 1.7:1. Check a new
surface against those numbers before adding it; the ramp in `HubColors.cs` is
ordered by lightness.

`Primary()` is for one action per window, such as "Confirm and run" or
"Apply 6 moves". A window with two of them has a hierarchy problem that a theme
cannot fix.

The semantic colours mean one thing each and are not decoration:

| | Means | Not |
| --- | --- | --- |
| `Good` | A realised gain — profit, a route that exists, a listing that landed | "this button is safe" |
| `Warn` | Reversible caution — held back, dry run off, roster incomplete | anything already broken |
| `Bad` | A failure that already happened | a risky-but-valid action |
| `Info` | Inert reference — ids, keys, paths, links | a call to action |

## Chrome is themed; domain colour is not

This is the distinction a port gets wrong first. The theme dresses the
*application chrome* — windows, frames, buttons, tabs, tables, scrollbars — and
the four semantic roles above. It does not own colour that carries meaning
specific to what a plugin is about.

Leave these alone, locally defined, in the plugin:

- InventoryCleaner's `Keep` / `Move` / `Danger` / `Seals` / `Materia` — a
  five-way category legend. It has to stay distinguishable at a glance, which is
  a different job from the theme's four roles.
- BlackjackAdvisor's `Felt`, `FeltRim`, `CardFace`, `SuitRed`, `SuitBlack` — a
  card table, deliberately not the app chrome. A gold felt would be worse.

Replace these, they are chrome wearing a local name:

- `Panel`, `SlotBg`, `Track`, `Rule`, `SlotBorder` → the surface ramp
  (`HubStyle.Surface` / `FrameBg` / `ChildBg` / `Ground`) and `color.border`.
  Use those named members, never `HubColors.Get("…")` at a call site: a mistyped
  name renders magenta at runtime instead of failing to compile.
- `Accent`, `AccentColor`, `Brass` → `HubStyle.Accent`. All three are already
  `(0.851, 0.741, 0.420)` in three different plugins, which is within a hair of
  `HubGold` — the family converged on this accent before the theme existed, so
  porting mostly means deleting the constant.
- `Good` / `Green`, `Warn` / `Amber`, `Danger` / `Red`, `Blue`, `Dim` / `Muted`
  / `Default` → `HubStyle.Good` / `Warn` / `Bad` / `Info` / `Faint`.

Test for which side a colour is on: would two of them ever need to be told
apart *at the same time*? A five-way legend must stay distinguishable, so it is
domain. A single "this failed" red never competes with anything, so it is
semantic and belongs to the theme. Most plugins turn out to have no domain
colour at all — SealHunter has none, and every colour in it was chrome or one of
the four roles.

If a domain palette has to sit next to the chrome, derive it so it does not
compete with gold: keep the category hues away from 30–50°, and match their
chroma to each other rather than to the accent.

## What "theme off" means

`Enabled = false` means **the chrome is untouched**, not that the plugin loses
its colours. The two halves behave differently on purpose:

- Anything that PUSHES ImGui style — `Push`/`Pop` and the `Primary()` scope —
  becomes a no-op, so every window falls back to the user's own Dalamud style.
- The semantic and ramp VALUES — `Accent`, `Good`, `Warn`, `Bad`, `Info`,
  `Faint`, `Surface` and friends — keep returning hub colours, because they are
  content rather than chrome. A "this failed" red is the plugin's own choice of
  colour; there is nothing to fall back to, and blanking it would lose meaning.

So a plugin with the theme off still shows a red failure line, but its buttons,
tabs and frames look like every other Dalamud plugin. That is the intent.

## What the wrap point cannot reach

`Push`/`Pop` dresses one plugin's own `UiBuilder.Draw`. Three things fall outside
it, by construction rather than by oversight:

- **Widgets drawn inside another plugin's window.** AutoPincher contributes a
  button to AutoRetainer's retainer-list overlay through
  `AutoRetainer.OnMainControlsDraw`. That draws inside AutoRetainer's frame, so
  the theme never wraps it — and theming it anyway would plant one gold-on-dark
  control in a window that is not ours. Leave those on the host's style.
- **Dalamud's own chrome.** Toast notifications (`NotificationType`), the DTR bar
  entry and the plugin installer entry are drawn by Dalamud outside every
  plugin's draw hook.
- **Anything the game renders.** Item icons, rarity colours off the Excel sheets,
  and job glyphs are game-authored art. Tinting them to match is not theming, it
  is defacing them.

## Extending it

When the theme lacks something you need, in order of preference:

**1. Use what is there.** Most "I need a new colour" turns out to be one of the
four semantic roles or a surface already on the ramp. Check `HubColors.Names`
first.

**2. Add an option to the table.** A value ImGui already has a `ImGuiCol` or
`ImGuiStyleVar` for belongs in `HubStyle`'s table — one entry, and both `Push`
and the settings editor pick it up with no further edit:

```csharp
new("color.tableHeaderBg", "Table Header Background", "HubTableHead", ImGuiCol.TableHeaderBg),
```

Point it at an existing palette name and set `Alpha` if it needs one. Add a
`Description` when the choice is not obvious from the label — it becomes the
tooltip.

**3. Add a palette name.** Only when no existing name means the right thing.
Derive it from the brand rather than inventing: the site tokens are `#080a11`,
`#eef1f8`, `#9aa6bd`, `#6a7488`, `#d9b370`, `#f4d79a`, `#86b8ec`, and the logo's
crystal ramp runs `#ffe7b3` → `#ecca84` → `#cda35d` → `#7c5f2c`. If you need a
surface, it goes on the existing dark ramp between two neighbours; to make an
action stand out, use gold's strengths above instead of a lighter surface.

**4. Add a scoped helper.** Behaviour that is not a single style value —
`Primary()` is the example — goes in `HubStyle` as an `IDisposable` scope that
pushes and pops a matched count. Never leave a push unbalanced across a
`return`; use `using`.

**Tables and tabs.** Dalamud's ImGui binding is older than ImGui's tab overline, so the selected
tab shows through `color.tabActive` alone.
- Give fixed columns no pixel width and no resize (`WidthFixed | NoResize` with no size, which is
  `HubTable.Fit`): ImGui then fits them to their header and contents every frame. In a `Resizable`
  table a resizable fixed column keeps the width it measured in its first frames, so rows that
  arrive later are cut off until "Size column to fit". A pixel width clips at larger numbers and
  font scales. Keep one `WidthStretch` column for the name.
- Pass `ImGuiTableFlags.NoSavedSettings` on data tables. ImGui saves column widths by position, so a
  table that gains a column loads the old first column's width into the new one.
- Pass `ImGuiTabBarFlags.FittingPolicyScroll` to a tab bar that can outgrow its window, so tabs
  scroll instead of being cut to "Retain…".

**Text.** No "…" (U+2026) anywhere in UI text, labels, status lines or tooltips: the game font
draws it as three centred dots, which reads as a glitch. End a status line with a full stop, and
name a button that opens a window for the window ("Shopping plan", "Buy from servant").

**What not to do.** Do not push a raw colour at a call site, do not read
`ImGui.GetStyle()` and patch it in place, and do not add a per-plugin theme
setting — a plugin that needs to look different from the family is a design
decision to make deliberately, not a config flag.

## Changing a default

Changing a value in the table changes it for every plugin that has not
overridden it, on their next build. That is the point of the shared kit, and it
is also the reason to be careful: check the change against the widget-state
artboards before shipping it, and remember users who already overrode that key
will not see it.
