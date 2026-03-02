# Changelog

## v2.2.0 -- Equal Opportunity Paranoia

Ghost pursuit and awareness tracking now cover **all non-hostile factions** — civilians, allies, and player units alike. Previously the AI only created ghosts for player units that broke LOS; now any opponent that disappears triggers investigation.

### Changed

- **AwarenessRecorder:** `OnEntityTileChanged` tracks all non-hostile entities, not just player faction. Hostile AI's own units are skipped to avoid self-tracking.
- **OpponentFilter:** Removed `isPlayerUnit` guard from ghost creation and cancellation. Any opponent breaking LOS can spawn a ghost; any re-sighted opponent cancels its ghost.
- **KnowledgeState:** Terminology updated (player → opponent) to reflect broader scope.

### Why

During wildlife-vs-civilian movement analysis, we found that AI wildlife ignored civilians that ducked behind cover — it only ghosted player units. Since the ghost system makes AI behavior more realistic for *all* opponent types, the player-only restriction was unnecessary.

## v2.1.0 -- I Saw You There

Ghost awareness system: AI investigates last-known player positions instead of instantly forgetting on LOS break.

### Added

- **Ghost pursuit:** When a previously spotted player unit breaks LOS, a ghost is created at the last-known position. Nearby tiles receive a UtilityScore bonus, nudging AI movement toward the ghost zone.
- **Auto-calibrating bonus:** Ghost bonus scales to the spread of the AI's existing tile scores (`spread × 0.33`), with a minimum floor of 20.0 for zoneless missions.
- **Mid-move sighting:** `Entity.SetTile` postfix tracks player unit positions as they move, recording sightings even between turns.
- **Per-unit tracking:** Each player unit is tracked independently. The AI can ghost one unit while engaging another.
- **Ghost lifecycle:** Priority decays 50% per round (20 → 10 → 5), expires after 3 rounds, cancelled immediately on re-sight.
- **Waypoint system:** Ghost waypoint advances toward the nearest AI unit each round, guiding investigation.
- **Settings panel:** 8 configurable parameters (zone size, priority, decay, spread fraction, minimum floor, etc.).
- **Score diagnostics:** Per-actor score ranges (Utility/Safety/Distance), top-5 tile breakdown with ghost bonus annotation (behind Debug Logging toggle).

### Changed

- **Codebase split into 6 files:** BooAPeekPlugin.cs (lifecycle), KnowledgeState.cs (state), OpponentFilter.cs (filtering), AwarenessRecorder.cs (sighting/discovery), GhostResponse.cs (ghost behavior), Patches.cs (Harmony patches). All `partial class BooAPeekPlugin`.
- **FilterOpponents** now calls KnowledgeState API (`RecordSighting`, `RecordLOSLost`) instead of inline dict manipulation.
- **Ghost decay timing fix:** Initial priority pre-multiplied by `1/GhostDecay` to compensate for immediate decay in `UpdateGhostWaypoints` (Postfix runs before ConsiderZones evaluates).

### Test Results

- **1-unit kill mission:** Ghost bonus +20 competes with mid-range player attraction (11.7–16.4), correctly loses to close-range (28.9–48.1). Wildlife moves 4–5 tiles toward ghost zone.
- **2-unit kill mission:** Ghost created for retreating Unit 2, boosted tiles from 11–15 to 31–35. On-screen enemy movement confirmed toward Unit 2's last-known position. Ghost cancelled ~2.5s later on re-sight.

[Design notes & full analysis](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v2.1.0_ghost_awareness.md)

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

Core fog-of-war rework: on each AI faction's turn, filters `m_Opponents` to only include player units visible to at least one living enemy in that faction. Pure binary filter — no awareness persistence, no TTL decay, no last-known-position.

[Investigation & analysis](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.1.1_AI_LEAK_ANALYSIS.md)
