# Tabstep

Tabbed Project window for Unity — browse your assets like Windows Explorer.

## Tabstep window

The folder view is Unity's stock Project browser hosted inside the window, so
search, thumbnails, drag & drop, renaming and context menus all behave exactly
like the built-in Project window. On top of it, Tabstep adds:

- **Tabs** — open any number of folders side by side. Tabs (including their
  history) survive domain reloads and editor restarts. Drag tab headers to
  reorder them; tabs of same-named folders show their parent name too
  (`Scripts — Player`), and the `▾` button at the right end lists every tab.
- **Pinned tabs** — right-click a tab to pin it: it moves to the left edge,
  shrinks to its folder icon and ignores Ctrl+W / middle-click (closing it
  explicitly via the context menu still works). Close Others / Close to the
  Right spare pinned tabs.
- **Reopen closed tab** — `Ctrl+Shift+T` (or the tab context menu) brings back
  the last closed tab with its full history; the last 10 are remembered.
- **Back / forward history per tab** — navigate like a web browser, including
  with the mouse side (thumb) buttons. Right-click ◀ / ▶ for the full history
  as a dropdown.
- **Per-tab search** — each tab remembers its own search filter and restores
  it when the tab becomes active again.
- **Quick Access** — right-click the `+` button for bookmarked folders; add
  the current folder from there or from a tab's context menu. Stored per user
  and per project in `UserSettings/`.
