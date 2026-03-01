# BooAPeek v2.1.0 Release Plan — "I Saw You There"

## Context

Ghost awareness system is confirmed working (2026-03-02): AI investigates last-known player position via UtilityScore injection in ConsiderZones.Evaluate postfix. But:
1. Testing was in a kill mission with NO zones — UtilityScore from game zones was always 0
2. Ghost priority (flat 20.0) might dominate or be ignored vs. real mission zone scores

The solution: keep the existing ConsiderZones.Evaluate postfix approach (it works, ~50 tiles/actor is cheap) but replace the flat +20.0 bonus with an **auto-calibrating spread-based bonus**. The ghost bonus scales to the range of scores the AI is already choosing between.

The iterative Agent loop (up to 16 Evaluate→Pick→Execute cycles per turn) means the existing UtilityScore injection already acts as a movement nudge — no additional hooks needed.

## Implementation: Spread-Based Auto-Calibrating Ghost Bonus

### What Changes

Replace `GetGhostScoreBonus()` returning flat `ghost.Priority` (20→10→5) with:

```
spread = observedMaxUtility - observedMinUtility  (from previous round)
ghostBonus = max(MIN_FLOOR, spread * GHOST_FRACTION) * decayFactor
```

Keep everything else: 5×5 ghost zone, GhostMemory, FactionAwareness, FilterOpponents, UpdateGhostWaypoints, SetTile patch, OnRoundStart.

### New Constants

```csharp
const float GHOST_FRACTION = 0.33f;    // Ghost bonus = 33% of score spread
const float GHOST_MIN_FLOOR = 5.0f;    // Minimum bonus (when spread ≈ 0)
```

Replace `GHOST_ZONE_INITIAL_PRIORITY = 20.0f` — no longer used as the bonus value.

### New State (per faction)

```csharp
private static Dictionary<int, float> _observedMax = new();   // current round tracking
private static Dictionary<int, float> _observedMin = new();
private static Dictionary<int, float> _calibratedSpread = new();  // snapshot from previous round
```

### Why It Works

| Scenario | Spread | Ghost Bonus (round 1) | Effect |
|----------|--------|----------------------|--------|
| Kill mission (no zones) | ~0 | MIN_FLOOR = 5.0 | Ghost is only differentiator → decisive |
| Weak patrol zones | ~10 | 10 × 0.33 = 3.3 | Tips close decisions toward ghost |
| Strong migration zone | ~100 | 100 × 0.33 = 33 | Noticeable but 100-score zone still wins |
| Critical objective | ~200 | 200 × 0.33 = 66 | Strong but objective at 200 overrides |

Decay factor still applies: round 1 = full, round 2 = ×0.5, round 3 = ×0.25.

### Code Changes in BooAPeekPlugin.cs

**1. OnRoundStart patch** — add spread snapshot + reset:
```csharp
// After existing GhostsUpdatedThisRound reset:
int fIdx = faction.GetIndex();
float max = _observedMax.GetValueOrDefault(fIdx, 0f);
float min = _observedMin.GetValueOrDefault(fIdx, 0f);
_calibratedSpread[fIdx] = max - min;
_observedMax[fIdx] = float.MinValue;
_observedMin[fIdx] = float.MaxValue;
```

**2. ConsiderZones.Evaluate Postfix** — replace bonus calculation:
```csharp
// Track game's UtilityScore BEFORE our bonus
float gameUtility = _tile.UtilityScore;
int fIdx = /* current faction index */;
if (gameUtility > _observedMax.GetValueOrDefault(fIdx, float.MinValue))
    _observedMax[fIdx] = gameUtility;
if (gameUtility < _observedMin.GetValueOrDefault(fIdx, float.MaxValue))
    _observedMin[fIdx] = gameUtility;

// Apply ghost bonus using PREVIOUS round's spread
float ghostBonus = GetGhostScoreBonus(tile, fIdx);
if (ghostBonus > 0f) {
    float spread = _calibratedSpread.GetValueOrDefault(fIdx, 0f);
    float scaledBonus = Math.Max(GHOST_MIN_FLOOR, spread * GHOST_FRACTION);
    float decayFactor = ghostBonus / GHOST_ZONE_INITIAL_PRIORITY;  // reuse existing decay
    _tile.UtilityScore += scaledBonus * decayFactor;
}
```

Round 1 of a new mission uses MIN_FLOOR (no previous data). Round 2+ auto-calibrates.

**3. GetGhostScoreBonus** — keep as-is (returns raw priority for tiles in 5×5 zone). Used only for "is this tile ghosted?" check + decay factor extraction.

**4. Diagnostic logging** — add spread tracking to existing `[SCORES]` log:
```
[SCORES] factionIdx=7 spread=42.3 ghostBonus=13.9 (decay=1.0)
```

### Open Question

Spread stability between rounds is unverified. Zone positions are constant, but the evaluated tile set shifts as units move. MIN_FLOOR (5.0) provides a safety net if spread collapses. If testing shows instability, consider EMA smoothing: `smoothedSpread = 0.7 * prev + 0.3 * observed`.

## Cleanup for Release

- [ ] Gate `[DIAG]`, `[SCORES]`, `[GHOST]` logging behind `DebugLogging` setting
- [ ] Remove `UnityEngine.CoreModule` from `modpack.json`
- [ ] Version bump `modpack.json` → `2.1.0`
- [ ] Update `README.md` — awareness persistence section
- [ ] Update `CHANGELOG.md` — v2.1.0 entry

## Files to Modify

| File | Changes |
|------|---------|
| `BooAPeek-modpack/src/BooAPeekPlugin.cs` | Add spread tracking state, modify OnRoundStart (snapshot spread), modify ConsiderZones postfix (track min/max + scaled bonus), gate diagnostic logging |
| `BooAPeek-modpack/modpack.json` | Version 2.1.0, remove `UnityEngine.CoreModule` |
| `BooAPeek-modpack/README.md` | v2.1 awareness persistence docs |
| `BooAPeek-modpack/CHANGELOG.md` | v2.1.0 entry |

## Verification

1. `/deploy BooAPeek` → compile succeeds
2. **Kill mission (no zones):** Peek + retreat → AI nudged toward ghost (spread ≈ 0, bonus = MIN_FLOOR = 5.0). Check `[SCORES]` log confirms spread ≈ 0 and bonus = 5.0.
3. **Mission with objectives:** Peek + retreat near migration path → check `[SCORES]` log for spread value. AI investigates ghost without abandoning migration. Ghost bonus should be ~33% of spread.
4. **Spread stability:** Observe `[SCORES]` spread values across 3+ rounds — verify they stay in same order of magnitude.
5. **Ghost expiry:** Wait 3 rounds → decay factor drops (1.0 → 0.5 → 0.25) → AI resumes normal behavior.
6. **No ghost:** AI behaves identically to v2.0 (bonus is no-op when no ghosts active).
