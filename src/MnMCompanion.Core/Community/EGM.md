# Community

## Overview
Implements opt-in community data consent rules, schema-versioned renewal prompts, anonymized contribution payload construction (`Schema = 4`), SHA-256 change fingerprinting, and upload cadence scheduling (`namespace MnMCompanion.Community`) across [CommunityPolicy.cs](./CommunityPolicy.cs), [Contribution.cs](./Contribution.cs), and [SharePlan.cs](./SharePlan.cs). Aggregates a player's own character loot, vendor transactions, harvests, trades, discards, item IDs, and redacted NPC dialogue into standalone JSON documents without performing disk or network I/O.

## Interface
- `CommunityPolicy` (`static class` in [CommunityPolicy.cs](./CommunityPolicy.cs)): Central policy for community data consent, schema renewal, startup prompting, and status strings:
  - `MayUseCommunityData(bool sharing) -> bool` and `MaySend(bool sharing) -> bool`
  - `Consented(bool yes, int answeredSchema, int currentSchema) -> bool` and `NeedsRenewal(bool yes, int answeredSchema, int currentSchema) -> bool`
  - `NewSince(int answeredSchema, int currentSchema) -> IReadOnlyList<string>` and `RenewalNote(int answeredSchema, int currentSchema) -> string`
  - `ShouldAsk(bool sharing, bool askingIsAllowed) -> bool`
  - `Status(bool sharing, bool sendingConnected, string? note = null) -> string`
- `Contribution` (`sealed record` in [Contribution.cs](./Contribution.cs)):
  - `Contribution(int Schema, string Contributor, string App, string Generated, string License, ContributionTotals Totals, IReadOnlyList<ContributionDrop> Drops, IReadOnlyList<ContributionPrice> Prices, IReadOnlyList<ContributionHarvest> Harvest, IReadOnlyList<ContributionName> Items, IReadOnlyList<ContributionNpc> Npcs, ContributionCounts Counts, IReadOnlyList<ContributionTrade> Trades, IReadOnlyList<ContributionDiscard> Discards)`
  - Part constants: `NpcPart = "npcs"`, `TradesPart = "trades"`
  - JSON codecs: `ToJson() -> string`, `ToCoreJson(bool indented = false) -> string`, `ToNpcPartJson(bool indented = false) -> string`, `ToTradesPartJson(bool indented = false) -> string`, and `static FromJson(string json) -> Contribution?`
- Contribution payload and input records ([Contribution.cs](./Contribution.cs)):
  - `ContributionItem(string Item, int Corpses, int Units)` and `ContributionDrop(string Mob, string Zone, int Corpses, int Purses, long Copper, IReadOnlyList<ContributionItem> Items, string Server = "")`
  - `ContributionPrice(string Item, string Zone, IReadOnlyDictionary<string, int> Sold, IReadOnlyDictionary<string, int> Bought, string Server = "")`
  - `ContributionHarvest(string Item, string Zone, int Harvests, int Units, string Server = "")`
  - `ContributionName(string Name, string Id)` and `ContributionNpc(string Name, IReadOnlyList<string> Lines)`
  - `ContributionTradeItem(string Item, int Qty)`, `ContributionTradeSide(long Coin, IReadOnlyList<ContributionTradeItem> Items)`, and `ContributionTrade(string Day, string Zone, ContributionTradeSide Gave, ContributionTradeSide Got, string Server = "")`
  - `ContributionDiscard(string Item, string Zone, int Dropped, int Destroyed, int TakenBack, string Server = "")`
  - `ContributionTotals(int Monsters, int Corpses, int Sales, int Purchases, int Harvests, int Items)`
  - `CountRow(string? Week, long Corpses, long Purses, long Copper, long Units, long Harvests, long HarvestUnits, long Sold, long SoldCopper, long Bought, long BoughtCopper, long Trades)` (`Week` uses `JsonIgnoreCondition.WhenWritingNull`; exposes computed `[JsonIgnore] bool IsEmpty { get; }`)
  - `ContributionCounts(CountRow Before, IReadOnlyList<CountRow> Weeks)`
  - `ServerRows(string Server, IReadOnlyList<LootEvent> Loot, IReadOnlyList<VendorRow> Vendor, IReadOnlyList<HarvestRow> Harvest, IReadOnlyList<TradeRow> Trades, IReadOnlyList<ItemMoveRow> ItemMoves)`
