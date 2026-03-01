# BooAPeek — AI Information Leak Analysis

Note that this is a summary of a LOT of raw output and interactions.

## Problem Statement

The AI considers concealed player units in its tactical decisions even when no enemy has ever spotted them. Enemies flee from or optimally position around a concealed player unit they should have no knowledge of. This breaks the concealment mechanic by giving the AI perfect information about player positions.

## AI Architecture (Discovered via REPL)

### Type Hierarchy

```
TacticalManager (singleton, s_Singleton)
  └─ m_Factions[] (BaseFaction[])
       ├─ [1] PlayerFaction (extends BaseFaction)
       └─ [2-9] AIFaction (extends BaseFaction)
            ├─ m_Actors: List<Actor>
            ├─ m_Opponents: List<Opponent>      ← THE LEAK SOURCE
            ├─ m_Strategy: StrategyData
            ├─ m_OperationalZones: OperationalZones
            ├─ m_CurrentState, m_Time, m_PickableActorsCount
            ├─ m_IsAlliedWithPlayer: bool
            └─ methods: OnTurnStart, OnRoundStart, OnOpponentSighted,
                        HasKnownOpponent, GetOpponent, Think, Execute, Pick,
                        IsThinking, IsAlliedWith, GetOpponents, GetStrategy

BaseFaction (base class)
  ├─ m_Actors: List<Actor>
  ├─ m_FactionType: FactionType
  ├─ m_FactionIndex: Int32
  ├─ m_ActiveActor: Actor
  ├─ m_DeadActors: List<Actor>
  └─ methods: GetActors, AddActor, RemoveActor, OnActorDeath, OnOpponentSighted

Opponent (wrapper around a player unit)
  ├─ Actor: Actor       — LIVE reference to the actual player unit object
  ├─ TTL: Int32          — time-to-live (-2 = pre-populated, never sighted)
  ├─ Data: Assessment    — threat/priority/danger scores (no managed proxy)
  └─ IsKnown(): bool     — returns True when TTL >= 0 (verified empirically)

OnOpponentSighted(Actor _a, Int32 _ttl)
  — Exists on BOTH BaseFaction and AIFaction (override)
  — Sets TTL and populates Assessment data
  — In practice: NEVER fires for concealed units (concealment blocks detection)

Agent (per-unit AI controller, on every actor including player)
  ├─ m_Faction: AIFaction
  ├─ m_Actor: Actor
  ├─ m_Behaviors: List<Behavior>
  ├─ m_ActiveBehavior: Behavior
  ├─ m_Tiles: Dictionary<TileKey, TileScore>   — scored per-turn, cleared after
  ├─ m_NumThreatsFaced: Int32                   — stays 0 despite leak (not used)
  ├─ m_Score: Int32, m_Priority: Single
  ├─ m_State: State (0=None, 1=EvalTiles, 2=EvalBehaviors, 3=Ready, 4=Executing)
  └─ m_IsDeployed, m_IsSleeping, m_Flags, FlaggedForDeactivation

FactionType enum: Neutral=0, Player=1, PlayerAI=2, Civilian=3,
  AlliedLocalForces=4, EnemyLocalForces=5, Pirates=6, Wildlife=7,
  Constructs=8, RogueArmy=9
```

### Key Insight: Opponent.Actor Is a Live Reference

The `Opponent` object stores a direct `Actor` reference — not a last-known position snapshot. Any code that reads `opponent.Actor.GetPosition()` gets the player's **current real-time position**, regardless of whether `IsKnown()` returns true.

### IsKnown() Gate: TTL >= 0

Empirically verified:
- `TTL = -2` → `IsKnown = False` (pre-populated, never sighted)
- `TTL = 0` → `IsKnown = True` (any non-negative = known)

The tile scoring system does NOT check `IsKnown()` before reading `Opponent.Actor`.

## Test Setup

