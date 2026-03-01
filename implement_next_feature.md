# BooAPeek v2.1 — Ghost Position Interception + Speculative Fire

## Goal

Build on the working ghost awareness system (UtilityScore injection via ConsiderZones.Evaluate) to add:
1. **Full tactical AI behavior** — keep ghost in m_Opponents so ThreatFromOpponents, CoverAgainstOpponents, and Attack behaviors all naturally work against the ghost's frozen position
2. **Speculative fire at retreat tiles** — AI fires at probable retreat positions (fan of 3 tiles behind last-known position) to punish "peek-shoot-retreat" exploit
3. **Blind fire for long-range weapons** — if weapon range > vision range, AI "takes its chance" firing at ghost position

## Current State (Working)

Ghost system confirmed working (2026-03-02 test). Architecture:
- `Entity.SetTile` postfix catches mid-movement sightings
- `FilterOpponents` strips ghost opponents from m_Opponents + creates GhostMemory
- `UpdateGhostWaypoints` computes waypoint toward target, decays priority ×0.5/round
- `GetGhostScoreBonus` adds priority to UtilityScore in ConsiderZones.Evaluate postfix
- `OnRoundStart` resets `GhostsUpdatedThisRound` flag

**Limitation:** Only UtilityScore drives behavior. Ghost is stripped so no threat/cover/attack evaluation.

---

## Implementation Plan

### Step 0: Test Entity.GetTile() Patchability

Add minimal Harmony postfix on `Entity.GetTile()`. If it fires → proceed. If not → use fallback.

**Key fact:** Both `Entity.GetTile()` and `Entity.SetTile()` are `virtual` (confirmed in stubs at line 84 and 581 of `Menace-dll/Assembly-CSharp/Menace/Tactical/Entity.cs`). Our SetTile patch works in production → GetTile should work too.

### Step 1: GhostMemory — Add Fan Tiles

```csharp
private class GhostMemory
{
    public int TargetX, TargetZ;       // Last-seen player position
    public int WaypointX, WaypointZ;   // Movement attraction waypoint (existing)
    public int FanCenterX, FanCenterZ; // Center retreat tile
    public int FanLeftX, FanLeftZ;     // Left retreat tile
    public int FanRightX, FanRightZ;   // Right retreat tile
    public int RoundsRemaining;
    public float Priority;
}
```

Fan tiles computed once at ghost creation, using nearest AI unit direction:
```
retreatX = sign(tx - ax), retreatZ = sign(tz - az)
Center: (tx + retreatX, tz + retreatZ)

For sides:
  Cardinal retreat (one axis is 0): spread on the zero axis ±1
  Diagonal retreat (both axes non-zero): sides are the two cardinal components
```

### Step 2: FilterOpponents — Keep Ghosts in m_Opponents

Change from "strip ghost" to "keep ghost, mark in `_ghostActorPtrs` HashSet."

### Step 3: Entity.GetTile() Harmony Postfix

```csharp
[HarmonyPatch(typeof(Entity), nameof(Entity.GetTile))]
static class Patch_Entity_GetTile
{
    static void Postfix(Entity __instance, ref Tile __result)
    {
        if (!_ghostInterceptionActive) return;
        if (!_ghostActorPtrs.Contains(__instance.Pointer)) return;

        var ghost = GetGhostForActor(__instance.Pointer);
        if (ghost == null) return;

        int tx, tz;
        if (_attackPhase) {
            var fan = _currentFanSelection[__instance.Pointer];
            tx = fan.x; tz = fan.z;
        } else {
            tx = ghost.TargetX; tz = ghost.TargetZ;
        }

        var tile = TacticalManager.Get().GetMap().GetTile(tx, tz);
        if (tile != null) __result = tile;
    }
}
```

### Step 4: Interception Gating

- `_ghostInterceptionActive = true` during OnTurnStart prefix (after FilterOpponents)
- `_ghostInterceptionActive = false` after AI turn completes
- `_attackPhase` flag for fan tile vs. last-known position

### Step 5: Tune UtilityScore

Ghost now contributes through multiple channels (ThreatFrom, Cover, Attack + UtilityScore). May need to reduce UtilityScore priority to avoid over-attraction.

### Fallback (if GetTile fails)

Keep ghost stripped. After AI moves (existing UtilityScore attraction), directly call `skill.Use(fanTile)` for AI units with indirect fire weapons in range.