- `ContributionBuilder` (`static class` in [Contribution.cs](./Contribution.cs)): Builds validated, sanitized, deterministically sorted `Contribution` documents from `MnMCompanion.Loot` and `MnMCompanion.History` rows (`Schema = 4`, `UnknownServer = "unknown"`, `MaxWeeks = 53`, `License = "CC-BY-NC-SA-4.0"`, `MaxTrades = 3000`, `MaxTradeItems = 40`, `MaxTradeQty = 9999`, `MaxDiscards = 5000`, `MaxTradeCoin = 1_000_000_000_000L`, `MaxLine = 2000`, `MaxLinesPerNpc = 500`, `MaxNpcs = 5000`):
  - `Build(IEnumerable<LootEvent> ownLoot, IEnumerable<VendorRow> vendor, IEnumerable<HarvestRow> harvest, IEnumerable<ContributionName> names, string contributor, string app, DateOnly generated, IEnumerable<ContributionNpc>? npcs = null, IEnumerable<TradeRow>? trades = null, IEnumerable<ItemMoveRow>? itemMoves = null, string server = UnknownServer) -> Contribution`
  - `Build(IEnumerable<ServerRows> servers, IEnumerable<ContributionName> names, string contributor, string app, DateOnly generated, IEnumerable<ContributionNpc>? npcs = null) -> Contribution`
  - `WeekStart(DateOnly day) -> DateOnly`
  - `Lifetime(ContributionCounts counts) -> CountRow`
  - `ServersOf(Contribution c) -> IReadOnlyList<string>`
  - `Describe(Contribution c) -> IReadOnlyList<string>`
- `ShareStep` (`enum` in [SharePlan.cs](./SharePlan.cs)): Upload loop decision (`Wait`, `NothingToShare`, `Unchanged`, `Send`).
- `SharePlan` (`static class` in [SharePlan.cs](./SharePlan.cs)): Upload cadence (`Every = 6h5m`, `Refresh = 7d`, `AfterFailure = 30m`, `AfterRefusal = 6h`), SHA-256 content hashing, and upload gating:
  - `Due(bool sharing, DateTime now, DateTime? lastSent, DateTime? notBefore) -> bool`
  - `Nothing(Contribution file) -> bool`
  - `Fingerprint(Contribution file) -> string`, `NpcFingerprint(Contribution file) -> string`, `TradesFingerprint(Contribution file) -> string`
  - `Decide(Contribution file, string? lastFingerprint, DateTime now, DateTime? lastSent) -> ShareStep`
  - `DecidePart(Contribution file, string? lastFingerprint, DateTime now, DateTime? lastSent) -> ShareStep`
  - `DecideTrades(Contribution file, string? lastFingerprint, DateTime now, DateTime? lastSent) -> ShareStep`

## Invariants
- Symmetric opt-in and schema-bound consent: Both consuming public community datasets and uploading require `sharing == true`. Prior consent (`answeredSchema`, clamped to `>= 1`) becomes invalid whenever `currentSchema > Math.Max(1, answeredSchema)`, pausing sharing until renewed.
- Headless prompt suppression: Callers in headless/CLI/export flows must pass `askingIsAllowed: false` to `CommunityPolicy.ShouldAsk` so execution never blocks on a consent dialog.
- Own-rows-only input: Callers of `ContributionBuilder.Build` must pass only the player's own `Character` ledger rows (never party `Social` rows) to prevent multi-counting group drops.
- Anonymization and privacy: Payloads omit character names (NPC lines expect `{player}` scrubbing beforehand), trade partner IDs, vendor names, and sub-day timestamps; `Contributor` must be a random PC-local identifier so re-uploads replace prior snapshots.
- Schema 4 server attribution and filtering rules: Activity rows (`Drops`, `Prices`, `Harvest`, `Trades`, `Discards`) carry normalized server names (`ServerName` trims and truncates strings `> 80` chars to `80` chars, returning `"unknown"` only when empty or containing control characters; trade and discard zones apply the same truncation and `"unknown"` fallback), while `Items` and `Npcs` remain server-agnostic. Vendor rows require `TotalCopper > 0` and non-blank `Item` across `Prices`, `Totals`, and `ContributionCounts`, and harvest rows require non-blank `Item` across all three; however, `Drops` and `Totals` (`Monsters`, `Corpses`) include all `c.Mob` corpses with non-blank `Source` (including `LootExtractor.UnknownCreature`), whereas `ContributionCounts` excludes `c.Source == LootExtractor.UnknownCreature`, and `ContributionCounts.Trades` deduplicates raw `serverTrades` by `(Partner, Time.Ticks / TimeSpan.TicksPerMinute)` without applying `TradesOf`'s `5s` pairing window, non-empty side check, `2025-01-01` date floor, or `MaxTrades = 3000` cap.
- Payload bounds: Enforces rolling `MaxWeeks = 53` Monday-UTC buckets (older folded into `Before`, future clamped to `generated` week), `MaxTrades = 3000` (from `2025-01-01`, `<= 40` items/side, `<= 9999` qty, `<= 10^12` copper), `MaxDiscards = 5000` (`TakenBack <= Dropped`), `MaxNpcs = 5000`, `MaxLinesPerNpc = 500` (`<= 2000` chars), `<= 80`-char truncated names, and 24-char lowercase ASCII hex item IDs.
- Date-insensitive multi-part fingerprinting: Core, `npcs`, and `trades` JSON parts hash with `Generated` cleared to `""` so calendar-day rollovers do not trigger redundant uploads within the 7-day `Refresh` window.

## Side Effects
- Pure in-memory computation (`System.Text.Json` serialization/deserialization and `SHA256.HashData` hashing) with zero disk I/O, zero network requests, zero ambient clock or environment reads, zero locks, and no shared mutable state.

## Verification
- Run: `dotnet test MnMCompanion.sln`
