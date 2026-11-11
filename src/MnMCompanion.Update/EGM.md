# MnMCompanion.Update

## Overview
`MnMCompanion.Update` (`net10.0`, namespace `MnMCompanion.Update`, defined in [MnMCompanion.Update.csproj](./MnMCompanion.Update.csproj)) is the isolated outbound HTTPS and release-management library for MnM Companion. It encapsulates GitHub Releases discovery, SHA-256-verified self-update binary downloads and atomic executable replacement, silent-update and What's New policy evaluation, embedded `CHANGELOG.md` parsing, community contribution/suggestion/query HTTP calls, conditional ETag fetches for public market and tradeskill feeds, and offline zone-to-map URL mapping. It is the sole project in the solution permitted to reference `System.Net` / `HttpClient` in C# code or declare `https://` URL literals in `.cs` source files (enforced by [LedgerOnlyTests.cs](../../tests/MnMCompanion.Tests/LedgerOnlyTests.cs)), whereas runtime map browsing over HTTPS is performed by the embedded `WebView2` control in [MnMCompanion](../MnMCompanion/EGM.md) using `Atlas` URLs when `Settings.MapsOnline` is enabled. It is built strictly on the .NET Base Class Library with zero NuGet packages and zero references to sibling projects.

## Interface
All public types reside in `namespace MnMCompanion.Update`:

- **Self-Update & Version Utilities ([UpdateService.cs](./UpdateService.cs))**
  - `public sealed record UpdateOffer(Version Version, string Tag, string Notes, string PageUrl, string ExeUrl, string ChecksumUrl, long Size)` — Metadata and verified GitHub asset URLs for a newer release.
  - `public enum CheckOutcome { UpToDate, Available, NoReleaseInfo, Failed }` — Outcome category for release checks.
  - `public sealed record CheckResult(CheckOutcome Outcome, string Message, UpdateOffer? Offer = null)` — Result of checking GitHub Releases.
  - `public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner)` — Human-readable error raised when update download, verification, or executable replacement fails.
  - `public static class AppVersion` — Four-part version normalization and formatting:
    - `static Version Normalize(Version v)` — Clamps `Major`, `Minor`, `Build`, and `Revision` to `>= 0` so 3-part and 4-part versions compare consistently.
    - `static Version? Parse(string? text)` — Parses `"v?major.minor[.build][.revision]"` (ignoring prerelease/build suffixes) or returns `null`.
    - `static Version Current(Assembly? assembly = null)` — Reads `AssemblyInformationalVersionAttribute` (defaulting to `0.0.0.0`).
    - `static string Show(Version v)` — Formats 4 parts when `Revision > 0`, otherwise `"Major.Minor.Build"`.
  - `public sealed class UpdateService : IDisposable` — GitHub release checker, verified downloader, and atomic self-updater:
    - Constants: `public const string Owner = "ExploreTheFloor"`, `Repo = "MnMCompanion"`, `ExeName = "MnMCompanion.exe"`, `ChecksumName = "MnMCompanion.exe.sha256"`.
    - Static URL properties: `public static string ProjectUrl { get; }` (`"https://github.com/ExploreTheFloor/MnMCompanion"`), `ReleasesUrl { get; }` (`ProjectUrl + "/releases/latest"`), `LatestUrl { get; }` (`"https://api.github.com/repos/ExploreTheFloor/MnMCompanion/releases/latest"`).
    - Instance properties & constructor: `public long MinimumBytes { get; init; } = 1_000_000`, `public Version Current { get; }`, `public UpdateService(Version current, HttpMessageHandler? handler = null)`.
    - `Task<CheckResult> CheckAsync(CancellationToken cancel = default)` — Fetches `LatestUrl` (15s timeout) and delegates 2xx JSON to `Interpret`, catching `HttpRequestException`, timeout `OperationCanceledException`, and `JsonException` into `CheckOutcome.Failed`.
    - `CheckResult Interpret(string json)` — Parses release JSON (throwing `JsonException` if `json` is malformed or its root element is not a JSON object), treating drafts/prereleases and versions `<= Current` as `UpToDate`, returning `Failed` on unparseable `tag_name`, and requiring both `.exe` and `.sha256` assets under `ProjectUrl + "/releases/download/"` (falling back `PageUrl` to `ReleasesUrl` if `html_url` is outside `ProjectUrl`).
    - `Task<string> DownloadAsync(UpdateOffer offer, string folder, IProgress<double>? progress = null, CancellationToken cancel = default)` — Downloads `.sha256` first (30s timeout, `<= 4096` chars, 64-hex token) and then streams `.exe.part` (10m timeout), enforcing post-redirect GitHub HTTPS hosts, `MZ` PE header, size `>= MinimumBytes`, and exact SHA-256 digest match before renaming to `MnMCompanion-<ver>.exe`.
    - `static bool CanReplace(string? exe)` — Verifies `exe` exists, ends with `.exe`, is not `dotnet.exe`, and resides in a writable directory.
    - `static void Apply(string downloaded, string currentExe, Func<string, bool> launch)` — Renames `currentExe` to `<currentExe>.old`, moves `downloaded` into `currentExe`, invokes `launch(currentExe)`, and rolls back `.old` + throws `UpdateException` on failure.
    - `static bool WaitForPreviousToExit(string? currentExe, TimeSpan timeout)` — Polls deletion of `<currentExe>.old` until unlocked or `timeout` elapses.
    - `static void CleanUp(string? currentExe, string downloadFolder)` — Best-effort deletes `<currentExe>.old` and recursively removes `downloadFolder`.
    - `void Dispose()` — Disposes the internal `HttpClient`.

