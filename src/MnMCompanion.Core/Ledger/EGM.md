# Ledger

## Overview
Discovers Monsters & Memories game directories across Windows `LocalLow` and Linux Wine/Proton prefixes, incrementally reads and deduplicates character/social JSON ledger files, decodes base64 and coin fields, and tracks the actively played character (`namespace MnMCompanion.Ledger`) across [ActiveCharacterWatch.cs](./ActiveCharacterWatch.cs), [GameFolderFinder.cs](./GameFolderFinder.cs), [LedgerFolder.cs](./LedgerFolder.cs), [LedgerRow.cs](./LedgerRow.cs), and [LedgerText.cs](./LedgerText.cs).

## Interface
- `ActiveCharacterWatch` (`sealed class` in [ActiveCharacterWatch.cs](./ActiveCharacterWatch.cs)):
  - `Update(IReadOnlyList<LedgerSource> sources, string? showing) -> LedgerSource?`: Tracks `LastActive` write times across `HasLedger` sources and returns a newly active character to switch to.
- Wine/Proton discovery and diagnostics ([GameFolderFinder.cs](./GameFolderFinder.cs)):
  - `FoundGameFolder(string Root, int Characters, DateTime LastActive, string Prefix)` (`sealed record`)
  - `DiagnoseFacts(string Version, string System, string Runtime, bool UnderWine, string WineSigns, IReadOnlyList<(string Name, string? Value)> Environment, string? LinuxRoot, string RootInUse, string DefaultRoot, int DefaultCharacters, string? ChosenRoot, int ChosenCharacters, IReadOnlyList<FoundGameFolder> Found, string WindowTest, string WebView, string OwnFolder)` (`sealed record`): Environment, Wine detection, candidate folder, and UI test facts formatted by `GameFolderFinder.Report`.
  - `GameFolderFinder` (`static partial class`):
    - `RunningUnderWine(Func<string, string?>? env = null, Func<string, bool>? fileExists = null) -> bool`
    - `LinuxRoot() -> string?`
    - `Find(string linuxRoot) -> IReadOnlyList<FoundGameFolder>`
    - `Choose(string? chosenRoot, Func<string, int> charactersIn, int charactersAtDefault, bool underWine, Func<string?> bestFound) -> string?`
    - `Best(string linuxRoot) -> FoundGameFolder?`
    - `Prefixes(string linuxRoot) -> IEnumerable<string>`
    - `Mask(string text) -> string`
    - `Report(DiagnoseFacts f) -> string`
- Character directory locator and incremental reader ([LedgerFolder.cs](./LedgerFolder.cs)):
  - `LedgerSource(string Server, string Character, string Directory, DateTime LastActive, bool HasLedger = true)` (`sealed record`): Exposes `string LedgerDirectory { get; }`, `string ServerName { get; }`, and `string Label { get; }`.
  - `LedgerLocator` (`static class`): `DefaultRoot() -> string`, `Find(string? root = null) -> IReadOnlyList<LedgerSource>`, `Capitalize(string name) -> string`, `Servers(IEnumerable<LedgerSource> sources) -> IReadOnlyList<string>`, `On(IEnumerable<LedgerSource> sources, string server) -> IReadOnlyList<LedgerSource>`.
  - `LedgerFolder(string characterDirectory, bool characterFilesOnly = false)` (`sealed class`): Exposes `string Directory { get; }`, `string LedgerDirectory { get; }`, `string Character { get; }`, `IReadOnlyList<LedgerRow> Rows { get; }`, `long Version { get; }`, `int FileCount { get; }`, `string? Error { get; }`, and `Refresh() -> bool`.
- Row model, JSON parser, and text/coin decoders ([LedgerRow.cs](./LedgerRow.cs), [LedgerText.cs](./LedgerText.cs)):
  - `LedgerFileKind` (`enum` in [LedgerRow.cs](./LedgerRow.cs)): `Character`, `Social`.
  - `LedgerRow` (`sealed class` in [LedgerRow.cs](./LedgerRow.cs)): Decoded ledger event exposing `init` properties (`Act`, `Time`, `Name`, `Zone`, `Level`, `Actor`, `Owner`, `Class`, `File`, `Raw`, `Identity`, `RawJson`, `DetailJson`), computed `bool ByOwner { get; }`, and field helpers `Get(string field) -> string`, `Text(string field) -> string`, and `Int(string field) -> int`.
  - `LedgerParser` (`static class` in [LedgerRow.cs](./LedgerRow.cs)): `Parse(string json, LedgerFileKind kind) -> List<LedgerRow>` and `Envelope(string json) -> Dictionary<string, string>`.
  - `LedgerText` (`static class` in [LedgerText.cs](./LedgerText.cs)): `Decode(string s) -> string`, `Prefixed(string s) -> string`, `Packed(string digits) -> long`, `Coin(string text) -> long`.

