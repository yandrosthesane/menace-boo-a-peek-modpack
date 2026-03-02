using System;
using System.Collections.Generic;
using HarmonyLib;
using Menace.SDK;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Menace.BooAPeek;

// ═══════════════════════════════════════════════════════════════════
//  Harmony Patches — fire before AI thinks, guaranteed timing
// ═══════════════════════════════════════════════════════════════════

[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnTurnStart))]
static class Patch_AIFaction_OnTurnStart
{
    static void Prefix(AIFaction __instance)
    {
        try
        {
            var plugin = BooAPeekPlugin.Instance;
            if (plugin == null || !plugin.IsReady) return;

            int factionIdx = __instance.GetIndex();
            if (!plugin.HostileAiFactions.Contains(factionIdx)) return;

            plugin.FilterOpponents(__instance);
        }
        catch (Exception ex)
        {
            BooAPeekPlugin.Log.Error($"[BooAPeek] OnTurnStart error: {ex.Message}");
        }
    }

    static void Postfix(AIFaction __instance)
    {
        try
        {
            var plugin = BooAPeekPlugin.Instance;
            if (plugin == null || !plugin.IsReady) return;

            int factionIdx = __instance.GetIndex();
            if (!plugin.HostileAiFactions.Contains(factionIdx)) return;

            plugin.UpdateGhostWaypoints(__instance);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnRoundStart))]
