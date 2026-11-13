# History

## Overview
Provides domain row models, character/server and time-period filters, and statistical aggregators for non-loot ledger activity (`namespace MnMCompanion.History`), covering vendor buys/sells, resource harvesting, player-to-player trades, and ground/destroyed item movements across [HistoryRows.cs](./HistoryRows.cs), [HistoryScope.cs](./HistoryScope.cs), [HistoryView.cs](./HistoryView.cs), [SessionPeriods.cs](./SessionPeriods.cs), [HarvestSummary.cs](./HarvestSummary.cs), [ItemChangeSummary.cs](./ItemChangeSummary.cs), [TradeSummary.cs](./TradeSummary.cs), and [VendorSummary.cs](./VendorSummary.cs).

## Interface
- Ledger row and period models ([HistoryRows.cs](./HistoryRows.cs)):
  - `VendorDirection` (`enum : byte`): `Sold`, `Bought`.
  - `VendorRow(DateTime Time, string Character, VendorDirection Direction, string Item, int Qty, long TotalCopper, string Zone = "")` (`sealed record`): Vendor stack transaction (`act_24`/`act_28`) with per-unit `long Each { get; }`.
  - `HarvestRow(DateTime Time, string Character, string Item, int Qty, string Zone = "")` (`sealed record`): Gathering event (`act_27`).
  - `ItemMoveKind` (`enum : byte`): `Dropped`, `PickedUp`, `Destroyed`.
  - `ItemMoveRow(DateTime Time, string Character, ItemMoveKind Kind, string Item, int Qty, string Zone = "")` (`sealed record`): Ground/destroy item event (`act_11`/`act_12`/`act_15`).
  - `TradeLine(string Name, int Qty)` and `TradeRow(DateTime Time, string Character, bool Gave, string Partner, long Coin, IReadOnlyList<TradeLine> Items, string Zone = "")` (`sealed record`s): One side of a completed player trade (`act_16`/`act_20`).
  - `Period(string Label, DateTime? From, DateTime? To)` (`sealed record`): Half-open local interval `[From, To)` with `Contains(DateTime t) -> bool`, `static Of(DateOnly day, DateOnly today) -> Period`, and `static Presets(DateTime now) -> IReadOnlyList<Period>`.
- Scoping, session period conversion, and UI view state:
  - `HistoryScope` (`sealed record` in [HistoryScope.cs](./HistoryScope.cs)): Scope selector exposing `static readonly HistoryScope Everyone`, `string? Server { get; }`, `string? Character { get; }`, `bool IsEveryone { get; }`, `bool IsOneCharacter { get; }`, `string Label { get; }`, `string Key { get; }`, `static Of(string? server, string? character = null) -> HistoryScope`, `Includes(LedgerSource source) -> bool`, and `Pick(IEnumerable<LedgerSource> sources) -> IReadOnlyList<LedgerSource>`.
  - `HistoryView` (`sealed class` in [HistoryView.cs](./HistoryView.cs)): Mutable UI filter state with `HistoryView(DateTime? now = null)` constructor, `Period Period { get; }`, `string Filter { get; }`, `event Action? Changed`, `Set(Period period) -> void`, `SetFilter(string text) -> void`, and `Matches(params string[] fields) -> bool`.
  - `SessionPeriods` (`static class` in [SessionPeriods.cs](./SessionPeriods.cs)): `Of(DateOnly day, LootSession? session) -> Period`.
