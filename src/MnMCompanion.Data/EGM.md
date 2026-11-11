# MnMCompanion.Data

## Overview
`MnMCompanion.Data` (`net10.0`, namespace `MnMCompanion.Data`, defined in [MnMCompanion.Data.csproj](./MnMCompanion.Data.csproj)) is the local SQLite persistence, incremental file-archiving, event-projection, and history-query layer for MnM Companion. Because the game client rotates old entries into archive folders and may prune files over time, `MnMCompanion.Data` maintains an append-only local copy (`ledger.db`) of all imported ledger rows and NPC journal lines across every server and character on the machine, projects character-owned rows into typed history tables, and assembles privacy-preserving community export payloads.

## Interface
- **Database & Schema ([LedgerDb.cs](./LedgerDb.cs))**
  - `public sealed record LedgerFileKey(string Server, string Character, string File)` — Composite identifier for an imported ledger JSON file (relative to `Ledger\`) or journal NPC file (relative to `journal\`).
  - `public sealed class LedgerDb : IDisposable` — Thread-safe SQLite database wrapper managing schema initialization, idempotent ledger/journal ingestion, and connection locking:
    - `public const int SchemaVersion = 1`
    - `public static string DefaultPath { get; }` — Default database path (`<AppFolder.Folder>/ledger.db`).
    - `public static string OtherFolder(string ledgerRoot)` — Deterministic isolated storage directory (`<AppFolder.Folder>/other-ledgers/<12-hex-sha256>`) for non-default game roots.
    - `public string FilePath { get; }` — Active SQLite database file path.
    - `public LedgerDb(string? path = null)` — Opens/creates the database at `path ?? DefaultPath`, configures WAL mode and busy timeout, applies DDL schema, seeds `act_code`, and initializes `meta`.
    - `public (long Size, string WrittenUtc)? ImportedState(LedgerFileKey key)` — Returns the last imported byte size and UTC write timestamp for a ledger file, or `null` if never imported.
    - `public int Import(LedgerFileKey key, long size, DateTime writtenUtc, IReadOnlyList<LedgerRow> rows, IReadOnlyDictionary<string, string> envelope)` — Deduplicates and inserts ledger rows into `ledger_row`, upserts `ledger_file`, and returns the count of newly inserted rows.
    - `public (long Size, string WrittenUtc)? JournalState(LedgerFileKey key)` — Returns the last imported byte size and UTC write timestamp for a journal file, or `null` if never imported.
    - `public (int Npcs, int Lines) ImportJournal(LedgerFileKey key, long size, DateTime writtenUtc, IReadOnlyList<JournalLine> lines)` — Upserts the NPC in `npc` (`COLLATE NOCASE`), updates `first_seen_utc` with the earliest timestamped line, deduplicates and inserts dialogue entries into `npc_line`, upserts `journal_file`, and returns `(Npcs, Lines)` added.
    - `public void MarkLootedNpcs()` — Updates `npc.looted_as_monster` (`1` or `0`) by matching NPC names against killed/looted mob names in `ledger_row` (`act_14` `name` and `act_13` `detail.d02`).
    - `public static LedgerFileKind KindOf(string file)` — Classifies filenames containing `"_Character_"` (case-insensitive) as `LedgerFileKind.Character`, and all others as `LedgerFileKind.Social`.
    - `public static bool IsArchived(string file)` — Returns `true` when `file` starts with `"Archive"` (case-insensitive).
    - `public long RowCount()` — Total row count in `ledger_row`.
    - `public long FileCount()` — Total file count in `ledger_file`.
    - `public IReadOnlyList<(string Act, string Meaning, long Rows)> RowsByAct()` — Row counts grouped by `act` left-joined with `act_code` meanings, ordered descending by count.
    - `public string ContributorId()` — Lazily generates, persists in `meta['contributor_id']`, and returns a random unlinked 32-hex GUID (`"N"` format) for community submissions.
    - `public void Dispose()` — Disposes the underlying `SqliteConnection` under the instance lock.
- **Act Code Catalog ([ActCodes.cs](./ActCodes.cs))**
  - `public sealed record ActCode(string Act, string Meaning, string Certainty, string Notes)` — Metadata entry describing a game ledger `act_XX` code.
  - `public static class ActCodes` — Reference catalog of ledger act codes:
    - `public const string Confirmed = "confirmed"`, `Inferred = "inferred"`, `Unknown = "unknown"`
    - `public static IReadOnlyList<ActCode> All { get; }` — Catalog of 26 sparse act-code entries across `act_01`–`act_36` (`act_03`, `act_06`–`act_10`, `act_25`–`act_26`, and `act_29`–`act_30` are absent; `act_12` has `Certainty = Inferred`, `act_21`–`act_23` have `Certainty = Unknown`, and the remaining 22 are `Confirmed`) seeded into `act_code`.
- **Incremental File Archivers ([LedgerArchiver.cs](./LedgerArchiver.cs), [JournalArchiver.cs](./JournalArchiver.cs))**
  - `public sealed record ArchiveResult(int Files, int FilesRead, int RowsSeen, int RowsAdded, IReadOnlyList<string> Problems)` — Summary of a `LedgerArchiver` pass (custom `ToString()`).
  - `public static class LedgerArchiver`
    - `public static ArchiveResult Run(LedgerDb db, string? root = null)` — Incrementally imports changed `<server>/<character>/Ledger/*.json` and `Ledger/Archive/*.json` files under `root ?? LedgerLocator.DefaultRoot()`.
  - `public sealed record JournalResult(int Files, int FilesRead, int NpcsAdded, int LinesAdded, IReadOnlyList<string> Problems)` — Summary of a `JournalArchiver` pass (custom `ToString()`).
  - `public static class JournalArchiver`
    - `public static JournalResult Run(LedgerDb db, string? root = null)` — Incrementally imports changed `<server>/<character>/journal/<NPC>` plain-text files under `root ?? LedgerLocator.DefaultRoot()` and invokes `db.MarkLootedNpcs()` when new NPCs or lines are added.
- **Typed Event Projection ([EventBuilder.cs](./EventBuilder.cs))**
  - `public sealed record BuildResult(int Scanned, int Added, bool Rebuilt)` — Summary of an `EventBuilder` pass.
  - `public static class EventBuilder`
    - `public const int Version = 1`
    - `public static BuildResult Update(LedgerDb db)` — Incrementally projects `Character`-kind rows from `ledger_row` (`act_11`, `act_12`, `act_15`, `act_16`, `act_20`, `act_24`, `act_27`, `act_28`) into `vendor_trade`, `harvest`, `item_move`, `trade`, and `trade_item` (or clears and rebuilds all five tables when `events_version` differs from `Version`, setting `Rebuilt = true` when a previous `events_version` was present).
- **History, NPC & Community Queries ([HistoryStore.cs](./HistoryStore.cs), [CommunityExport.cs](./CommunityExport.cs), [NpcStore.cs](./NpcStore.cs))**
  - `public sealed record HistoryCounts(int Vendor, int Harvest, int ItemMoves, int Trades)` ([HistoryStore.cs](./HistoryStore.cs)) — Per-table history row totals.
  - `public sealed record NpcSummary(string Name, int Lines, DateTime? First, DateTime? Last, bool LootedAsMonster)` ([NpcStore.cs](./NpcStore.cs)) — Aggregated NPC journal summary with local-time bounds.
  - `public sealed record NpcLine(DateTime? Time, string Character, string Speaker, string Verb, string Text)` ([NpcStore.cs](./NpcStore.cs)) — Single NPC journal dialogue entry with local timestamp.
  - `public sealed partial class HistoryStore(LedgerDb db)` — Read-only query facade split across [HistoryStore.cs](./HistoryStore.cs), [CommunityExport.cs](./CommunityExport.cs), and [NpcStore.cs](./NpcStore.cs):
    - `public IReadOnlyList<VendorRow> Vendor(string? server, string? character, Period? period = null)` ([HistoryStore.cs](./HistoryStore.cs)) — Queries `vendor_trade` filtered by optional server, character, and `[From, To)` period.
    - `public IReadOnlyList<VendorRow> VendorAll()` ([HistoryStore.cs](./HistoryStore.cs)) — Queries all `vendor_trade` rows across all servers and characters for the price book.
    - `public IReadOnlyList<HarvestRow> Harvests(string? server, string? character, Period? period = null)` ([HistoryStore.cs](./HistoryStore.cs)) — Queries `harvest`.
    - `public IReadOnlyList<ItemMoveRow> ItemMoves(string? server, string? character, Period? period = null)` ([HistoryStore.cs](./HistoryStore.cs)) — Queries `item_move`.
    - `public IReadOnlyList<TradeRow> Trades(string? server, string? character, Period? period = null)` ([HistoryStore.cs](./HistoryStore.cs)) — Queries `trade` left-joined with `trade_item`.
    - `public HistoryCounts Counts(string? server, string? character)` ([HistoryStore.cs](./HistoryStore.cs)) — Counts total rows across `vendor_trade`, `harvest`, `item_move`, and `trade`.
    - `public IReadOnlyList<DateOnly> Days(string? server, string? character)` ([HistoryStore.cs](./HistoryStore.cs)) — Distinct local calendar dates containing any history events, newest first.
    - `public IReadOnlyList<string> Servers()` ([CommunityExport.cs](./CommunityExport.cs)) — Distinct server names from `ledger_file` ordered case-insensitively.
    - `public IReadOnlyList<LootEvent> OwnLoot(string? server = null)` ([CommunityExport.cs](./CommunityExport.cs)) — Extracts `Character`-only item (`act_13`) and coin purse (`act_14`) loot events from `ledger_row`.
    - `public IReadOnlyList<ContributionName> OwnNames()` ([CommunityExport.cs](./CommunityExport.cs)) — Distinct `(name, id)` item pairs from `Character`-only `ledger_row` entries (`act_11`, `act_12`, `act_13`, `act_15`, `act_27`).
    - `public IReadOnlyList<NpcSummary> Npcs(string? server = null, string? character = null)` ([NpcStore.cs](./NpcStore.cs)) — Lists journal NPCs ordered by most recently heard first.
    - `public IReadOnlyList<NpcLine> NpcLines(string npc, string? server = null, string? character = null)` ([NpcStore.cs](./NpcStore.cs)) — Lists dialogue entries for `npc` (case-insensitive match), oldest first.
    - `public IReadOnlyList<ContributionNpc> OwnNpcDialogue()` ([NpcStore.cs](./NpcStore.cs)) — Exports anonymized, deduplicated NPC dialogue lines (preserving first-heard line order per NPC and sorting NPCs case-insensitively) for community sharing.
  - `public static class CommunityExport` ([CommunityExport.cs](./CommunityExport.cs))
    - `public static Contribution Build(LedgerDb db, string app, DateOnly? generated = null)` — Builds a `Contribution` aggregate across all servers in `db` using character-owned rows, item names, `db.ContributorId()`, and anonymized NPC dialogue.

## Invariants
- **Strict Dependency & NuGet Boundary**: [MnMCompanion.Data.csproj](./MnMCompanion.Data.csproj) is the **sole project** in the solution permitted to reference `Microsoft.Data.Sqlite` (enforced by [LedgerOnlyTests.cs](../../tests/MnMCompanion.Tests/LedgerOnlyTests.cs)). It references only [MnMCompanion.Core](../MnMCompanion.Core/EGM.md) and BCL assemblies, never references [MnMCompanion.Update](../MnMCompanion.Update/EGM.md) or [MnMCompanion](../MnMCompanion/EGM.md), and never performs network operations.
- **Append-Only Raw Persistence & Deterministic Deduplication**: Rows in `ledger_row` and `npc_line` are never deleted or mutated. `LedgerDb.Import` uses `INSERT OR IGNORE` over an uppercase hex SHA-256 `uid` computed from `$"{server}\n{character}\n{kind}\n{occurrence}\n{row.RawJson}"` (counting 0-based intra-file `occurrence` per `row.RawJson`). `LedgerDb.ImportJournal` uses `INSERT OR IGNORE` over an uppercase hex SHA-256 `uid` computed from `$"{server}\n{character}\n{file}\n{occurrence}\n{stamp}\n{line.Raw}"` where `stamp` is `""` when `line.Time is null` or `Iso(line.Time.Value)` otherwise, tracking 0-based intra-file `occurrence` per `stamp + "\n" + line.Raw` (`(stamp, line.Raw)` pair). Both guarantee idempotency across repeated reads and archive rotations while preserving genuinely identical entries within a single file.
- **Alternate Root Isolation**: Non-default game roots must be isolated via `LedgerDb.OtherFolder(ledgerRoot)` (12-char lowercase SHA-256 prefix of the normalized lowercase path) so test fixtures or borrowed ledgers are never imported into `LedgerDb.DefaultPath`.
- **Derived Table Rebuildability**: `vendor_trade`, `harvest`, `item_move`, `trade`, and `trade_item` are strictly reproducible projections of `kind = 'Character'` rows in `ledger_row`. `EventBuilder.Update` tracks watermarks via `meta['events_built_upto']` and `meta['events_version']`; changing `EventBuilder.Version` clears and rebuilds all five tables atomically.
- **Own-Data-Only Privacy & Anonymization**: `EventBuilder`, `CommunityExport.Build`, `HistoryStore.OwnLoot`, and `HistoryStore.OwnNames` process strictly `kind = 'Character'` rows (never `Social` rows, preventing double-counting and third-party player attribution). `HistoryStore.OwnNpcDialogue` replaces the listener character's name with `{player}`, omits timestamps and character names, and enforces `<= 80`-char NPC name and `ContributionBuilder` line/NPC caps; `LedgerDb.ContributorId()` stores a random GUID unrelated to machine or player identity.
- **Pre-Read Change Snapshot & Fault Tolerance**: `LedgerArchiver` and `JournalArchiver` capture `(file.Length, file.LastWriteTimeUtc)` *before* opening a file so files modified mid-read do not match stored state on the next pass. `LedgerArchiver` catches `IOException`, `UnauthorizedAccessException`, and partial-write `JsonException` per file (plus `IOException`/`UnauthorizedAccessException` on root enumeration), whereas `JournalArchiver` reads plain-text journal files and catches only `IOException` and `UnauthorizedAccessException`; both record failures in `Problems` without aborting the scan.
- **Thread Safety, Timezone Normalization & Purse Decoding**: All `LedgerDb` operations serialize over a single non-pooled `SqliteConnection` via private `lock (_gate)`. Stored timestamps use UTC ISO-8601 (`yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`), converting to local `DateTime`/`DateOnly` at `HistoryStore` read boundaries with half-open `[From, To)` period filtering. Purse coin (`act_14`) in `OwnLoot` must be decoded from `detail.d12` via `LedgerText.Packed` (100-base steps), never `detail.d11` (faulty 10-base encoding).

## Side Effects
- **SQLite Database & Directory Creation**: Constructing `LedgerDb` creates parent directories for `FilePath`, opens/creates the SQLite database file plus `-wal`/`-shm` sidecars (`Pooling = false`, `DefaultTimeout = 30`, `PRAGMA journal_mode = WAL`, `PRAGMA busy_timeout = 15000`), runs idempotent schema DDL, upserts the 26 `ActCodes.All` entries into `act_code`, and initializes `meta` (`schema_version`, `created_utc`).
- **Transactional SQLite Writes & Synchronization**: `LedgerDb.Import`, `LedgerDb.ImportJournal`, `LedgerDb.MarkLootedNpcs`, `EventBuilder.Update`, and first-call `LedgerDb.ContributorId` mutate SQLite tables inside `lock (_gate)` (blocking concurrent in-process callers on the same `LedgerDb` instance). `HistoryStore` methods perform read-only SQLite queries under `lock (_gate)`.
- **Non-Blocking Game File Reads**: `LedgerArchiver.Run` and `JournalArchiver.Run` traverse `<root>/<server>/<character>/Ledger` (plus `Archive`) and `journal` directories, opening files with `FileMode.Open`, `FileAccess.Read`, and `FileShare.ReadWrite | FileShare.Delete` so active game client writes and file rotations are never blocked; game files are never modified.
- **Zero Network I/O**: Performs strictly local filesystem reads and local SQLite database reads/writes.

## Verification
- Run: `dotnet test MnMCompanion.sln`
