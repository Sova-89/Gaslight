# Changelog

## 1.1.0 — first public release (pre-release)

Hollow Knight (Modding API).

### Fixed
- **Game crash while scanning mods** (access violation in `RuntimeMethodInfo.get_IsGenericMethod`). It happened when a mod contained a method whose signature refers to a type missing from the current game version. Mod DLLs are now read from disk with Mono.Cecil and Mono is asked about a method only after all types of its signature are confirmed to exist.
- The self-protection could permanently switch off timing of 24 small methods because of the (temporary) load of installing the stopwatches at start-up. It now ignores the start-up phase.
- `report.html` contained the report data twice (a template comment repeated the placeholder), which made the file about twice as large as needed.

- The report verdict judged mod load only by percent of the frame: at 500 FPS 0.3 ms (15 % of a 2 ms frame) was called a "heavy load". It now also looks at the absolute time (under 0.5 ms/frame = "barely affect", "heavy" needs at least 1.5 ms/frame).

### Added
- Timing of more than `Update`: coroutines, PlayMaker actions, event handlers (`On.*`, `ModHooks`, lambdas, UI callbacks) and Harmony patches. `HookScope=full` (default) / `light`.
- `scan_guard.txt` / `quarantine.txt`: if the game still dies during a scan, the next start skips that spot.
- Works with long mod lists (designed for ~100): IL is read as raw bytes (no per-method objects, almost no garbage); small DLLs are hooked first; no fixed limit on the number of timed methods.
- The install budget is an **average** per-frame budget: `StepBudgetPercent` (default 15 %) of the frame time, capped by `StepBudgetMs` (1.5 ms). A heavy hook is followed by a few "rest" frames. A fixed 1.5 ms was too much at high FPS (28 % of a 5 ms frame in the first real Hollow Knight test). The log reports how long installation took.
- Lag spikes that happen while the stopwatches are still being installed are marked (their cause may be the installation itself); the HUD explains "not mods" as game / GPU / system.
- **English and Russian interface.** New setting `Language` (`auto` / `en` / `ru`; `auto` follows the system language). It can be changed in game (mod menu or the `PageUp` panel) and covers the HUD, setting descriptions, log, `settings.txt` comments and the HTML report.

- **Report autosave.** The report is saved on scene changes (at most every `ReportAutosaveSec`, 60 s by default), when the game window loses focus, and when the process exits - not only when you quit through the game menu. One folder per game launch, rewritten in place (files are replaced atomically, disk writes happen on a background thread, building the report takes a few ms even after hours of play). `Home` still makes a separate snapshot.
- **The game's time inside mod hooks is no longer charged to mods.** In `On.*` hooks the call of the original (`orig(...)`) is timed separately. Before, a mod that wrapped a slow game method (for example `LoadGame` / `SaveGame`) was blamed for the game's own time. Such lag spikes are now "not mods: the game was running its own code inside a mod hook".

### Known
- Verified in real Hollow Knight play (6 mods, up to 2541 timed methods): several sessions up to ~9 minutes, including Godhome Pantheon 1 and a ~5-minute Godhome run at ~500 fps. No crash. Gaslight itself took 0.07 ms per frame (3.3 % of a 2 ms frame, ~0.4 % at 60 fps). Not yet tested: a 100-mod list and multi-hour sessions in the real game.
- The original is separated only when the hook calls it directly. If a mod passes `orig` on (to a closure or another method), the time still counts for the mod.
