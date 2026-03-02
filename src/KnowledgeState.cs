using System;
using System.Collections.Generic;

namespace Menace.BooAPeek;

public partial class BooAPeekPlugin
{
    // ═══════════════════════════════════════════════════════════════════
    //  Knowledge State — central awareness data + transition API
    // ═══════════════════════════════════════════════════════════════════

    private class GhostMemory
    {
        public int TargetX, TargetZ;     // Last-seen player position
        public int WaypointX, WaypointZ; // Current waypoint (between AI and target)
        public int RoundsRemaining;
        public float Priority;
    }

    private class FactionAwareness
    {
        // Actor pointer → last-seen tile coordinates (updated while visible)
        public Dictionary<IntPtr, (int x, int z)> LastSeen = new();
        // Actor pointer → ghost pursuit state (created on LOS break)
        public Dictionary<IntPtr, GhostMemory> Ghosts = new();
        // Tracks whether ghosts were already updated this game round
        public bool GhostsUpdatedThisRound;
    }

    // Per-faction awareness tracking
    private Dictionary<int, FactionAwareness> _awareness = new();

    private FactionAwareness EnsureAwareness(int factionIdx)
    {
        if (!_awareness.TryGetValue(factionIdx, out var awareness))
        {
            awareness = new FactionAwareness();
            _awareness[factionIdx] = awareness;
        }
        return awareness;
    }

    private void ClearAllAwareness()
    {
        _awareness.Clear();
    }

    /// <summary>
    /// Record that a player unit is visible at (x, z) to the given faction.
    /// Returns true if an active ghost was cancelled (re-sighted).
    /// </summary>
    private bool RecordSighting(int factionIdx, IntPtr actorPtr, int x, int z)
    {
        var awareness = EnsureAwareness(factionIdx);
        awareness.LastSeen[actorPtr] = (x, z);

        if (awareness.Ghosts.TryGetValue(actorPtr, out _))
        {
            awareness.Ghosts.Remove(actorPtr);
            return true; // ghost cancelled
        }
        return false;
    }

    /// <summary>
    /// Record that a player unit has broken LOS with the given faction.
    /// Creates a ghost from LastSeen if tracked and no ghost exists.
    /// Returns the ghost if one was created, null otherwise.
    /// </summary>
    private GhostMemory RecordLOSLost(int factionIdx, IntPtr actorPtr)
    {
        if (!_awareness.TryGetValue(factionIdx, out var awareness)) return null;
        if (!awareness.LastSeen.TryGetValue(actorPtr, out var lastPos)) return null;
        if (awareness.Ghosts.ContainsKey(actorPtr)) return null;

        // Pre-multiply to compensate for the immediate decay in UpdateGhostWaypoints
        // (which runs in the same OnTurnStart Postfix before ConsiderZones evaluates)
        float initPriority = GhostInitialPriority / GhostDecay;
        int maxRounds = GhostMaxRounds;

        var ghost = new GhostMemory
        {
            TargetX = lastPos.x,
            TargetZ = lastPos.z,
            WaypointX = lastPos.x,
            WaypointZ = lastPos.z,
            RoundsRemaining = maxRounds,
            Priority = initPriority
        };
        awareness.Ghosts[actorPtr] = ghost;
        return ghost;
    }

    /// <summary>Remove an expired ghost and its LastSeen entry.</summary>
    private void ExpireGhost(int factionIdx, IntPtr actorPtr)
    {
        if (_awareness.TryGetValue(factionIdx, out var awareness))
        {
            awareness.Ghosts.Remove(actorPtr);
            awareness.LastSeen.Remove(actorPtr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Read accessors (used by GhostResponse + Patches)
    // ═══════════════════════════════════════════════════════════════════

    internal int GetGhostCount(int factionIdx)
    {
        return _awareness.TryGetValue(factionIdx, out var a) ? a.Ghosts.Count : 0;
    }

    /// <summary>
    /// Returns the raw ghost score bonus for a tile (sum of ghost priorities in zone).
    /// </summary>
    internal float GetGhostScoreBonus(int factionIdx, int tileX, int tileZ)
    {
        if (!_awareness.TryGetValue(factionIdx, out var awareness)) return 0f;
        if (awareness.Ghosts.Count == 0) return 0f;

        float bonus = 0f;
        int zoneSize = GhostZoneSize;
        foreach (var kvp in awareness.Ghosts)
        {
            var ghost = kvp.Value;
            int half = zoneSize / 2;
            if (tileX >= ghost.WaypointX - half && tileX <= ghost.WaypointX + half &&
                tileZ >= ghost.WaypointZ - half && tileZ <= ghost.WaypointZ + half)
            {
                bonus += ghost.Priority;
            }
        }
        return bonus;
    }
}