- **Map**: Tactical mission, 1 player unit (Darby) vs 26 pirates (faction 6)
- **Darby template patched**: `Properties.Concealment = 3` (via `stats/EntityTemplate.json`)
- **BooAPeek plugin**: Observation-only — logs turn transitions, LOS checks on enemy turns

## Evidence Timeline

### Rounds 1-2: Baseline (No Contact)

```
Darby @ (19,22), Concealment=3
Pirates (f6) m_Opponents: 1 entry
  [0] Actor=player_squad.darby, TTL=-2, IsKnown=False, Assessment=all zeros
All other factions (f2-f5, f7-f9): 0 opponents
Enemies with LOS: 0 (both rounds)
```

- Darby pre-populated in opponent list at mission start
- Enemy movement: scattered/random, no convergence on Darby
- `IsKnown=False` gating appears to hold when enemies are far away

### Round 3: First Contact (THE LEAK CONFIRMED)

Darby moved close to enemies, spotted a pirate outcast. Concealment prevented detection — no enemy had LOS to Darby.

```
Darby @ (7,22), Enemies with LOS: 0
Opponent: TTL=-2, IsKnown=False, Assessment=zeros (UNCHANGED)
```

**After enemy turn:** Pirates ran STRAIGHT AWAY from Darby's position despite `IsKnown=False`, `TTL=-2`, zero assessment, and zero LOS. The AI optimized movement to flee from a unit it should have no knowledge of.

### Round 4: The Definitive Test

Darby entered mutual visual range with two outcasts, then retreated behind concealment.

```
Darby @ (4,20)
  enemy.pirate_outcasts @ (8,16) dist=8, seesD=False
  enemy.pirate_outcasts @ (8,14) dist=10, seesD=False
Opponent: STILL TTL=-2, IsKnown=False, Assessment=zeros
```

**After enemy turn:** Both outcasts moved exactly one tile behind an obstacle to break LOS with Darby — not toward (investigate), not randomly — specifically to maximize cover FROM her current position.

**Smoking gun:** AI knows Darby's exact LIVE position and evaluates cover/threat against it, despite all gating fields being in the "unknown" state.

## Deep Probes (Round 5)

| Probe | Target | Result |
|-------|--------|--------|
| 1. IsKnown() IL | Decompile | 48-byte interop trampoline, native logic. Empirically: `TTL >= 0` → True |
| 2. Assessment struct | Field scan | No managed proxy. All guessed field names zero/null. Empty for unsighted opponents. |
| 3. Agent.m_NumThreatsFaced | Nearby enemies | **threats=0** on both outcasts that fled. Leak is transient in tile scoring, not persisted. |
| 4. Cached tile scores | Post-turn | **No cached tiles** — cleared after execution. Would need mid-turn intercept to capture. |

## Runtime Poke Experiments

### Poke 1: Set TTL=0 (Round 5)

**Hypothesis:** Setting TTL to 0 might make the scoring path properly gate.
**Result:** `TTL=0` → `IsKnown=True`. This makes it WORSE — tells the AI the opponent is officially known.
**Reverted** TTL back to -2 immediately.
**Conclusion:** TTL manipulation in either direction doesn't help. Positive = known, negative = unknown but still leaked.

### Poke 2: Clear m_Opponents._size=0 (Round 5→6)

**Hypothesis:** Empty list = nothing to read = no leaked position data.
**Action:** Set `m_Opponents._size = 0` before ending turn.
**Result:** Enemy turn completed. The game **repopulated m_Opponents** during the turn (count back to 1, TTL=-2, IsKnown=False after turn). The list is rebuilt in `OnTurnStart` or `OnRoundStart`.
**Enemy behavior:** With the list briefly empty at turn start, nearby enemies moved MORE toward Darby (dist decreased). Suggests the leak was temporarily broken but repopulation happened before/during Think().
**Conclusion:** Clearing once is insufficient — list is rebuilt each turn. Would need continuous clearing via coroutine during AI Think phase, or hook into OnTurnStart.