- **Silent Update & Restart Policy ([UpdatePolicy.cs](./UpdatePolicy.cs))**
  - `public static class UpdatePolicy`:
    - `static bool InstallWithoutAsking(bool installAutomatically, bool canInstall, UpdateOffer offer, string? skippedVersion, string? lastInstalledVersion, bool declinedThisRun)` — Permits silent background installation only when automatic updates and replacement are enabled, the user has not declined during this session, and `AppVersion.Show(offer.Version)` matches neither `skippedVersion` nor `lastInstalledVersion`.
    - `static bool MayRestart(bool somethingRunning, bool restartRequested)` — Blocks automatic restart while active timers/countdowns are running unless explicitly requested by the user.

- **Release Notes Parser & What's New Policy ([ReleaseNotes.cs](./ReleaseNotes.cs))**
  - `public sealed record ReleaseEntry(Version Version, DateOnly Date, IReadOnlyList<string> Lines)` — Parsed `CHANGELOG.md` release entry.
  - `public abstract record NotesPiece(string Text)` with sealed subtypes `PlainPiece(string Text)`, `BoldPiece(string Text)`, `CodePiece(string Text)`, and `LinkPiece(string Text, string Url)` — Inline Markdown tokens for UI rendering.
  - `public static class ReleaseNotes`:
    - `static IReadOnlyList<ReleaseEntry> Parse(string changelog)` — Parses `## x.y.z (yyyy-mm-dd)` sections and joins indented continuation lines onto the preceding non-empty line.
    - `static IReadOnlyList<ReleaseEntry> Between(IReadOnlyList<ReleaseEntry> entries, Version? after, Version upTo)` — Filters entries in `(after, upTo]` (or `== upTo` when `after` is `null`), sorted newest-first.
    - `static bool IsBullet(string line, out string rest)` — Detects `- ` / `* ` list items and outputs trimmed item text.
    - `static IReadOnlyList<NotesPiece> Pieces(string line, string site)` — Tokenizes inline bold, code, and Markdown links, resolving relative targets via `Absolute`.
    - `static string Absolute(string url, string site)` — Preserves `http(s)` links and resolves `README.md` or repository-relative paths (`<site>/blob/main/...`) against `site`.
  - `public static class WhatsNewPolicy`:
    - `static bool ShouldShow(string? shownVersion, bool freshInstall, Version current)` — When `shownVersion is null`, returns `!freshInstall` (suppressing What's New on a brand-new install without `settings.json`, while showing it when upgrading an existing install that predates `NotesShownVersion`); otherwise returns `true` when `shownVersion` parses and `AppVersion.Normalize(current) > AppVersion.Normalize(shown)`.

- **Community Contribution, Suggestion & Query Client ([CommunityClient.cs](./CommunityClient.cs), [Suggestion.cs](./Suggestion.cs))**
  - `public static class Suggestion` ([Suggestion.cs](./Suggestion.cs)) — Input sanitizer with `public const int MaxChars = 2000` and `public static string Clean(string? text)` (normalizes `\r\n`/`\r` to `\n`, strips control characters and `\u007f` except `\n` and `\t`, trims, and truncates to `MaxChars` with trailing whitespace trimmed).
  - `public static class CommunityService` ([CommunityClient.cs](./CommunityClient.cs)) — Defines `public const string BaseUrl = "https://mnm-intake.mnm-companion.workers.dev"`.
  - `public enum SendOutcome { Sent, TooSoon, Busy, Refused, Failed }` and `public sealed record SendResult(SendOutcome Outcome, string Message, TimeSpan? RetryAfter = null)`.
  - `public enum AskOutcome { Ok, NotAllowed, TooMany, Failed }` and `public sealed record Asked<T>(AskOutcome Outcome, T? Data, string Message) where T : class`.
  - Response records ([CommunityClient.cs](./CommunityClient.cs)): `CommunityDropItem(string Item, double Share, double UnitsPerTake, int Takers)`, `CommunityDropZone(string Zone, int Corpses, int Purses, long Copper, int Contributors, IReadOnlyList<CommunityDropItem> Items)`, `CommunityDrops(string Mob, IReadOnlyList<CommunityDropZone> Zones)`, `CommunityPrice(string Direction, long Median, long Low, long High, int Stacks, int Contributors)`, `CommunityPrices(string Item, IReadOnlyList<CommunityPrice> Prices)`, `CommunityYield(string Zone, double PerHarvest, int Harvests, int Contributors)`, `CommunityHarvests(string Item, IReadOnlyList<CommunityYield> Zones)`, `CommunityNpcLine(string Text, int Contributors)`, `CommunityNpc(string Npc, bool AlsoMonster, IReadOnlyList<CommunityNpcLine> Lines)`.
  - `public sealed class CommunityClient : IDisposable` ([CommunityClient.cs](./CommunityClient.cs)):
    - `CommunityClient(Version app, HttpMessageHandler? handler = null, string? baseUrl = null)`
    - `Task<SendResult> SendAsync(string json, CancellationToken cancel = default)` — Posts prebuilt `json` directly to `POST /v1/contribution` without client-side contributor-ID validation.
    - `Task<SendResult> SuggestAsync(string contributor, string text, Version app, CancellationToken cancel = default)` — Validates `contributor` against `^[0-9a-f]{32}$` and requires non-empty `Suggestion.Clean(text)`, then posts `{ contributor, app, text }` to `POST /v1/suggestion`.
    - `Task<Asked<CommunityDrops>> DropsAsync(string contributor, string mob, string? zone = null, CancellationToken cancel = default)` — `GET /v1/community/drop` (validating `contributor` and `mob`).
    - `Task<Asked<CommunityPrices>> PricesAsync(string contributor, string item, CancellationToken cancel = default)` — `GET /v1/community/price` (validating `contributor` and `item`).
    - `Task<Asked<CommunityHarvests>> HarvestsAsync(string contributor, string item, CancellationToken cancel = default)` — `GET /v1/community/harvest` (validating `contributor` and `item`).
    - `Task<Asked<CommunityNpc>> NpcAsync(string contributor, string name, CancellationToken cancel = default)` — `GET /v1/community/npc` (validating `contributor` and `name`).
    - `void Dispose()` — Disposes the internal `HttpClient`.

- **Market Feed, Recipe Wiki & Atlas Helpers ([MarketFeedClient.cs](./MarketFeedClient.cs), [RecipeWikiClient.cs](./RecipeWikiClient.cs), [Atlas.cs](./Atlas.cs))**
  - [MarketFeedClient.cs](./MarketFeedClient.cs):
    - `public static class MarketSite` — `public const string Origin = "https://mnm-db.com"`, `public const string FeedUrl = "https://mnm-db.com/auctions.json"`, `public const string PageUrl = "https://mnm-db.com/#/auctions"` (browser-launch URL), and `public static string ItemUrl(string item)` (`"https://mnm-db.com/#/auctions/<escaped-item>"`).
    - `public sealed record MarketDownload(bool Ok, string Json, string Message, bool NotModified = false, string? ETag = null, TimeSpan? RetryAfter = null)`.
    - `public sealed class MarketFeedClient : IDisposable` — `MarketFeedClient(Version app, HttpMessageHandler? handler = null, string? url = null)`, `Task<MarketDownload> FetchAsync(string? etag = null, CancellationToken cancel = default)`, `void Dispose()`.
  - [RecipeWikiClient.cs](./RecipeWikiClient.cs):
    - `public static class RecipeWiki` — `public const string Origin = "https://monstersandmemories.wiki"`, `public const string DataUrl = "https://monstersandmemories.wiki/api/tradeskills"`, and `public const string PageUrl = "https://monstersandmemories.wiki/tradeskills"` (browser-launch URL).
    - `public sealed record RecipeDownload(bool Ok, string Json, string Message, bool NotModified = false, string? ETag = null, TimeSpan? RetryAfter = null)`.
    - `public sealed class RecipeWikiClient : IDisposable` — `RecipeWikiClient(Version app, HttpMessageHandler? handler = null, string? url = null)`, `Task<RecipeDownload> FetchAsync(string? etag = null, CancellationToken cancel = default)`, `void Dispose()`.
  - [Atlas.cs](./Atlas.cs):
    - `public static class Atlas` — `public const string Origin = "https://www.mnmatlas.com"`, `public static string? UrlFor(string zone)`, and `public static string? EmbedUrlFor(string zone)` (pure slug lookup mapping 29 normalized lowercase-alpha zone keys across 27 atlas maps without network I/O).

## Invariants
- **Strict Dependency & Network Boundary**: [MnMCompanion.Update.csproj](./MnMCompanion.Update.csproj) must remain BCL-only with zero NuGet `PackageReference` entries and zero `ProjectReference` dependencies (never referencing [MnMCompanion.Core](../MnMCompanion.Core/EGM.md), [MnMCompanion.Data](../MnMCompanion.Data/EGM.md), or [MnMCompanion](../MnMCompanion/EGM.md)). Enforced by [LedgerOnlyTests.cs](../../tests/MnMCompanion.Tests/LedgerOnlyTests.cs), it is the sole project permitted to reference `System.Net` / `HttpClient` in C# code or declare external `https://` host literals in `.cs` files (while embedded `WebView2` map navigation at runtime occurs in [MnMCompanion](../MnMCompanion/EGM.md) using `Atlas` URLs when `Settings.MapsOnline` is opted into).
- **Pinned HTTPS Origins & Redirect Controls**: `UpdateService` only accepts release assets prefixed with `https://github.com/ExploreTheFloor/MnMCompanion/releases/download/` and enforces post-redirect HTTPS hosts restricted to `github.com`, `*.github.com`, or `*.githubusercontent.com` (maximum 5 redirects). `CommunityClient`, `MarketFeedClient`, and `RecipeWikiClient` disable automatic redirects (`AllowAutoRedirect = false`), and the feed clients disable cookies (`UseCookies = false`) with automatic decompression enabled.
- **Mandatory SHA-256 & PE Verification Before Replacement**: `UpdateService.DownloadAsync` must verify a valid 64-hex SHA-256 checksum document (`<= 4096` chars, 30s timeout), Windows PE `"MZ"` header bytes, file size `>= MinimumBytes`, and exact SHA-256 digest match before promoting `.exe.part` to `MnMCompanion-<ver>.exe`; any mismatch or download error deletes `.exe.part` and throws `UpdateException`.
- **Safe Executable Replacement & Rollback**: `UpdateService.CanReplace` forbids replacing missing binaries, non-`.exe` files, `dotnet.exe` test/dev hosts, or binaries in unwritable directories. `UpdateService.Apply` preserves `<currentExe>.old` and restores it if moving the new binary fails or `launch(currentExe)` returns `false` or throws.
- **Restart Loop & Active Session Protection**: `UpdatePolicy.InstallWithoutAsking` blocks silent installs when `offer.Version` matches `skippedVersion` or `lastInstalledVersion`, preventing infinite update loops if a release tag mismatches its embedded assembly version. `UpdatePolicy.MayRestart` never interrupts active timers unless `restartRequested` is `true`.
- **Error Handling, Input Validation & Payload Caps**: `UpdateService.CheckAsync`, `CommunityClient` methods, `MarketFeedClient.FetchAsync`, and `RecipeWikiClient.FetchAsync` catch transport and timeout exceptions (unless `cancel.IsCancellationRequested`) and return structured outcome records (`UpdateService.Interpret` itself throws `JsonException` on malformed or non-object JSON, which `CheckAsync` catches). In `CommunityClient`, `SuggestAsync` and `Ask<T>` (`DropsAsync`, `PricesAsync`, `HarvestsAsync`, `NpcAsync`) validate `contributor` against `^[0-9a-f]{32}$` before sending (whereas `SendAsync` forwards raw JSON directly), and `Ask<T>` additionally rejects trimmed query names of length `0` or `> 80`. Response bodies are strictly bounded: `64,000` bytes for non-2xx error JSON in `CommunityClient.SendAsync`/`SuggestAsync` (`ReadError`, parsing `"error"` and up to 3 `"problems"`), `2,000,000` bytes for `200` answers in `CommunityClient` (`Ask<T>`), `40,000,000` bytes with leading `"{"` and `"listings"` check for `MarketFeedClient`, and `8,000,000` bytes with leading `"{"` and `"tradeskills"` check for `RecipeWikiClient`.
- **Offline Determinism for Pure Helpers**: `Atlas`, `Suggestion`, `UpdatePolicy`, `ReleaseNotes`, `WhatsNewPolicy`, `AppVersion`, `MarketSite.PageUrl`/`ItemUrl`, and `RecipeWiki.PageUrl` are pure in-memory helpers that perform zero network or filesystem I/O.

## Side Effects
- **Outbound HTTPS Requests**:
  - `UpdateService`: `GET https://api.github.com/repos/ExploreTheFloor/MnMCompanion/releases/latest` (15s timeout) and `GET` requests to `https://github.com/ExploreTheFloor/MnMCompanion/releases/download/...` (following up to 5 HTTPS redirects to GitHub CDN hosts; 30s timeout for `.sha256` checksum fetch followed by 10m timeout for `.exe` stream).
  - `CommunityClient`: `POST /v1/contribution`, `POST /v1/suggestion` (triggered explicitly by user action regardless of `Settings.Sharing`), and `GET /v1/community/{drop,price,harvest,npc}` (with `X-Contributor-Id` header) against `https://mnm-intake.mnm-companion.workers.dev` (60s timeout).
  - `MarketFeedClient`: Conditional `GET https://mnm-db.com/auctions.json` with optional `If-None-Match` header (60s timeout).
  - `RecipeWikiClient`: Conditional `GET https://monstersandmemories.wiki/api/tradeskills` with optional `If-None-Match` header (45s timeout).
- **Local Filesystem Mutations (`UpdateService` only)**: Creates the update staging directory, writes and renames/deletes `MnMCompanion-<ver>.exe.part` and `MnMCompanion-<ver>.exe`, probes directory write access via temporary `.write-test-<guid>` files in `CanReplace`, renames/restores `<currentExe>.old` in `Apply`, polls deletion of `<currentExe>.old` with 100ms sleeps in `WaitForPreviousToExit`, and deletes `<currentExe>.old` plus `downloadFolder` recursively in `CleanUp`.
- **Process Launch Callback & Socket Disposal**: `UpdateService.Apply` invokes the caller-supplied `launch(currentExe)` delegate to spawn the updated executable; `Dispose()` on `UpdateService`, `CommunityClient`, `MarketFeedClient`, and `RecipeWikiClient` releases underlying `HttpClient` resources.

## Verification
- Run: `dotnet test MnMCompanion.sln`
