# MnMCompanion.Core

## Overview
Platform-independent domain, parsing, analytics, and local-cache library ([MnMCompanion.Core.csproj](./MnMCompanion.Core.csproj), `TargetFramework = net10.0`, `RootNamespace = MnMCompanion`) powering the Monsters & Memories companion application and CLI/tests. Resolves and migrates the local application data directory via `AppFolder` ([AppFolder.cs](./AppFolder.cs)) and encapsulates all ledger ingestion, loot/history analytics, journal scrubbing, community contribution building, auction market math, recipe indexing, and timer rules without external NuGet packages, databases, network transports, or Windows UI dependencies.

## Interface
- Project definition: [MnMCompanion.Core.csproj](./MnMCompanion.Core.csproj) (`Microsoft.NET.Sdk`, `net10.0`, `Nullable = enable`, `ImplicitUsings = enable`, `RootNamespace = MnMCompanion`).
- `AppFolder` (`public static class` in [AppFolder.cs](./AppFolder.cs)): Resolves and migrates the application's local state directory:
  - `Name = "MnMCompanion"`, `PreviousName = "LootWindow"`, `OverrideVariable = "MNMCOMPANION_DATA"` (`public const string`s)
  - `Folder` (`public static string { get; }`): Thread-safe `Lazy<string>`-cached process-wide data directory path.
  - `Choose(string? overrideFolder, string localAppData) -> string`: Returns `Path.GetFullPath(overrideFolder.Trim())` when `overrideFolder` is non-whitespace, otherwise delegates to `Resolve(localAppData)`.
  - `Resolve(string localAppData) -> string`: Resolves `<localAppData>/MnMCompanion`, migrating `<localAppData>/LootWindow` via `Directory.Move` when only `LootWindow` exists, or falling back to `LootWindow` if locked.
- Child subsystem contracts:
  - [Community](./Community/EGM.md): Opt-in consent policy, schema-4 anonymized contribution builder, SHA-256 fingerprinting, and upload cadence (`MnMCompanion.Community`).
  - [History](./History/EGM.md): Vendor, harvest, trade, and item-movement ledger models, scoping/period filters, and statistical summaries (`MnMCompanion.History`).
  - [Journal](./Journal/EGM.md): NPC dialogue log parser and whole-word player-name privacy scrubber (`MnMCompanion.Journal`).
  - [Ledger](./Ledger/EGM.md): Windows/Wine game folder discovery, incremental JSON ledger reader/deduplicator, base64/coin decoders, and active-character watcher (`MnMCompanion.Ledger`).
  - [Loot](./Loot/EGM.md): Thread-safe `LootStore`, group split estimation, multi-character merge, session persistence, item valuation, Wilson drop rates, search, and breakdowns (`MnMCompanion.Loot`).
  - [Market](./Market/EGM.md): Persistent auction price book (`market.json`), `mnm-db.com` feed parser, item classification, standing price math, and refresh throttling (`MnMCompanion.Market`).
  - [Recipes](./Recipes/EGM.md): Wiki tradeskill recipe parser, local cache (`recipes.json`), level banding, multi-token/range search, and refresh throttling (`MnMCompanion.Recipes`).
  - [Timers](./Timers/EGM.md): Countdown timer book (`timers.json`), monster corpse timers, global hotkey validation, overlay opacity/click-through rules, and sound specifiers (`MnMCompanion.Timers`).

## Invariants
- Pure .NET BCL and zero external dependencies: Targets `net10.0` (`Nullable = enable`, `ImplicitUsings = enable`) with zero NuGet `PackageReference` entries and zero `ProjectReference` entries, allowing cross-platform execution and testing on Linux and Windows.
- Zero SQLite, zero network, and zero UI/Win32 boundary: Contains no SQLite or database engine (all local caches are plain JSON/text files), performs zero HTTP/socket calls (remote feeds and uploads are passed in/out as JSON strings with pure rate-limit plans), and has zero WPF, WinForms, audio playback, or Win32 P/Invoke dependencies.
- Read-only game data guarantee: Game installation folders, Wine/Proton prefixes, and game JSON ledger files are strictly read-only (`FileShare.ReadWrite | FileShare.Delete`); Core never creates, modifies, or deletes files inside game directories.
- Test/screenshot storage isolation: `AppFolder.Folder` passes `Environment.GetEnvironmentVariable("MNMCOMPANION_DATA")` and `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` to `AppFolder.Choose`; when `MNMCOMPANION_DATA` is non-whitespace, `Choose` returns `Path.GetFullPath(overrideFolder.Trim())` directly and skips `AppFolder.Resolve(localAppData)` (avoiding `Directory.Exists` checks and `Directory.Move` migration in `localAppData`) so tests and headless runs never read or mutate a user's live data folder.
- Safe legacy folder migration: `AppFolder.Resolve` renames `<localAppData>/LootWindow` to `<localAppData>/MnMCompanion` only when `MnMCompanion` is absent and `LootWindow` exists; if `Directory.Move` fails with `IOException` or `UnauthorizedAccessException` (e.g., another instance holds open files), it falls back to `LootWindow` rather than throwing or creating an empty split-brain directory. `AppFolder.Folder` caches the result once per process via thread-safe `Lazy<string>`.

## Side Effects
- `AppFolder.Folder` (on first read) eagerly queries `Environment.GetEnvironmentVariable("MNMCOMPANION_DATA")` and `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`; only when `MNMCOMPANION_DATA` is null or whitespace does it invoke `Resolve(localAppData)`, checking `Directory.Exists` and potentially executing a one-time `Directory.Move` from `LootWindow` to `MnMCompanion`.
- Child subsystem I/O boundaries: `Community`, `History`, and `Journal` are pure in-memory with zero disk I/O; `Ledger` performs read-only game directory, Wine prefix, and JSON ledger reads; `Loot`, `Market`, `Recipes`, and `Timers` read and atomically write (`*.tmp` staging + `File.Move`) local JSON state under `AppFolder.Folder` or caller-provided state folders, while `LootStore.NewSession` writes `<StateFolder>/sessions/*.txt` breakdown archives directly via `File.WriteAllText` (and `Timers` also performs read-only Windows Media `.wav` enumeration).
- Performs zero network I/O, zero database I/O, zero registry writes, and zero mutations outside the application's own state directory.

## Verification
- Run: `dotnet test MnMCompanion.sln`