### Poke 3: Null Actor Reference (Round 6)

**Hypothesis:** Null the live Actor ref so AI can't read position.
**Action:** Set `Opponent.Actor = null` via managed setter.
**Verified:** Actor stayed null through player movement and spotting enemies.
**Result:** **GAME FROZE.** AI turn never completed. The native tile scoring code dereferences `Opponent.Actor` without null checks and entered an infinite loop/deadlock.
**Conclusion:** Nulling Actor is NOT viable — native code assumes it's always valid.

## Confirmed Facts

| Fact | Evidence |
|------|----------|
| All player units pre-loaded into `m_Opponents` at mission start | R1: opponents=1, Darby present before any turn |
| Pre-loaded with `IsKnown=False`, `TTL=-2` | Consistent across all rounds |
| `IsKnown()` returns `TTL >= 0` | Poke 1: TTL=0 → IsKnown=True, TTL=-2 → False |
| `OnOpponentSighted` never fires for concealed units | TTL stayed -2 even after mutual visual range |
| `IsKnown=False` does NOT prevent AI from using position | Enemies fled from concealed Darby (R3-R4) |
| AI uses LIVE position, not last-known | Enemies tracked Darby after she moved |
| Leak is in tile scoring (transient) | Agent.m_NumThreatsFaced=0, no cached tiles post-turn |
| `m_Opponents` is rebuilt each AI turn | Poke 2: cleared list repopulated during enemy turn |
| Nulling `Opponent.Actor` freezes the game | Poke 3: native code has no null check |
| `m_Opponents` list clearing briefly works | Poke 2: enemies moved differently when list was empty at turn start |
| Sighting sets TTL=2, Assessment populated | Enemy at dist=2 → TTL=2, Threat=29.58 |
| TTL does NOT decay naturally | Leak causes enemies to chase → re-spot → TTL stays at 2 |
| Swapped list resets to TTL=-2 on rebuild | After swap turn, repopulated opponent had TTL=-2, Threat=0 |
| **Game has NO awareness persistence** | No last-known-position, no memory outside m_Opponents |
| **Game has NO TTL decay mechanism** | TTL is either refreshed by sighting or reset to -2 on rebuild |

## TTL Lifecycle (Empirically Determined)

```
Mission Start:
  m_Opponents populated with all player units, TTL=-2, IsKnown=False, Assessment=zeros

Each AI Turn (OnTurnStart/OnRoundStart):
  m_Opponents rebuilt → all entries reset to TTL=-2 if list was replaced
  (If list was NOT replaced, existing entries persist with current TTL)

During AI Turn (Think/tile scoring):
  IF enemy spots player → OnOpponentSighted fires → TTL set to positive (observed: 2)
  IF enemy does NOT spot → TTL unchanged (stays -2 or whatever it was)
  Tile scorer reads ALL opponents regardless of IsKnown() → THE LEAK

Key observations:
  - TTL=2 after sighting, but we never observed natural decay (2→1→0)
  - Because the leak causes enemies to chase, they always re-spot, refreshing TTL
  - After a swapped turn (enemies wander randomly), the rebuilt list resets to TTL=-2
  - The game stores NO last-known-position — only the live Actor reference
  - Assessment (Threat=29.58) is only populated when IsKnown=True
```

## Fix Vector Tests

### ~~Vector 1: TTL manipulation~~ — RULED OUT
Setting TTL=0 makes `IsKnown=True` (worse). No TTL value both keeps `IsKnown=False` and stops the leak.

### ~~Vector 2: Null Actor reference~~ — RULED OUT
Game freezes. Native code assumes Actor is always valid.

### ~~Vector 3: Continuous m_Opponents._size clearing~~ — SUPERSEDED
Poke 2 showed _size=0 briefly changed behavior but the list is rebuilt mid-turn. Superseded by the cleaner list swap approach (Vector 4).

