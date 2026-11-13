# Journal

## Overview
Provides stateless parsing and player-name privacy scrubbing for exported or copied Monsters & Memories NPC journal logs (`namespace MnMCompanion.Journal` in [JournalParser.cs](./JournalParser.cs)). Splits raw multi-line journal text into structured dialogue entries and redacts player character names before data leaves the local machine.

## Interface
- `JournalLine(DateTime? Time, string Speaker, string Verb, string Text, string Raw)` (`sealed record` in [JournalParser.cs](./JournalParser.cs)): Parsed journal entry holding the local timestamp if stamped (`Time`), speaker name and lowercased speech verb when matching dialogue syntax (`Speaker`, `Verb`), unquoted dialogue body or raw text (`Text`), and normalized entry body without the timestamp prefix (`Raw`).
- `JournalParser` (`static partial class` in [JournalParser.cs](./JournalParser.cs)):
  - `Parse(string text) -> IReadOnlyList<JournalLine>`: Normalizes line endings, splits entries on `yyyy-MM-dd HH:mm:ss:` prefixes or blank lines, joins wrapped continuation lines with single spaces, and extracts speaker/verb/dialogue fields.
  - `WithoutPlayerName(string text, string player) -> string`: Replaces case-insensitive whole-word occurrences of trimmed `player` (when length `>= 2`) with `"{player}"`.

## Invariants
- Input nullability and short-circuiting: `Parse(text)` requires a non-null `text` and throws on `null`. `WithoutPlayerName(text, player)` requires a non-null `player` (throwing on `null` `player`, or on `null` `text` when `player.Trim().Length >= 2`), whereas when `player` is non-null with `player.Trim().Length < 2` it short-circuits and returns `text` unchanged without invoking `Regex.Replace`.
- Entry delimitation and continuation: Lines matching `^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}):\s?(.*)$` flush any buffered entry and start a new stamped entry; blank/whitespace lines flush the buffered entry and reset `Time` to `null`; other lines are trimmed and space-joined onto the open entry. Empty entries are discarded.
- Timestamp parsing: Matched prefixes are parsed via `DateTime.TryParseExact("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)`; syntactically matched prefixes with invalid calendar dates still delimit a new entry and strip the prefix, yielding `Time = null`.
- Dialogue extraction: Entries matching `^(.{1,60}?)\s+(says|whispers|shouts|tells you|yells),?\s+(.*)$` (`Singleline | IgnoreCase`) populate trimmed `Speaker` (1–60 chars), lowercased invariant `Verb`, and `Text` stripped of surrounding ASCII/curly double quotes (`"`, `“`, `”`); unmatched entries set `Speaker = ""`, `Verb = ""`, and `Text = Raw`.
- Regex compilation and thread safety: `Parse` uses stateless source-generated `[GeneratedRegex]` matchers (`Stamp()` and `Says()`), while `WithoutPlayerName` leaves `text` unchanged when `player.Trim().Length < 2` to avoid corrupting short words and otherwise calls `Regex.Replace` (`IgnoreCase | CultureInvariant`) with a runtime `Regex.Escape(player.Trim())` pattern bounded by `(?<!\w)...(?!\w)`. Both methods are stateless and thread-safe.

## Side Effects
- Pure in-memory string and regex computation with zero disk I/O, zero network calls, zero locks, and no shared mutable state.
- Reads the process local timezone (`TimeZoneInfo.Local`) via `DateTimeStyles.AssumeLocal` when parsing entry timestamps.

## Verification
- Run: `dotnet test MnMCompanion.sln`
