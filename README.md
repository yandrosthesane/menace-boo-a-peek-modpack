# BooAPeek — Fixes AI Knowing about Concealed Player Units.

**Version:** 2.1.0 — *"I Saw You There"*
**Author:** YandrosTheSane

## What It Does

BooAPeek tries to fix a fundamental AI information leak in Menace's tactical combat. 
Without this mod, the AI knows the exact real-time position of all player units — even concealed ones it has never seen — and uses that knowledge to flee, take cover, and position optimally against invisible threats.

## The Problem

At mission start, the game pre-populates every AI faction's `m_Opponents` list with **all** player units, each containing a live `Actor` reference to the actual unit object. The AI's tile scoring system reads opponent positions from this list to evaluate threat, cover, and flee calculations — but **never checks `IsKnown()`** before doing so.

The result: enemies react to concealed player units they have zero legitimate knowledge of.

They flee from unseen threats, freeze behind high-cover tiles relative to invisible positions, and optimally reposition against ghosts causing the well known "herding" pattern..

## How It Currently Works

### Core Mechanism: Opponent List Filtering

On each AI unit's turn start (via Harmony prefix on `AIFaction.OnTurnStart`), BooAPeek:

1. Gets the faction's `m_Opponents` list directly (compiled Il2Cpp types, no reflection)
2. For each opponent, checks if **any** living enemy in that faction has line-of-sight (`CanActorSee`) to that player unit
3. Builds a new list containing only opponents that are actually visible
4. Swaps `m_Opponents` to the filtered list

The filtering runs *before* the AI thinks, with guaranteed timing. The game naturally rebuilds `m_Opponents` at the start of the next turn, so no cleanup is needed.

### Ghost Awareness (v2.1.0)

When the AI loses line-of-sight on a previously spotted player unit, BooAPeek creates a "ghost" at the last-known position. This injects a bonus into the AI's tile scoring (via `ConsiderZones.Evaluate` postfix), nudging nearby enemies to investigate rather than instantly forgetting.

- **Auto-calibrating bonus:** Scales to the spread of the AI's existing tile scores. Minimum floor of 20.0 ensures ghosts matter even in zoneless kill missions.
- **Decay over time:** Ghost priority halves each round (3 rounds max), so the AI doesn't fixate forever.
- **Cancellation on re-sight:** If any enemy spots the player again, the ghost is immediately cancelled.
- **Per-unit tracking:** Each player unit is tracked independently — the AI can ghost one unit while actively engaging another.

### How the Leak Supposedly Works

```
Mission Start:
  m_Opponents populated with ALL player units
  Each entry: Actor = live reference, TTL = -2, IsKnown = False

During AI Turn (Think / tile scoring):
  Tile scorer reads Opponent.Actor.GetPosition() for EVERY opponent
  → Gets player's CURRENT real-time position
  → Never checks IsKnown() or TTL
  → Evaluates threat/cover/flee against that position

Result:
  AI reacts to concealed units it has never seen
```

## Changelog

### v2.1.0 -- I Saw You There

Ghost awareness system: AI investigates last-known player positions instead of instantly forgetting on LOS break. Auto-calibrating UtilityScore injection via ConsiderZones postfix, with spread-based scaling, per-round decay, and per-unit tracking. Codebase split into 6 concern-based files. [Design notes & test results](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v2.1.0_ghost_awareness.md)

### v2.0.0 -- Under the Hood

Complete architecture rewrite: direct Il2Cpp types + Harmony `OnTurnStart` prefix replaces reflection + frame polling. Same filtering logic, guaranteed timing, zero per-frame overhead. [Migration details](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v2.0.0_harmony_migration.md)

### v1.2.0 -- Who Goes There?

Factions are now discovered at runtime instead of hardcoded. Allied AI factions (Civilian, Allied Local Forces) are correctly skipped — v1.1.x incorrectly stripped their opponents too. [Design notes](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.2.0_better_filtering.md)

### v1.1.1 -- Housekeeping

Settings cleanup, release tooling, documentation.

### v1.1.0 -- Opponent List Filtering

Core fog-of-war fix: on each AI faction's turn, filters `m_Opponents` to only include player units visible to at least one living enemy in that faction. Pure binary filter — no awareness persistence, no TTL decay, no last-known-position. [Investigation](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.1.1_AI_LEAK_ANALYSIS.md)