---

## Research: AI Scoring System

### TileScore Fields

| Field | Purpose | Written by |
|-------|---------|-----------|
| `UtilityScore` | Strategic value | ConsiderZones ← **we inject ghost bonus here** |
| `SafetyScore` | Threat/safety | ThreatFromOpponents, CoverAgainstOpponents, FleeFrom |
| `DistanceScore` | Penalty for distance | DistanceToCurrentTile |
| `UtilityByAttacksScore` | Attack opportunity from this tile | Attack behaviors |
| `SafetyScoreScaled` / `UtilityScoreScaled` | Post-processed | PostProcess phase |
| `APCost` | Action points to reach | Movement system |
| `IsVisibleToOpponentsHere` | Exposure flag | Criteria |

### All 11 Criteria

| Criterion | What it scores | Phase |
|-----------|---------------|-------|
| `ThreatFromOpponents` | SafetyScore from enemy threat | Evaluate (multi-threaded) |
| `CoverAgainstOpponents` | SafetyScore from cover quality | Evaluate |
| `FleeFromOpponents` | SafetyScore for fleeing | Evaluate |
| `AvoidOpponents` | SafetyScore for proximity | Evaluate |
| `ConsiderZones` | UtilityScore from zones | Collect + Evaluate + PostProcess |
| `DistanceToCurrentTile` | DistanceScore | Evaluate |
| `ExistingTileEffects` | Tile effects | Evaluate |
| `ConsiderSurroundings` | Environmental | Collect |
| `Roam` | Random movement | Collect |
| `WakeUp` | Alert response | Collect |
| (unnamed) | UtilityByAttacksScore | Attack behavior eval |

### Behavior Selection

```
Agent.Evaluate() → runs all criteria → populates TileScore per tile
  → each Behavior.Evaluate() → computes behavior score
Agent.PickBehavior() → sorts by score, picks highest
Agent.Execute() → runs the behavior
```

Move and Attack both have `GetOrder() → 0` (same priority). Winner determined by score.

### AI Weights (AIWeightsTemplate)

Key weight fields that affect ghost behavior:
- `ThreatFromOpponents` — weight for threat scoring
- `CoverAgainstOpponents` — weight for cover scoring
- `UtilityScale` / `SafetyScale` — global scaling
- `MoveIfNewTileIsBetterBy` — threshold to move
- `InflictDamage` (RoleData) — attack priority weight

### RoleData — Per-Unit AI Personality

```csharp
public float UtilityScale;            // Aggression
public float SafetyScale;             // Defensive emphasis
public float DistanceScale;           // Distance preference
public float Move;                    // Movement priority
public float InflictDamage;           // Attack priority
public float InflictSuppression;      // Suppression priority
public bool AttemptToStayOutOfSight;
public bool PeekInAndOutOfCover;
```

---

## Research: Skill/Weapon System

### Key API Chain

```
Entity.GetSkills() → SkillContainer
  .GetAllSkills() → List<BaseSkill>
    baseSkill.GetTemplate() → SkillTemplate
      .IsLineOfFireNeeded  (bool) — needs LOS?
      .TargetsAllowed      (SkillTarget flags) — what can be targeted?
      .IsAttack            (bool)
      .MaxRange            (int) — max range in tiles
      .MinRange            (int)
      .IsAlwaysHitting     (bool)
      .CanHitAnotherTile   (bool) — scatter
      .IsIgnoringCover     (bool)

    (BaseSkill as Skill)
      .GetMaxRange()       → int
      .GetMinRange()       → int
      .IsInRange(Tile target, Tile user) → bool
      .IsUsableOn(Tile target, Tile user) → bool
      .Use(Tile target, UsageParameter params) → bool  ← FIRES THE WEAPON
```

### SkillTarget Enum (Bitmask)

```csharp
None = 0
EmptyTile = 1          // ← Can target tiles with no actor!
IsolatedTile = 2
BlockedTile = 4
Structure = 1024
Self = 2048
EnemyActor = 1048576
AlliedActor = 2097152
Attack = 1049603       // = EnemyActor | EmptyTile | IsolatedTile | BlockedTile | Structure
All = 3148807
```

**Key fact:** `SkillTarget.Attack` includes `EmptyTile` — attack skills CAN target empty tiles. Suppressive fire at an empty tile is valid.