### Vector 4: Replace m_Opponents with empty list — CONFIRMED WORKING

Swap the entire `m_Opponents` reference to a freshly constructed empty `List<Opponent>` before the AI turn. The game's tile scorer reads the (now empty) list and finds no opponents to consider. After the AI turn, the game repopulates the list naturally (or we restore the original).

#### Mechanism

```csharp
// Before AI turn:
var oldList = getter.Invoke(aiF, null);           // save real list
var emptyList = listCtor.Invoke(null);             // new List<Opponent>()
setter.Invoke(aiF, new object[] { emptyList });    // swap in empty

// AI runs Think() → tile scoring finds 0 opponents → no leaked position data

// After AI turn:
// Game repopulates m_Opponents naturally (rebuilds with TTL=-2 entries)
```

The empty list is constructed via reflection on the parameterless constructor of `Il2CppSystem.Collections.Generic.List<Opponent>` (generic syntax doesn't parse in REPL, so we use `oldList.GetType().GetConstructor(new Type[0]).Invoke(null)`).

#### Test 1: Baseline (Round 1→2, fresh mission)

**Setup:** Darby @ (19,22), concealment=3, 26 pirates. Swapped m_Opponents to empty list before ending turn.

| Enemy (R1 pos) | R2 pos | Dist before | Dist after | Delta | Direction |
|-----------------|--------|-------------|------------|-------|-----------|
| (5,2) | (0,1) | 24.4 | 28.3 | +3.9 | Away |
| (9,4) | (5,7) | 20.6 | 20.5 | -0.1 | Lateral |
| (41,40) | (39,41) | 28.4 | 27.6 | -0.8 | Toward |
| (40,36) | (38,39) | 25.2 | 25.5 | +0.3 | Lateral |
| (2,39) | (2,38) | 24.0 | 23.3 | -0.7 | Toward |
| (2,6) | (4,3) | 23.3 | 24.2 | +0.9 | Away |
| (30,6) | (27,10) | 19.4 | 14.4 | -5.0 | Toward |
| (7,41) | (12,41) | 22.5 | 20.2 | -2.2 | Toward |
| (25,37) | (26,37) | 16.2 | 16.6 | +0.4 | Lateral |
| *(17 units)* | *(same)* | — | — | 0 | Stayed |

**Summary:** 9 moved, 17 stayed. Of movers: 4 toward, 2 away, 3 lateral.
**Assessment:** Normal patrol behavior — no coordinated fleeing from Darby. Compare with unpatched R3-R4 where enemies unanimously fled.

#### Test 2: Close Proximity (Round 2→3, near enemies)

**Setup:** Darby moved to (11,18), two pirates within 5-6 tiles. Swapped m_Opponents before ending turn.

| Enemy | Before | After | Dist before | Dist after | Delta | Direction |
|-------|--------|-------|-------------|------------|-------|-----------|
| **Enemy 1** | **(7,15)** | **(9,18)** | **5.0** | **2.0** | **-3.0** | **TOWARD** |
| **Enemy 2** | **(6,16)** | **(2,16)** | **5.4** | **9.2** | **+3.8** | **Away (patrol)** |

**Summary:** One enemy walked straight into Darby's face (dist 5→2). The other patrolled away randomly.
**Assessment:** Enemies have ZERO awareness of Darby. In unpatched sessions at similar distances, both enemies would flee or take optimal cover positions. Enemy 1 closing to dist=2 is the definitive proof — no AI with knowledge of a threat would voluntarily walk into melee range.

#### Test 3: TTL Decay Investigation (Rounds 3→7)

**Goal:** Determine if TTL decays naturally after losing LOS.

| Round | Action | TTL | IsKnown | Threat | Notes |
|-------|--------|-----|---------|--------|-------|
| R3 | Enemy at dist=2 spotted Darby | 2 | True | 29.58 | Sighting populated Assessment |
| R4 | Darby moved to (15,22), dist=9 | 2 | True | 29.58 | TTL unchanged (still player turn) |
| R4 post | Enemy turn (no swap) | 2 | True | 29.48 | Enemy chased to (14,19) dist=3.2, re-spotted |
| R5 | Darby at (15,28), dist=9 | 2 | True | 29.48 | Still too close, enemy re-spots |
| R5 post | Enemy turn (no swap) | 2 | True | — | Enemy chased to (14,24) dist=4.1 |
| R6 | **Swap applied**, Darby fled | 0 opp | — | — | Enemies wander randomly |
| R6 post | List rebuilt after swap | **-2** | **False** | **0** | **Complete reset — no memory** |

**Conclusion:** The game has NO awareness persistence outside `m_Opponents`. Swapping the list destroys all sighting history. TTL never decayed naturally because the leak kept enemies close enough to re-spot. The game has no last-known-position system — BooAPeek must build its own.

#### Test 4: Controlled Before/After — Leak vs Swap (Rounds 9→12)

**Goal:** Same enemies, same Darby position, back-to-back unswapped vs swapped turns for direct comparison.

**Setup:** Darby @ (23,31), concealment=3. Three nearby pirates, TTL=-2, IsKnown=False, 0 LOS. Perfect conditions — enemies close enough to react but have zero legitimate knowledge.

**Phase A — Unswapped (leak active), R9→R10→R11:**

| Enemy | R9 | R10 | R11 | Trend |
|-------|-----|-----|-----|-------|
| A | (26,35) d=5.0 | (26,35) d=5.0 | (26,35) d=5.0 | **"Frozen" 2 turns** (see note) |
| B | (27,35) d=5.7 | (30,38) d=9.9 | (31,39) d=11.3 | **Fleeing** (+5.6 over 2 turns) |
| C | (28,34) d=5.8 | (28,34) d=5.8 | (27,35) d=5.7 | ~frozen |

Behavior: One enemy actively fleeing, two "frozen" in place. Classic leak — AI knows Darby is there, evaluates threat, chooses to either hide or run. **Note:** The (26,35) tile is a high-cover position. Different units rotated through it across turns — the leak causes the tile scorer to rate it highly as cover against Darby's leaked position, so multiple units converge there. Not truly "frozen" — it's the tile being overvalued.

**Phase B — Swapped (leak blocked), R11→R12:**

Swap applied before ending R11 turn. Same Darby position (23,31), same enemies.

| Enemy | R11 (pre-swap) | R12 (swapped) | Δ to Darby | Behavior |
|-------|----------------|---------------|------------|----------|
| A | (26,35) d=5.0 | (26,35) d=5.0 | 0.0 | Didn't move |
| B | (31,39) d=11.3 | (32,36) d=10.3 | **-1.0** | **TOWARD** (was fleeing!) |
| C | (27,35) d=5.7 | (26,35) d=5.0 | **-0.7** | **TOWARD** |

**Verdict:** Same enemies, same Darby position, completely reversed behavior. Enemy B had been fleeing at +5.6 over 2 unswapped turns, then immediately reversed direction (-1.0 toward) once the leak was blocked. The swap eliminates the AI's illegitimate knowledge.

#### Comparison: Patched vs Unpatched Behavior

| Scenario | Unpatched | Patched (list swap) |
|----------|-----------|---------------------|
| Far enemies (dist>15) | Scattered/random | Scattered/random |
| Medium enemies (dist 5-10) | **Flee from Darby** | Normal patrol |
| Close enemies (dist<5) | **Optimal cover / frozen / flee** | **Walk toward/past Darby obliviously** |
| Darby visible then concealed | **Track and hide from new position** | Not yet tested (need awareness system) |
| After swap + rebuild | N/A | **TTL=-2, complete amnesia** |
| Same enemies, same position (Test 4) | Frozen + fleeing for 2 turns | **Reversed direction, moved toward Darby** |
