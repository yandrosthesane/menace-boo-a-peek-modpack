using System;
using System.Collections.Generic;
using Menace.SDK;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Menace.BooAPeek;

public partial class BooAPeekPlugin
{
    // ═══════════════════════════════════════════════════════════════════
    //  Ghost Response — waypoint computation + spread calibration
    // ═══════════════════════════════════════════════════════════════════

    // Per-faction spread calibration (for auto-scaling ghost bonus)
    private Dictionary<int, float> _observedMax = new();
    private Dictionary<int, float> _observedMin = new();
    private Dictionary<int, float> _calibratedSpread = new();

    private void ClearCalibration()
    {
        _observedMax.Clear();
        _observedMin.Clear();
        _calibratedSpread.Clear();
    }

    /// <summary>
    /// Called from OnTurnStart postfix — updates ghost waypoints once per round.
    /// </summary>
    internal void UpdateGhostWaypoints(AIFaction aiFaction)
    {
        try
        {
            int factionIdx = aiFaction.GetIndex();
            if (!_awareness.TryGetValue(factionIdx, out var awareness)) return;
            if (awareness.GhostsUpdatedThisRound || awareness.Ghosts.Count == 0) return;

            awareness.GhostsUpdatedThisRound = true;
            var factionEnemies = GetLivingActorsForFaction(factionIdx);

            var expired = new List<IntPtr>();
            foreach (var kvp in awareness.Ghosts)
            {
                var ghost = kvp.Value;
                ghost.RoundsRemaining--;
                if (ghost.RoundsRemaining <= 0)
                {
                    expired.Add(kvp.Key);
                    Log.Msg($"[BooAPeek] Ghost expired at ({ghost.TargetX},{ghost.TargetZ})");
                    continue;
                }

                // Find nearest AI unit to the target
                int nearestX = ghost.TargetX, nearestZ = ghost.TargetZ;
                float nearestDist = float.MaxValue;
                foreach (var enemy in factionEnemies)
                {
                    try
                    {
                        var entity = new Entity(enemy.Pointer);
                        var tile = entity.GetTile();
                        if (tile == null) continue;
                        int ex = tile.GetX(), ez = tile.GetZ();
                        float dx = ghost.TargetX - ex, dz = ghost.TargetZ - ez;
                        float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestX = ex;
                            nearestZ = ez;
                        }
                    }
                    catch { }
                }

                // Compute waypoint toward target
                if (nearestDist <= GhostWaypointDist)
                {
                    ghost.WaypointX = ghost.TargetX;
                    ghost.WaypointZ = ghost.TargetZ;
                }
                else
                {
                    float ratio = GhostWaypointDist / nearestDist;
                    ghost.WaypointX = nearestX + (int)((ghost.TargetX - nearestX) * ratio);
                    ghost.WaypointZ = nearestZ + (int)((ghost.TargetZ - nearestZ) * ratio);
                }

                // Decay priority for next round
                ghost.Priority *= GhostDecay;

                Log.Msg($"[BooAPeek] Ghost waypoint at ({ghost.WaypointX},{ghost.WaypointZ}) priority {ghost.Priority:F1}, {ghost.RoundsRemaining} rounds left, nearest AI at ({nearestX},{nearestZ}) dist {nearestDist:F0}");
            }

            foreach (var ptr in expired)
                ExpireGhost(factionIdx, ptr);
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] UpdateGhostWaypoints error: {ex.Message}");
        }
    }

    internal void ResetRoundFlag(int factionIdx)
    {
        if (_awareness.TryGetValue(factionIdx, out var awareness))
            awareness.GhostsUpdatedThisRound = false;

        // Snapshot this round's observed spread for next round's calibration
        float max = _observedMax.GetValueOrDefault(factionIdx, 0f);
        float min = _observedMin.GetValueOrDefault(factionIdx, 0f);
        float spread = max > min ? max - min : 0f;
        _calibratedSpread[factionIdx] = spread;

        // Reset for next round's observations
        _observedMax[factionIdx] = float.MinValue;
        _observedMin[factionIdx] = float.MaxValue;

        if (DebugLogging && spread > 0f)
            Log.Msg($"[BooAPeek] Round spread snapshot: faction {factionIdx} spread={spread:F1} (max={max:F1}, min={min:F1})");
    }

    /// <summary>
    /// Track game's UtilityScore for a tile (before our ghost bonus).
    /// Updates per-faction min/max for spread calibration.
    /// </summary>
    internal void TrackTileScore(int factionIdx, float gameUtility)
    {
        if (gameUtility > _observedMax.GetValueOrDefault(factionIdx, float.MinValue))
            _observedMax[factionIdx] = gameUtility;
        if (gameUtility < _observedMin.GetValueOrDefault(factionIdx, float.MaxValue))
            _observedMin[factionIdx] = gameUtility;
    }

    /// <summary>
    /// Returns the spread-scaled ghost bonus for a tile, or 0 if not in a ghost zone.
    /// </summary>
    internal float GetCalibratedGhostBonus(int factionIdx, int tileX, int tileZ)
    {
        float rawBonus = GetGhostScoreBonus(factionIdx, tileX, tileZ);
        if (rawBonus <= 0f) return 0f;

        float spread = _calibratedSpread.GetValueOrDefault(factionIdx, 0f);
        float scaledBonus = Math.Max(GhostMinFloor, spread * GhostFraction);
        float decayFactor = rawBonus / GhostInitialPriority;
        return scaledBonus * decayFactor;
    }
}