static class Patch_AIFaction_OnRoundStart
{
    static void Prefix(AIFaction __instance)
    {
        try
        {
            var plugin = BooAPeekPlugin.Instance;
            if (plugin == null || !plugin.IsReady) return;

            int factionIdx = __instance.GetIndex();
            if (!plugin.HostileAiFactions.Contains(factionIdx)) return;

            plugin.ResetRoundFlag(factionIdx);
        }
        catch { }
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Mid-movement sighting: record LOS when player unit changes tile
// ═══════════════════════════════════════════════════════════════════

[HarmonyPatch(typeof(Entity), nameof(Entity.SetTile))]
static class Patch_Entity_SetTile
{
    static void Postfix(Entity __instance)
    {
        try
        {
            var plugin = BooAPeekPlugin.Instance;
            if (plugin == null || !plugin.IsReady) return;
            plugin.OnEntityTileChanged(__instance);
        }
        catch { }
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Ghost score injection via ConsiderZones criterion (spread-based)
// ═══════════════════════════════════════════════════════════════════

[HarmonyPatch(typeof(Il2CppMenace.Tactical.AI.Behaviors.Criterions.ConsiderZones), nameof(Il2CppMenace.Tactical.AI.Behaviors.Criterions.ConsiderZones.Evaluate))]
static class Patch_ConsiderZones_Evaluate
{
    static int _callCount = 0;
    static int _ghostHits = 0;
    static int _factionId = -1;
    static IntPtr _lastActor = IntPtr.Zero;

    // Per-actor score tracking for diagnostics
    static float _minUtility = float.MaxValue, _maxUtility = float.MinValue;
    static float _minSafety = float.MaxValue, _maxSafety = float.MinValue;
    static float _minDistance = float.MaxValue, _maxDistance = float.MinValue;

    struct TileInfo
    {
        public int X, Z;
        public float Utility, Safety, Distance, GhostBonus;
        public bool Visible;
    }
    static List<TileInfo> _topTiles = new();
    const int TOP_N = 5;

    static void LogActorSummary()
    {
        if (_lastActor == IntPtr.Zero || !BooAPeekPlugin.DebugLogging || _callCount == 0) return;

        BooAPeekPlugin.Log.Msg($"[BooAPeek][SCORES] faction {_factionId}: {_callCount} tiles, {_ghostHits} ghost hits | Util=[{_minUtility:F1},{_maxUtility:F1}] Safe=[{_minSafety:F1},{_maxSafety:F1}] Dist=[{_minDistance:F1},{_maxDistance:F1}]");

        // Log top tiles sorted by utility descending
        _topTiles.Sort((a, b) => b.Utility.CompareTo(a.Utility));
        int limit = Math.Min(_topTiles.Count, TOP_N);
        for (int i = 0; i < limit; i++)
        {
            var t = _topTiles[i];
            string ghost = t.GhostBonus > 0 ? $" ghost=+{t.GhostBonus:F1}" : "";
            string vis = t.Visible ? " VIS" : "";
            BooAPeekPlugin.Log.Msg($"[BooAPeek][TOP{i + 1}] ({t.X},{t.Z}) util={t.Utility:F1} safe={t.Safety:F1} dist={t.Distance:F1}{ghost}{vis}");
        }
    }

    static void Postfix(Actor _actor, Il2CppMenace.Tactical.AI.Data.TileScore _tile)
    {
        var plugin = BooAPeekPlugin.Instance;
        if (plugin == null || !plugin.IsReady) return;

        // Reset per-actor state
        if (_actor.Pointer != _lastActor)
        {
            LogActorSummary();

            _lastActor = _actor.Pointer;
            _callCount = 0;
            _ghostHits = 0;
            _minUtility = float.MaxValue; _maxUtility = float.MinValue;
            _minSafety = float.MaxValue; _maxSafety = float.MinValue;
            _minDistance = float.MaxValue; _maxDistance = float.MinValue;
            _topTiles.Clear();

            try
            {
                var entity = new Entity(_actor.Pointer);
                _factionId = entity.GetFactionID();
            }
            catch { _factionId = -1; }

            if (BooAPeekPlugin.DebugLogging)
            {
                try
                {
                    var e = new Entity(_actor.Pointer);
                    var t = e.GetTile();
                    int ax = t != null ? t.GetX() : -1, az = t != null ? t.GetZ() : -1;
                    BooAPeekPlugin.Log.Msg($"[BooAPeek][DIAG] Actor at ({ax},{az}) faction {_factionId}, ghosts active: {plugin.GetGhostCount(_factionId)}");
                }
                catch { }
            }
        }
        _callCount++;

        if (_factionId < 0) return;

        try
        {
            var tile = _tile.Tile;
            if (tile == null) return;

            int tileX = tile.GetX(), tileZ = tile.GetZ();
            float utilBefore = _tile.UtilityScore;
            float safety = _tile.SafetyScore;
            float distance = _tile.DistanceScore;

            // Track ranges
            if (utilBefore > _maxUtility) _maxUtility = utilBefore;
            if (utilBefore < _minUtility) _minUtility = utilBefore;
            if (safety > _maxSafety) _maxSafety = safety;
            if (safety < _minSafety) _minSafety = safety;
            if (distance > _maxDistance) _maxDistance = distance;
            if (distance < _minDistance) _minDistance = distance;

            // Track game's UtilityScore for spread calibration (before our bonus)
            plugin.TrackTileScore(_factionId, utilBefore);

            // Apply spread-calibrated ghost bonus
            float ghostBonus = plugin.GetCalibratedGhostBonus(_factionId, tileX, tileZ);
            if (ghostBonus > 0f)
            {
                _tile.UtilityScore += ghostBonus;
                _ghostHits++;
            }

            // Track top tiles by utility (after ghost bonus)
            if (BooAPeekPlugin.DebugLogging)
            {
                bool vis = false;
                try { vis = _tile.IsVisibleToOpponentsHere; } catch { }

                var info = new TileInfo
                {
                    X = tileX, Z = tileZ,
                    Utility = _tile.UtilityScore,
                    Safety = safety,
                    Distance = distance,
                    GhostBonus = ghostBonus,
                    Visible = vis
                };

                if (_topTiles.Count < TOP_N)
                {
                    _topTiles.Add(info);
                }
                else
                {
                    // Replace lowest-utility entry if this one is higher
                    int minIdx = 0;
                    float minVal = _topTiles[0].Utility;
                    for (int i = 1; i < _topTiles.Count; i++)
                    {
                        if (_topTiles[i].Utility < minVal)
                        {
                            minVal = _topTiles[i].Utility;
                            minIdx = i;
                        }
                    }
                    if (info.Utility > minVal)
                        _topTiles[minIdx] = info;
                }
            }
        }
        catch { }
    }
}