## Invariants
- Single-threaded stateful types: `LedgerFolder` and `ActiveCharacterWatch` mutate internal caches without locking and must be accessed from a single thread at a time.
- `ActiveCharacterWatch` polling protocol: The first `Update()` call primes internal timestamps across `HasLedger` sources and always returns `null`; callers must invoke `Update()` on every poll tick (even when auto-follow is disabled) so gradual writes are tracked, and `Update()` returns `null` if `showing` also advanced.
- Character folder admission: `LedgerLocator.Find` admits `<server>/<character>` directories that contain a `Ledger` directory or `chats.json` / `windows.json`; folders with zero `Ledger/*.json` files are returned with `HasLedger = false` and `LastActive = DateTime.MinValue`, sorted after active ledger characters.
- File scoping and cross-file vs intra-file deduplication: When `characterFilesOnly` is `false` (default), `LedgerFolder.Refresh()` scans top-level `Ledger/*.json` (`_Character_` as `Character`, others as `Social`) followed by `Ledger/Archive/*.json` (`Social`); when `characterFilesOnly` is `true`, it scans only top-level `Ledger/*_Character_*.json` files and skips both `_Social_` files and `Ledger/Archive`. Identical `Identity` rows (`f01\u0001f02\u0001f03\u0001f04\u0001f07\u0001f09`) within the same file are preserved while duplicates across later files are dropped; `Rows` is stably sorted by `Time` ascending, and `Version` increments iff cached file entries change.
- Row parsing rules: `LedgerParser.Parse` reads JSON objects from a root array or any top-level array property of a root object, requiring `"f01"` and an `"f04"` timestamp parseable via `DateTimeOffset.TryParse(..., InvariantCulture, DateTimeStyles.None)` (converted to `.LocalDateTime`). Object-valued `"f03"` strings have their inner properties (`d01`...) flattened into `Raw` (keeping raw `"f03"` if malformed JSON) and base64-decoded into `DetailJson`; `Name` (`f02`) and `Zone` (`f05`) decode via `LedgerText.Prefixed`; `Level` parses `"f06"` (`0` on failure); and `Class` (`f10`) normalizes `"None"` (case-insensitive) to `""`.
- Concurrent game-write resilience: `LedgerFolder.Refresh()` captures `(LastWriteTimeUtc, Length)` before opening each file so writes mid-read force a re-read next cycle; `IOException`, `UnauthorizedAccessException`, or `JsonException` (half-written JSON) preserves previously cached rows for that file and populates `Error`.
- Non-clobber prefix selection: `GameFolderFinder.Choose` never overrides a user-chosen or default Windows folder that already contains `>= 1` character, and `Find` deduplicates symlink-aliased Steam prefixes by character-set signature.
- Heuristic base64 decoding: `LedgerText.Decode` requires length `>= 4` divisible by 4, valid base64 ASCII, not `<= 8` pure letters, not pure hex digits, and strict UTF-8 output free of control characters.

## Side Effects
- Strictly read-only filesystem access: `LedgerFolder.Refresh()` opens `Ledger/*.json` (and `Ledger/Archive/*.json` unless `characterFilesOnly` is `true`) with `FileShare.ReadWrite | FileShare.Delete`; `LedgerLocator.Find()` and `GameFolderFinder` enumerate character folders (`Ledger`, `chats.json`, `windows.json`), Wine `Z:\` prefix directories (Steam/Flatpak Steam/`libraryfolders.vdf`, UMU, Lutris, `.wine`), and `Z:\proc\version` without creating, modifying, or deleting files.
- Environment and clock reads: Queries Wine environment variables (`WINEPREFIX`, `STEAM_COMPAT_DATA_PATH`, `PROTON_VERSION`, `WINELOADERNOEXEC`), `Environment.SpecialFolder.UserProfile`, and local timezone offsets (`DateTimeOffset.LocalDateTime`).
- In-memory state mutations in `LedgerFolder` and `ActiveCharacterWatch`; `LedgerParser`, `LedgerText`, `LedgerRow`, and formatting/filtering helpers are pure in-memory functions with zero `FileSystemWatcher`, registry, lock, or network usage.

## Verification
- Run: `dotnet test MnMCompanion.sln`
