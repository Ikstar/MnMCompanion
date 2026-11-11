# Recipes

## Overview
Parses community wiki tradeskill JSON (`/api/tradeskills`), persists a local recipe cache and HTTP conditional-request metadata under `AppFolder.Folder`, organizes recipes into trivial-level bands and UI tiers, filters recipes by text or level ranges, and schedules polite wiki refresh checks (`namespace MnMCompanion.Recipes`).

## Interface
- Domain records and immutable index ([RecipeBook.cs](./RecipeBook.cs)):
  - `Ingredient(string Name, int Qty, string? Or = null)` (`Show() -> string`), `RecipeNote(string Label, string Text)`, `Recipe(string Skill, string Group, string Result, int ResultQty, int? Level, string LevelText, IReadOnlyList<Ingredient> Ingredients, IReadOnlyList<RecipeNote> Notes)` (`Title`, `IngredientLine`, `Text() -> string`), and `Skill(string Name, string Station, string Tools, string Trainers, int Count, int? LowLevel, int? HighLevel)` (`LevelRange`).
  - `RecipeBook` (`sealed class`): `Skills`, `Recipes`, `WithoutIngredients`, `Of(string skill) -> IReadOnlyList<Recipe>`, `SkillNamed(string name) -> Skill?`, `static TryParse(string? json) -> RecipeBook?`.
- Local persistence and refresh policy ([RecipeCache.cs](./RecipeCache.cs), [RecipeRefresh.cs](./RecipeRefresh.cs)):
  - `RecipeCache` (`static class`): `DefaultPath`, `Load(string? path = null) -> (RecipeBook Book, DateTime FetchedUtc)?`, `Save(string json, string? path = null) -> bool`, `Age(DateTime fetchedUtc, DateTime? nowUtc = null) -> string`.
  - `RecipeMeta` (`sealed class`): Sidecar HTTP state (`recipes.meta.json`) with `ETag`, `CheckedUtc`, `TriedUtc`, `NotBeforeUtc`, `static PathFor(string? listPath = null) -> string`, `static Load(string? listPath = null) -> RecipeMeta`, `Save(string? listPath = null) -> bool`, and `BackOff(TimeSpan? wait, DateTime nowUtc)`.
  - `RefreshStep` (`Fresh`, `TooSoon`, `Ask`), `RefreshPlan(RefreshStep Step, string Why = "")`, and `RecipeRefresh` (`FreshFor = 1d`, `BetweenTries = 1h`, `BetweenManual = 10m`, `DefaultBackoff = 1h`, `MaxBackoff = 1d`, `Decide(DateTime nowUtc, bool haveList, RecipeMeta meta, DateTime? listTimeUtc, bool manual) -> RefreshPlan`).
- Level banding and search ([RecipeLevels.cs](./RecipeLevels.cs), [RecipeSearch.cs](./RecipeSearch.cs)):
  - `LevelBand(string Title, int? From, int? To, IReadOnlyList<Recipe> Recipes)` and `RecipeLevels` (`Width = 25`, `FlatBelow = 13`, `Tiers = 6`, `Tier(int? level) -> int`, `Compare(Recipe a, Recipe b) -> int`, `Sorted(IEnumerable<Recipe> recipes) -> IReadOnlyList<Recipe>`, `Bands(IEnumerable<Recipe> recipes) -> IReadOnlyList<LevelBand>`).
  - `RecipeSearch` (`static class`): `Matches(Recipe recipe, string? query) -> bool` and `Filter(IEnumerable<Recipe> recipes, string? query) -> IReadOnlyList<Recipe>`.

## Invariants
- Exception-safe parsing and normalization: `RecipeBook.TryParse` never throws—returning `null` on blank/malformed JSON, missing `"tradeskills"` array, or zero valid recipes—and normalizes whitespace, replaces `U+FFFD` with `'–'`, clamps non-positive quantities to `1`, folds stat attributes into a single `"Stats"` note, and omits skills with zero valid recipes.
- Case-insensitive lookups and level ordering: `RecipeBook.Skills`, `Of`, `SkillNamed`, `RecipeLevels.Compare` tie-breakers (`Result`, `Group`), and `RecipeSearch` use `OrdinalIgnoreCase`. `null` trivial levels sort last (`int.MaxValue`); `RecipeLevels.Bands` returns `[]` when `count == 0`, a single `"All levels"` band when `1 <= count < 13` (`FlatBelow`), or 25-level buckets (`level <= 0` placed in `1–25`) plus a trailing `"Level not on the wiki yet"` band only when at least one recipe has `Level is null`.
- Multi-token and level-range search: `RecipeSearch` splits queries via `MnMCompanion.Loot.NameSearch.Words` and AND-combines tokens; `d1-d2` ranges (1–3 digits, auto-swapped when `d1 > d2`) require non-null `Level` in `[min, max]`, while plain integers match exact `Level` or fall back to substring matching across `Result`, `Skill`, `Group`, ingredient names/alternatives, and `Notes[].Text`.
- UTC refresh throttling: `RecipeRefresh.Decide` expects UTC timestamps and enforces `NotBeforeUtc > nowUtc` first (even on manual refresh; `BackOff` clamps wait to `[1m, 1d]`), then `10m` manual cooldown, `1d` (`FreshFor`) freshness (`nowUtc - (CheckedUtc ?? listTimeUtc) <= FreshFor`) only when `haveList == true`, and `1h` automatic retry spacing.
- Persistence resilience and concurrency: `RecipeCache` (`Load`/`Save`) and `RecipeMeta.Save` catch `IOException` and `UnauthorizedAccessException` (`RecipeCache.Load` delegates JSON error handling to `RecipeBook.TryParse`), while `RecipeMeta.Load` catches `IOException`, `UnauthorizedAccessException`, and `JsonException`, returning fallback values instead of throwing. Domain records and `RecipeBook` are immutable, whereas `RecipeMeta` and `.tmp` file writes are unsynchronized. Depends on `AppFolder` and `MnMCompanion.Loot.NameSearch`.

## Side Effects
- Synchronous disk reads and atomic `.tmp`-move writes of `recipes.json` (`RecipeCache`) and `recipes.meta.json` (`RecipeMeta`) under `AppFolder.Folder`.
- In-memory property mutations on `RecipeMeta` (`BackOff` and setters) and fallback `DateTime.UtcNow` read in `RecipeCache.Age`.
- Zero network calls, background timers, or locks (`RecipeBook`, `RecipeLevels`, `RecipeRefresh.Decide`, and `RecipeSearch` are pure in-memory computations).

## Verification
- Run: `dotnet test MnMCompanion.sln`
