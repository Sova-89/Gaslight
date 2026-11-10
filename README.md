# Gaslight — FPS and mod load profiler for Hollow Knight

**Find out what is eating your FPS — and which mod is to blame.**
A profiler for Hollow Knight mods: an in-game FPS graph, per-mod and per-method timings, and an HTML report with the lag spikes and who caused them.

*Русская версия: [README.ru.md](README.ru.md)*

> **Status: pre-release (1.1.0).** Written for the Hollow Knight Modding API.
> **Interface language: English and Russian.** By default it follows your system language; switch it in game (*Options → Mods → Gaslight → Language*, or the `PageUp` panel). It covers the HUD, setting descriptions, the log and the HTML report. Code comments are in Russian.

## What it does

- **In-game HUD** (toggle with `Insert`): FPS graph, FPS, 1% low, how many ms per frame all mods take, how much Gaslight itself takes, top-3 mods, the last lag spike and who caused it.
- **Per-method timings.** Gaslight finds the places where the game calls into a mod and times them: Unity messages (`Update`, `FixedUpdate`, `OnGUI`, …), coroutines, PlayMaker actions, event handlers (`On.*` hooks, `ModHooks`, lambdas, UI callbacks) and Harmony patches (prefix / postfix / finalizer).
- **"Who is to blame" for lag spikes.** Every frame that is much longer than usual is classified: a mod (and which method), scene load, garbage collection, several mods together, or "not mods" (game / GPU / system).
- **Report**: `report.html` plus `mods.csv`, `timeline.csv`, `spikes.csv` in `Reports\<date-time>\`. It is saved automatically (every minute or so on a scene change, when the game window loses focus, and when you quit), so closing the window is fine. `Home` saves an extra snapshot into a new folder.
- **Observer only.** Gaslight never disables, limits or changes anything in the game or in other mods. It only starts and stops stopwatches.
- **Built for big mod lists** (designed for ~100 mods): see [Performance](#performance).

## Install

1. Download `Gaslight.zip` from the [Releases](../../releases) page.
2. Extract it so that the files end up here:
   ```
   Hollow Knight\hollow_knight_Data\Managed\Mods\Gaslight\Gaslight.dll
   Hollow Knight\hollow_knight_Data\Managed\Mods\Gaslight\report_template.html
   ```
3. Start the game. The first lines of the mod log (`ModLog.txt`) should say `Gaslight 1.1.0 started` (`Gaslight 1.1.0 запущен` if the language is Russian).

Requires the Hollow Knight [Modding API](https://github.com/hk-modding/api). Compiled against Hollow Knight 1.5.78.11833 with the Modding API (MonoMod.RuntimeDetour 21.4.29.1). Other versions have not been tested.

## Hotkeys

Defaults (change them in **Options → Mods → Gaslight**: press an arrow on the key row, then press the key you want):

| Key | Action |
|---|---|
| `Insert` | show / hide the HUD |
| `End` | compact / detailed HUD |
| `Home` | save a report right now |
| `PageUp` | on-screen settings panel (↑↓ pick, ←→ change, Enter assign a key, Esc close) |

## Where the reports are

Next to `Gaslight.dll`: `Mods\Gaslight\Reports\<date-time>\report.html`. Open `report.html` in a browser. Settings are in `Mods\Gaslight\settings.txt` (created on first start).

## Performance

- In a real Hollow Knight run (6 mods, 2541 timed methods, ~500 fps) Gaslight itself took **0.07 ms per frame**: 3.3 % of a 2 ms frame, which is about 0.4 % at 60 fps. Each stopwatch costs ~100 ns per call. The same run showed the cost of installing the stopwatches: about a minute after start-up (9 s of CPU, spread over the frames).
- Start-up: mod DLLs are read from disk with Mono.Cecil (raw IL, no per-method objects, so almost no garbage). Scanning ~27 000 methods took ~120 ms in a test. Stopwatches are installed gradually in the background with an **average** budget of `StepBudgetPercent = 15` % of the frame time (at most `StepBudgetMs = 1.5` ms), so the FPS dip while it installs stays small and a big mod list just takes longer to hook (a few minutes for ~100 mods). The HUD shows `(installing…)` meanwhile, and lag spikes that happen during that time are marked as such. When it is done, the log says how long it took.
- Nothing is dropped to save time: no hook limits (only very large safety caps).

## Crash safety

Mods built for another game version can contain methods whose signatures refer to types that no longer exist. Asking Mono about such a method through reflection can crash the whole game (an access violation that `try/catch` cannot stop). Gaslight therefore:

- reads the DLL from disk and never asks Mono about a method before checking that all types in its signature exist (such a method can never run, so it is simply not timed — the log says how many);
- writes what it is about to do to `scan_guard.txt`; if the game still dies, the next start moves that spot to `quarantine.txt` and skips it (only affects measuring; delete the file to reset).

## What it can and cannot tell you

- Time of **entry points** into a mod is measured. If a mod calls a method of another mod or library, that time counts for the caller.
- When a mod hooks a game method (`On.*`) and calls the original, the game's own time inside the original is **not** charged to the mod: it is counted separately ("game inside a mod hook"). This only works when the hook calls the original directly; if the original is passed on to another method, its time still counts for the mod.
- Work on other threads is not measured. Rendering, physics and GPU are not mods: they end up as "other".
- Gaslight itself uses a little time; it is shown as a separate "Gaslight" line.
- Gaslight shows **load and lag**, not mod conflicts.

## Settings you may want

| Setting | Meaning |
|---|---|
| `HookScope=full` (default) / `light` | `full`: everything above; `light`: only Unity messages and coroutines |
| `StepBudgetPercent=15` | share of the frame time spent on finding and hooking methods at start-up, on average (speed only: lower = smaller FPS dip, longer install) |
| `StepBudgetMs=1.5` | upper limit of the same budget, ms per frame |
| `SpikeMinMs=40` | a lag spike is a frame longer than this and `SpikeFactor` times longer than usual |
| `SelfProtect` | if Gaslight itself takes more than `SelfBudgetPercent` % of a frame for a while (after start-up), it stops timing the tiniest methods |
| `ProfilerEnabled=false` | FPS graph only, no hooks (restart needed) |
| `ReportAutosaveSec=60` | autosave the report on scene changes at most this often (seconds; `0` = off). It is also saved when the window loses focus and, as a fallback, every 3x this time |
| `Language=auto` / `en` / `ru` | interface language; `auto` follows the system language. Changes at once, also from the in-game menu / panel |

## Build from source

Needs Windows (uses the C# compiler that ships with .NET Framework 4; the code is C# 5) and a Hollow Knight install:

```
build_hk.bat "C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight"
```

The DLL appears in `out\hk\Gaslight.dll`. Without an argument the script uses the `HOLLOWKNIGHT` environment variable and then the default Steam folder.

Where to look in the code:

| I want to change… | Open |
|---|---|
| hotkeys, thresholds, settings (and their English / Russian descriptions) | `src/shared/Settings.cs` |
| other interface texts | the `L.T("русский", "English")` calls in the code (`src/shared/Lang.cs`); the report page has its own texts in `report_template.html` |
| how the HUD looks | `src/shared/HudLayout.cs` |
| the HTML report | `report_template.html` (next to the DLL, no rebuild needed) |
| which methods are timed | `src/shared/IlScanner.cs` |
| which DLLs are ignored | `src/shared/Hooker.cs` (`IgnorePrefixes`) |
| how a lag and its verdict are computed | `src/shared/Profiler.cs` |
| Hollow Knight adapter (mod menu, finding DLLs) | `src/hk/GaslightMod.cs` |

## License

[MIT](LICENSE)