## Complementary mods
- [Wake Up ~ By Pylkij](https://www.nexusmods.com/menace/mods/36)
- [PeekABoo ~ By YandrosTheSane](https://www.nexusmods.com/menace/mods/69)

## Installation
Use the https://github.com/p0ss/MenaceAssetPacker/releases to deploy (build the sources) and activate the mod.

## Current State & Known Limitations

### What v2.0.0 Does (Supposedly) Well

(at the time of release I have played legitimately 5 full operations with the mods above and feel very confident about it being a better player experience)

- Eliminates the AI's illegitimate knowledge of concealed player positions
- Enemies behave more naturally when they haven't spotted you: patrolling, wandering, even walking past or into you
- When enemies do have LOS, they react normally (opponent stays in the list)
- Zero gameplay impact outside of fog-of-war — no behavior changes for visible encounters
- Stable across all tested scenarios — no crashes, no freezes, no list corruption

It not yet satisfying but way better than before, no more herding.

### Awareness Persistence (v2.1.0)

v2.0.0 was a pure binary filter — instant amnesia on LOS break. v2.1.0 adds ghost awareness:

- **Last-known-position tracking:** When a player unit is visible, its tile is recorded. When LOS breaks, a ghost is created at that position.
- **UtilityScore injection:** Ghost zones boost nearby tiles by +20 (auto-calibrated minimum floor), nudging AI movement toward the last-seen position.
- **Decay:** Ghost priority halves each round (20 → 10 → 5) over 3 rounds, then expires.
- **Per-unit independence:** Each player unit is tracked separately — losing sight of one doesn't affect awareness of another.

### Remaining Limitations

- **No faction-wide memory sharing:** Only the enemies that personally had LOS contribute to ghost creation. There is no "alert" propagation between units.
- **No speculative fire:** The AI investigates but doesn't shoot at ghost positions (future work).
- **Ghost bonus is movement-only:** Injected into UtilityScore, which drives tile selection. It does not affect combat decisions like target priority or ability usage.


## Settings

Configurable via the in-game Modkit settings panel:

| Setting | Default | Description |
|---------|---------|-------------|
| **Debug Logging** | Off | Per-actor score ranges, top tiles, ghost zone diagnostics |
| **Ghost Zone Size** | 5 | Width/height of the ghost influence zone (tiles) |
| **Initial Priority** | 20 | Base ghost priority (pre-multiplied to survive first decay) |
| **Decay Per Round** | 0.5 | Priority multiplier each round |
| **Max Rounds** | 3 | Rounds before ghost expires |
| **Waypoint Distance** | 6 | Max tiles the ghost waypoint advances toward nearest AI |
| **Spread Fraction** | 0.33 | Ghost bonus as fraction of observed score spread |
| **Minimum Bonus** | 20 | Floor for ghost bonus when spread is low/zero |

Log output always includes turn transitions and filtering results (e.g. `"Wildlife: stripped 1, kept 1, ghosts +1/-0"`).

## Investigation & Testing

Tested with a concealed player unit (Concealment=3) against 26 pirates across 12 rounds, using live REPL inspection of AI state at each turn boundary. Full round-by-round data, position tables, and architecture notes in [v1.1.1_AI_LEAK_ANALYSIS.md](docs/v1.1.1_AI_LEAK_ANALYSIS.md).

### Confirming the Leak

With the leak active, enemies unanimously flee from a concealed unit they have never seen — even when all gating fields (`TTL=-2`, `IsKnown=False`, `Assessment=zeros`) confirm "unknown." At close range, enemies take optimal cover positions relative to the player's exact live position despite zero LOS.

### Ruled-Out Fixes

Three runtime approaches were tested before arriving at the list swap:

| Approach | Result |
|----------|--------|
| Set TTL=0 | Makes `IsKnown=True` — worse, not better |
| Clear list size to 0 | Game rebuilds the list mid-turn before AI thinks |
| Null the Actor reference | Game freeze — native code has no null checks |

### Fix Validation (4 Controlled Tests)

| Test | Scenario | Result |
|------|----------|--------|
| **Baseline** | 26 enemies, no contact | Normal patrol — random movement, no coordinated fleeing |
| **Close proximity** | 2 enemies at distance 5 | One walked straight into the player (d=5→2), the other patrolled away. Zero awareness. |
| **TTL lifecycle** | Enemy spots player, then list swapped | Complete amnesia — `TTL=-2`, `Threat=0` after swap. No persistence outside `m_Opponents`. |
| **Before/after** | Same enemies, same position, leak on then off | Enemy that fled +5.6 over 2 turns **reversed direction** (-1.0 toward) immediately after swap. |

## Technical Details

- **Turn detection:** Harmony prefix on `AIFaction.OnTurnStart` — fires per AI unit, guaranteed before AI thinks
- **No reflection:** All types accessed directly at compile time via `Assembly-CSharp` reference
- **LOS checks:** Uses `LineOfSight.CanActorSee(enemy, playerUnit)` — the same function the game uses internally
- **Actor enumeration:** `EntitySpawner.ListEntities(factionIdx)` for faction-specific living actors
- **Faction discovery:** Probes factions 0–15 at init, classifies each as hostile AI (filtered), allied AI (skipped), or player (LOS source) using `GameObj.Is()` type-checking and `AIFaction.m_IsAlliedWithPlayer`
- **Scene lifecycle:** State fully reset on scene transitions; reflection cache invalidated when leaving tactical

## File Structure

```
BooAPeek-modpack/
├── modpack.json                      # Mod metadata and load order
├── src/
│   ├── BooAPeekPlugin.cs             # Entry point, lifecycle, settings
│   ├── KnowledgeState.cs             # Central awareness data + state transitions
│   ├── OpponentFilter.cs             # LOS-based opponent stripping
│   ├── AwarenessRecorder.cs          # Mid-move sighting, faction discovery
│   ├── GhostResponse.cs              # Ghost waypoints, spread calibration
│   └── Patches.cs                    # Harmony patches + score diagnostics
├── docs/
│   ├── v1.1.1_AI_LEAK_ANALYSIS.md   # Full investigation with round-by-round evidence
│   ├── v1.2.0_better_filtering.md   # Dynamic faction discovery design notes
│   ├── v2.0.0_harmony_migration.md  # Harmony + direct Il2Cpp types migration
│   └── v2.1.0_ghost_awareness.md    # Ghost awareness design + test results
├── media/                            # Screenshots for Nexus (not in release zip)
├── CHANGELOG.md                      # Version history
├── release.sh                        # Build release zip
└── README.md                         # This file
```

## Credits

- **Rat** — [Fight You Cowards](https://www.nexusmods.com/menace/mods/34) mod. Its `ThreatFromOpponents=0` template patch was the starting point that revealed the AI's opponent-list-driven behavior and led to the full investigation of the information leak.

## Requirements

- Menace with MelonLoader
- Menace ModpackLoader
