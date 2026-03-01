# BooAPeek — Fixes AI Knowing about Concealed Player Units.

**Version:** 1.1.0 — *"Must have been the wind"*
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

On each AI faction's turn start, BooAPeek:

1. Gets the faction's `m_Opponents` list via reflection
2. For each opponent, checks if **any** living enemy in that faction has line-of-sight (`CanActorSee`) to that player unit
3. Builds a new list containing only opponents that are actually visible
4. Swaps `m_Opponents` to the filtered list

The AI's tile scorer then sees only legitimately visible opponents. The game naturally rebuilds `m_Opponents` at the start of the next turn, so no cleanup is needed.

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

## Complementary mods
- [Wake Up ~ By Pylkij](https://www.nexusmods.com/menace/mods/36)
- [PeekABoo ~ By YandrosTheSane](https://www.nexusmods.com/menace/mods/69)

## Installation
Use the https://github.com/p0ss/MenaceAssetPacker/releases to deploy (build the sources) and activate the mod.

## Current State & Known Limitations

### "What v1.1.0 Does (Supposedly) Well

(at the time of release I have played legitimately 5 full operations with the mods above and feel very confident about it being a better player experience)

- Eliminates the AI's illegitimate knowledge of concealed player positions
- Enemies behave more naturally when they haven't spotted you: patrolling, wandering, even walking past or into you
- When enemies do have LOS, they react normally (opponent stays in the list)
- Zero gameplay impact outside of fog-of-war — no behavior changes for visible encounters
- Stable across all tested scenarios — no crashes, no freezes, no list corruption

It not yet satisfying but way better than before, no more herding.

### What v1.1.0 Does NOT Do: Awareness Persistence

The current version is a **pure fog-of-war filter** — binary visible/invisible, evaluated fresh each turn. The system has no built-in awareness persistence:

- **No TTL decay:** TTL is either refreshed by sighting or reset to -2 on list rebuild. It never naturally decays (2→1→0). We never observed natural decay because the leak kept enemies close enough to always re-spot.
- **No last-known-position:** The only position data in `Opponent` is the live `Actor` reference. There is no snapshot of where a player was last seen.
- **No faction memory:** Swapping the list causes total amnesia. I did not find something outside `m_Opponents`. I may be there but we're in the dark.

This means that once **all** enemies in a faction lose line-of-sight, the faction **instantly forgets** the player existed. An enemy that was actively chasing you will suddenly wander aimlessly next turn if it rounds a corner and loses LOS.


## Settings

Configurable via the in-game Modkit settings panel:

| Setting | Default | Description |
|---------|---------|-------------|
| **Debug Logging** | Off | Logs actor counts and detailed init info |

Log output always includes turn transitions and filtering results (e.g. `"stripped 1 unseen opponent(s), kept 0"`).

## Investigation & Testing

Tested with a concealed player unit (Concealment=3) against 26 pirates across 12 rounds, using live REPL inspection of AI state at each turn boundary. Full round-by-round data, position tables, and architecture notes in [AI_LEAK_ANALYSIS.md](docs/AI_LEAK_ANALYSIS.md).

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

- **Turn detection:** Polls `TacticalController.GetCurrentFaction()` each frame; triggers on faction transitions for AI factions (indices 3–9)
- **Reflection cache:** All type lookups, method handles, and constructors are resolved once on scene load (60-frame delay for scene init), then reused every turn with zero allocation overhead
- **LOS checks:** Uses `LineOfSight.CanActorSee(enemy, playerUnit)` — the same function the game uses internally
- **Actor enumeration:** `EntitySpawner.ListEntities(factionIdx)` for faction-specific living actors
- **Faction filtering:** Only processes AI factions (indices 3–9); player (1–2) and neutral (0) are skipped
- **Scene lifecycle:** State fully reset on scene transitions; reflection cache invalidated when leaving tactical

## File Structure

```
BooAPeek-modpack/
├── modpack.json              # Mod metadata and load order
├── src/
│   └── BooAPeekPlugin.cs     # Plugin source (IModpackPlugin)
├── stats/
│   └── EntityTemplate.json   # Test template patches (Concealment=3)
├── AI_LEAK_ANALYSIS.md       # Full investigation with round-by-round evidence
├── ab-test-maps.txt          # Test map notes
└── README.md                 # This file
```

## Credits

- **Rat** — [Fight You Cowards](https://www.nexusmods.com/menace/mods/34) mod. Its `ThreatFromOpponents=0` template patch was the starting point that revealed the AI's opponent-list-driven behavior and led to the full investigation of the information leak.

## Requirements

- Menace with MelonLoader
- Menace ModpackLoader