- **Explorer-style address bar** — back/forward/up buttons, the folder icon and
  the current path as clickable breadcrumb segments in a sunken field (hover
  highlights them; parents that don't fit collapse behind `«`). Click any
  segment to jump there, or click the `›` between segments to pick one of that
  folder's subfolders, exactly like Explorer's chevrons.
- **One single bar instead of three** — with [Harmony](#harmony-optional-but-recommended)
  available, the embedded browser's own toolbar (create button + search field)
  and its `Assets > ...` path header are removed entirely; the create button
  and the search field move into the navigation bar and the two freed rows go
  to the folder view. Stock Project windows are not affected. Toggleable via
  the Navigation Bar preference (off restores the stock browser chrome).
- **Editable path** — click the empty part of the address bar, the current
  folder name, or press `Ctrl+L` / `Alt+D` to type or paste a path. Accepts
  `Assets/...`, `Packages/...` and absolute paths inside the project (quotes
  from "Copy as path" are fine); pasting an asset file path jumps to its
  folder and pings the asset.
- **Copy / paste paths** — right-click the path bar for Copy Path,
  Copy Absolute Path and Paste Path; tabs offer Copy/Paste Path too.
- **Pings open a new tab** — when something outside the window changes the
  shown folder (clicking an object field in the Inspector, "Show in Project"),
  the destination opens as a new tab instead of losing your current place.
  Navigation inside the window stays in the same tab. Toggleable in preferences.
- **Move assets between tabs** — drag assets out of the folder view and drop
  them onto another tab's header to move them into that tab's folder. Hovering
  a tab for a moment while dragging spring-loads it (switches the view), so you
  can also drop into a subfolder inside it — just like Explorer.
- **Folder drop** — drop folders onto the empty part of the tab bar to open
  them as new tabs.
- **`Assets > Open in Tabstep`** — open the selected folder in a new tab.

## Tabstep Shelf

A small floating tray for objects in transit (`Shelf` button in the navigation
bar, `Ctrl+Shift+D`, or `YozoLab > Tabstep Shelf`). Park assets or scene
objects on it, then drag them back out **anywhere Unity drag & drop reaches**:
another tab's folder, an Inspector object field, the Hierarchy, the Scene view.

- **Getting things onto it** — drop them on the shelf window, drop them on the
  `▼ Shelf` zone that appears at the right end of the tab bar while you drag
  assets, or use *Send Selection to Shelf* in a tab's context menu.
- **One-shot by default** — dragging an item out consumes it: the shelf is a
  hand-off, not a copy source. Dragging it back before dropping restores it;
  the behaviour is toggleable in preferences.
- **Stays out of the way** — shown as a borderless popup that never steals
  focus, so you can switch tabs underneath it mid-drag. Drag its title row to
  move it. It survives domain reloads, closes with its Tabstep window (unless
  pinned with the `Pin` button), and an emptied unpinned shelf closes itself.
- Items are stored as GUIDs / GlobalObjectIds, so they survive domain reloads;
  scene objects resolve while their scene is open and grey out otherwise.

## Tabstep Inspector (parked)

A tabbed Inspector also exists in the codebase, but it is **temporarily
withdrawn and invisible in Unity** while its integration strategy is reworked
in a separate scope — hooking the inspector-opening paths destabilized the
editor. The code sits behind the `TABSTEP_INSPECTOR` scripting define symbol;
without that define none of it is compiled, registered or shown.

## Usage

Open via `YozoLab > Tabstep`.

| Shortcut                      | Action                                                                       |
| ----------------------------- | ---------------------------------------------------------------------------- |
| `Ctrl+T`                      | New tab                                                                      |
| `Ctrl+W`                      | Close tab (closing the last tab closes the window; pinned tabs are immune)   |
| `Ctrl+Shift+T`                | Reopen the last closed tab                                                   |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab                                                          |
| `Ctrl+1` … `Ctrl+8`           | Jump to that tab                                                             |
| `Ctrl+9`                      | Jump to the last tab                                                         |
| `Alt+←` / `Alt+→`             | Back / forward                                                               |
| Mouse side buttons            | Back / forward                                                               |
| `Alt+↑`                       | Parent folder                                                                |
| `Ctrl+L` / `Alt+D`            | Edit the path (`Enter` to go, `Esc` to cancel)                               |
| `Ctrl+F`                      | Focus the search field (when it lives in the navigation bar)                 |
| `Ctrl+Shift+C`                | Copy the current folder's absolute path                                      |
| `Ctrl+Shift+D`                | Toggle the shelf                                                             |
| Drag tab                      | Reorder tabs                                                                 |
| Middle-click tab              | Close tab                                                                    |
| Right-click tab               | Pin / quick access / shelf / close others / duplicate / copy & paste path    |
| Right-click `+`               | Quick Access menu                                                            |
| Right-click `◀` / `▶`         | Full history dropdown                                                        |
| Right-click path bar          | Copy path / copy absolute path / paste path / edit path                      |

Preferences live under `Edit > Preferences > Yozolab > Tabstep`
(new-tab folder, new-tab position, navigation bar, tab title length,
middle-click close, mouse side buttons, ping behaviour, shelf one-shot).

## Harmony (optional but recommended)

Tabstep uses [Harmony](https://github.com/pardeike/Harmony) — when it is
present in the project — to remove the embedded browser's toolbar and
`Assets > ...` path header and fold them into its own navigation bar. Harmony
is loaded by reflection at runtime: there is no hard dependency, no scripting
define, and no setup beyond having `0Harmony.dll` somewhere in the project.

- **VRChat projects**: the VRChat SDK already ships `0Harmony.dll` — nothing
  to do.
- **Other projects**: download the
  [Lib.Harmony NuGet package](https://www.nuget.org/packages/Lib.Harmony),
  rename the `.nupkg` to `.zip`, extract it, and copy `lib/net48/0Harmony.dll`
  into your project (e.g. `Assets/Plugins/`). Alternatively install
  `Lib.Harmony` via [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity)
  or any other means that gets the DLL into the project.

Without Harmony, Tabstep still works: the browser keeps its own toolbar
(create + search stay there), and the redundant path header is blanked out in
place instead of being removed — its row remains as empty space.

The patches only affect the browsers Tabstep hosts, and only while the
navigation bar is enabled; regular Project windows are never touched.

## Notes / limitations

- The embedded browser is shared between tabs. The folder, its history and the
  search text are kept per tab; the scroll position is not.
- While searching, a search-scope header row appears above the results, just
  like in the stock Project window; clearing the search removes it again.
- The search field in the navigation bar accepts the stock filter syntax
  (`t:Texture`, `l:MyLabel`, ...); the type/label filter dropdown buttons of
  the stock toolbar are not replicated.
- Tabstep reaches into Unity's internal `ProjectBrowser` via reflection (and
  optionally Harmony). If a future Unity version changes that API the window
  degrades — to a warning message, or to the stock browser chrome — instead
  of breaking; tested against Unity 2022.3.

# Vibe Coding