### Skill Execution Flow

```
Skill.Use(Tile target, UsageParameter)
  → ApplyToTile(target, params)
    → SpawnProjectile(... impactPoint ...)
      → Projectile travels to tile
        → OnTileHit(tile) — damages entities ON THAT TILE at impact time
```

Damage is applied to whatever is on the TARGET TILE at impact, NOT to the Actor reference. So:
- Player IS on fan tile → projectile hits, damage applied
- Player NOT there → projectile hits empty tile, no damage
- **No position leak either way**

### Range Checking

```csharp
Skill.IsInRange(Tile targetTile, Tile userTile = null, bool includeAngle = true) → bool
Skill.IsInRange(int distanceToTarget, int additionalRange) → bool
```

### Getting a Tile from Coordinates

```csharp
TacticalManager.Get().GetMap().GetTile(int x, int z) → Tile
// Map : BaseMap<Tile>, GetTile(x, z) returns Tile
// Also: map.GetTile(x, z, out Tile tile) → bool (safe version)
```

---

## Research: Attack Behavior Internals

### Attack Class Hierarchy

```
Behavior (base)
  └─ SkillBehavior
       └─ Attack (abstract)
            ├─ InflictDamage
            └─ InflictSuppression
```

### Attack Key Fields

- `m_Candidates` — `List<Attack.Data>` — scored target list
- `m_TargetTiles` — `List<Tile>` — valid target tiles
- `m_PossibleOriginTiles` — `HashSet<Tile>` — fire-from positions
- `m_PossibleTargetTiles` — `HashSet<Tile>` — fire-at positions

### Attack.Data Struct

```csharp
public struct Data {
    public Tile Target;
    public float Score;
}
```

### Attack Key Methods

```csharp
GetTargetValue(bool forImmediateUse, Skill skill, Tile target, Tile from, Tile targetedTile = null) → float  // abstract
GetHighestScoredTarget() → Attack.Data
GetUtilityFromTileMult() → float  // abstract
OnScaleMovementWeight(float scoreMult) → float
OnScaleBehaviorWeight(float scoreMult) → float
```

### SkillBehavior Key Fields

- `m_Skill` — the weapon being evaluated
- `m_TargetTile` — selected target tile
- `m_IsRotationTowardsTargetRequired`
- `m_DeployBeforeExecuting`

---

## Research: Entity.GetTile() Details

- **Location:** `Menace/Tactical/Entity.cs` line 84
- **Signature:** `public virtual Tile GetTile()` — VIRTUAL, returns Tile
- **SetTile:** `public virtual void SetTile(Tile _tile)` — also VIRTUAL, line 581
- **SetTile Harmony patch WORKS** in production (confirmed 2026-03-02 test)
- **Implication:** GetTile Harmony postfix should work too (same virtual dispatch mechanism)

---

## Key Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| GetTile not patchable | SetTile works → likely works. Fallback: direct Skill.Use() |
| Performance on hot path | Two bool checks for early exit (>99% calls exit immediately) |
| AI flees from ghost | UtilityScore injection still active as counterweight |
| Skill.Use() on empty tile crashes | SkillTarget.Attack includes EmptyTile. Test early |
| Ghost Assessment data stale | Desirable — stale = weaker threat = fading memory |
| Wildlife has no indirect weapons | Feature primarily helps vs. military enemies. Wildlife still uses movement-only ghost |

---

## Files to Modify

| File | Changes |
|------|---------|
| `BooAPeek-modpack/src/BooAPeekPlugin.cs` | GhostMemory fan tiles, FilterOpponents keeps ghosts, GetTile patch, gating, fan selection |
| `BooAPeek-modpack/modpack.json` | Remove `UnityEngine.CoreModule` (unused) |

## Verification Steps

1. `/deploy BooAPeek` — compile
2. **Test 0:** GetTile postfix fires (minimal logging test)
3. **Test 1:** Ghost stays in m_Opponents after LOS break
4. **Test 2:** GetTile returns frozen position during AI turn (log values)
5. **Test 3:** AI approaches with cover (tactical behavior, not beelining)
6. **Test 4:** AI fires at fan tile when weapon in range
7. **Test 5:** Fan spread — multiple AI units target different fan tiles
8. **Test 6:** Hit/miss — player on fan tile takes damage, otherwise projectile hits ground
