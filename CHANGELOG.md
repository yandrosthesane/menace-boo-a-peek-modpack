# Changelog

## v2.0.0 -- Under the Hood

Complete architecture rewrite. Same filtering logic, but now uses direct Il2Cpp types and Harmony patching instead of reflection and frame polling.

### Changed

- **Harmony `OnTurnStart` prefix** replaces OnUpdate frame polling for turn detection. Filtering now runs *before* the AI thinks, with guaranteed timing.
- **Direct Il2Cpp type access** replaces all reflection (`GameObj.ReadObj`, `ReadInt`, string-based method lookup). Fields and methods are accessed at compile time.
- **`TryCast<AIFaction>()`** replaces `GameObj.Is()` for faction type-checking.
- **`Il2CppSystem.Collections.Generic.List<Opponent>`** replaces reflected generic list construction.
- **No more OnUpdate polling** — the mod does zero work per frame outside of Harmony patch execution.

### Discovered

- `AIFaction.OnTurnStart` fires per AI **unit**, not per faction (15 wildlife = 15 calls per round).
- Harmony patches on `AIFaction.OnTurnStart` work in Il2Cpp (contradicts earlier assumption that native dispatch bypasses Harmony for all game methods).

### Unchanged

- Filtering logic (binary LOS check, list swap)
- Faction discovery (0-15 probe, three-way classification)
- No awareness persistence (instant amnesia on LOS break)

[Design notes & migration details](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v2.0.0_harmony_migration.md)

## v1.2.0 -- Who Goes There?

Factions are now discovered at runtime instead of hardcoded. Allied AI factions (Civilian, Allied Local Forces) are correctly skipped — v1.1.x incorrectly stripped their opponents too.

[Design notes & analysis](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.2.0_better_filtering.md)

## v1.1.1 -- Housekeeping

Settings cleanup, release tooling, documentation.

## v1.1.0 -- Opponent List Filtering

Core fog-of-war fix: on each AI faction's turn, filters `m_Opponents` to only include player units visible to at least one living enemy in that faction. Pure binary filter — no awareness persistence, no TTL decay, no last-known-position.

[Investigation & analysis](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.1.1_AI_LEAK_ANALYSIS.md)