- Gathering, discard, trade, and vendor summaries:
  - [HarvestSummary.cs](./HarvestSummary.cs): `HarvestItem(string Item, int Harvests, int Units, IReadOnlyList<string> Zones, DateTime First, DateTime Last)`, `HarvestTotals(int Harvests, int Units, int Kinds, TimeSpan Active)` (with `double? PerHour { get; }`), and `HarvestSummary` (`MaxGap = 10m`, `Active(IEnumerable<HarvestRow> rows) -> TimeSpan`, `Totals(IEnumerable<HarvestRow> rows) -> HarvestTotals`, `ByItem(IEnumerable<HarvestRow> rows) -> IReadOnlyList<HarvestItem>`).
  - [ItemChangeSummary.cs](./ItemChangeSummary.cs): `ItemChangeTotals(int Dropped, int PickedUp, int Destroyed, int TakenBack)`, `DiscardedItem(string Item, int Dropped, int Destroyed, int PickedUp)` (with `int Lost { get; }`), and `ItemChangeSummary` (`TakeBackWindow = 10m`, `Totals(IEnumerable<ItemMoveRow> rows) -> ItemChangeTotals`, `TakenBackByItemAndZone(IEnumerable<ItemMoveRow> rows) -> IReadOnlyDictionary<(string Item, string Zone), int>`, `MostDiscarded(IEnumerable<ItemMoveRow> rows) -> IReadOnlyList<DiscardedItem>`).
  - [TradeSummary.cs](./TradeSummary.cs): `TradeEntry(DateTime Time, string Character, string Partner, long GaveCoin, IReadOnlyList<TradeLine> GaveItems, long ReceivedCoin, IReadOnlyList<TradeLine> ReceivedItems, string Zone = "")` (with `int GaveCount { get; }` and `int ReceivedCount { get; }`) and `TradeSummary` (`SameTrade = 5s`, `Pair(IEnumerable<TradeRow> rows) -> IReadOnlyList<TradeEntry>`, `Label(string partner) -> string`, `Describe(long coin, IReadOnlyList<TradeLine> items) -> string`).
  - [VendorSummary.cs](./VendorSummary.cs): `VendorTotals(long SoldCopper, int SoldUnits, int SoldStacks, long BoughtCopper, int BoughtUnits, int BoughtStacks, string BestSeller, long BestSellerCopper)` (with `long Net { get; }`), `VendorItem(string Item, int Stacks, int Units, long Total, long Each, DateTime Last)`, `PriceEntry(string Item, int Samples, long Median, long Min, long Max, int Units, DateTime Last, int BoughtSamples, long BoughtMedian)`, and `VendorSummary` (`Totals(IEnumerable<VendorRow> rows) -> VendorTotals`, `ByItem(IEnumerable<VendorRow> rows, VendorDirection direction) -> IReadOnlyList<VendorItem>`, `PriceBook(IEnumerable<VendorRow> all) -> IReadOnlyList<PriceEntry>`, `Median(IEnumerable<long> prices) -> long`).

## Invariants
- Scope hierarchy: `HistoryScope.Of` guarantees `Character` is never non-null when `Server` is null (`server == null` always resolves to `HistoryScope.Everyone`); `Server` and `Character` comparisons use `OrdinalIgnoreCase`.
- Half-open local intervals: `Period` treats `From` as inclusive and `To` as exclusive (null bounds are unbounded); `SessionPeriods.Of` clamps a `LootSession` to `[day 00:00:00, day+1 00:00:00)`.
- Safe arithmetic and clamping: `VendorRow.Each` divides by `Math.Max(1, Qty)`; `HarvestTotals.PerHour` returns `null` when `Active < 1 minute`; `DiscardedItem.Lost` clamps `Dropped + Destroyed - PickedUp` to `>= 0`.
- Vendor zero-copper handling and medians: `VendorSummary.PriceBook` excludes rows with `TotalCopper <= 0` completely (omitting items with no positive-copper sales or purchases), whereas `VendorSummary.ByItem` excludes `TotalCopper <= 0` only when computing `Each` median—zero-copper rows still increment `Stacks`, `Units`, `Total`, and `Last` (yielding `Each = 0` when all rows have `TotalCopper <= 0`). `VendorSummary.Median` returns `0` for empty sequences or `sorted[count / 2]` (upper-middle) for even counts.
- Order-independent time windowing: Summary methods sort inputs internally—`HarvestSummary.Active` sums consecutive gaps `<= 10m`; `ItemChangeSummary.TakenBackByItemAndZone` matches `PickedUp` to prior `Dropped` rows per lowercased `(Character, Item)` in FIFO order within `10m`; `TradeSummary.Pair` clusters consecutive rows per `(Character.ToLowerInvariant(), Partner)` within `5s` and returns trades newest-first.
- Concurrency and dependencies: All types are immutable records or stateless helpers except `HistoryView`, which is single-threaded UI state and suppresses `Changed` when `Period` or trimmed `Filter` does not change. Depends only on `MnMCompanion.Ledger` and `MnMCompanion.Loot`.

## Side Effects
- Pure in-memory, zero-I/O component: performs no disk, network, database, environment, or lock operations.
- `HistoryView(DateTime? now = null)` reads `DateTime.Now` only when `now` is `null`; `Set` and `SetFilter` mutate instance fields and synchronously invoke `Changed` subscribers.

## Verification
- Run: `dotnet test MnMCompanion.sln`
