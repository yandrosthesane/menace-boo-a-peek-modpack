using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using Menace.ModpackLoader;
using Menace.SDK;
using Il2CppInterop.Runtime;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Menace.BooAPeek;

public class BooAPeekPlugin : IModpackPlugin
{
    internal static MelonLogger.Instance Log;
    internal static BooAPeekPlugin Instance;

    private HarmonyLib.Harmony _harmony;
    private bool _inTactical;
    private int _initDelay;
    private bool _ready;

    private const string MOD_NAME = "BooAPeek";
    private const string MOD_VERSION = "2.1.0";
    private const int FACTION_PROBE_MAX = 15;

    // Per-faction awareness tracking
    private Dictionary<int, FactionAwareness> _awareness = new();

    // Per-faction spread calibration (for auto-scaling ghost bonus)
    private Dictionary<int, float> _observedMax = new();
    private Dictionary<int, float> _observedMin = new();
    private Dictionary<int, float> _calibratedSpread = new();

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

    // Faction classification (discovered per mission)
    internal HashSet<int> HostileAiFactions = new();
    internal HashSet<int> AlliedAiFactions = new();
    internal List<int> PlayerFactions = new();

    // ═══════════════════════════════════════════════════════════════════
    //  Plugin Lifecycle
    // ═══════════════════════════════════════════════════════════════════

    public void OnInitialize(MelonLogger.Instance logger, HarmonyLib.Harmony harmony)
    {
        Log = logger;
        Instance = this;
        _harmony = harmony;

        RegisterSettings();
        _harmony.PatchAll(typeof(BooAPeekPlugin).Assembly);

        Log.Msg($"BooAPeek v{MOD_VERSION} initialized (Harmony patches applied)");
    }

    public void OnSceneLoaded(int buildIndex, string sceneName)
    {
        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _initDelay = 60;
            _ready = false;
            _awareness.Clear();
            _observedMax.Clear();
            _observedMin.Clear();
            _calibratedSpread.Clear();
            Log.Msg("BooAPeek — Tactical scene loaded, waiting for init...");
        }
        else
        {
            if (_inTactical)
                Log.Msg($"BooAPeek — Left tactical (now: {sceneName})");
            _inTactical = false;
            _ready = false;
            _awareness.Clear();
            _observedMax.Clear();
            _observedMin.Clear();
            _calibratedSpread.Clear();
        }
    }

    public void OnUpdate()
    {
        if (!_inTactical || _ready)
            return;

        if (_initDelay > 0)
        {
            _initDelay--;
            if (_initDelay == 0)
            {
                DiscoverFactions();
                if (DebugLogging)
                    LogActorSummary();
            }
        }
    }

    internal bool IsReady => _ready;

    /// <summary>
    /// Called from ConsiderZones.Evaluate postfix — directly boosts tile utility for ghost targets.
    /// Returns the score bonus for a tile at (tileX, tileZ) for the given faction.
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
            // Check if tile is inside the ghost's waypoint zone area
            if (tileX >= ghost.WaypointX - half && tileX <= ghost.WaypointX + half &&
                tileZ >= ghost.WaypointZ - half && tileZ <= ghost.WaypointZ + half)
            {
                bonus += ghost.Priority;
            }
        }
        return bonus;
    }

    /// <summary>
    /// Called from OnTurnStart postfix — updates ghost waypoints once per round.
    /// No longer creates Zone objects; just computes waypoint positions for score injection.
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
            {
                awareness.Ghosts.Remove(ptr);
                awareness.LastSeen.Remove(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] UpdateGhostWaypoints error: {ex.Message}");
        }
    }

    internal int GetGhostCount(int factionIdx)
    {
        return _awareness.TryGetValue(factionIdx, out var a) ? a.Ghosts.Count : 0;
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

    /// <summary>
    /// Called from SetTile patch when any entity changes tile.
    /// If a player unit moved into hostile AI vision, record the sighting.
    /// </summary>
    internal void OnEntityTileChanged(Entity entity)
    {
        try
        {
            int entityFaction = entity.GetFactionID();
            if (!PlayerFactions.Contains(entityFaction)) return;

            var tile = entity.GetTile();
            if (tile == null) return;
            int x = tile.GetX(), z = tile.GetZ();

            var playerObj = new GameObj(entity.Pointer);

            foreach (int hostileFaction in HostileAiFactions)
            {
                var enemies = GetLivingActorsForFaction(hostileFaction);
                bool seen = false;
                foreach (var enemy in enemies)
                {
                    if (LineOfSight.CanActorSee(enemy, playerObj))
                    {
                        seen = true;
                        break;
                    }
                }

                if (seen)
                {
                    if (!_awareness.TryGetValue(hostileFaction, out var awareness))
                    {
                        awareness = new FactionAwareness();
                        _awareness[hostileFaction] = awareness;
                    }
                    var prev = awareness.LastSeen.ContainsKey(entity.Pointer);
                    awareness.LastSeen[entity.Pointer] = (x, z);
                    if (!prev)
                        Log.Msg($"[BooAPeek] Player unit spotted mid-move at ({x},{z}) by faction {hostileFaction}");
                }
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Settings
    // ═══════════════════════════════════════════════════════════════════

    internal static bool DebugLogging => ModSettings.Get<bool>(MOD_NAME, "DebugLogging");
    internal static int GhostZoneSize => ModSettings.Get<int>(MOD_NAME, "GhostZoneSize");
    internal static float GhostInitialPriority => ModSettings.Get<float>(MOD_NAME, "GhostInitialPriority");
    internal static float GhostDecay => ModSettings.Get<float>(MOD_NAME, "GhostDecay");
    internal static int GhostMaxRounds => ModSettings.Get<int>(MOD_NAME, "GhostMaxRounds");
    internal static int GhostWaypointDist => ModSettings.Get<int>(MOD_NAME, "GhostWaypointDist");
    internal static float GhostFraction => ModSettings.Get<float>(MOD_NAME, "GhostFraction");
    internal static float GhostMinFloor => ModSettings.Get<float>(MOD_NAME, "GhostMinFloor");
    internal static int DebugLogLimit => ModSettings.Get<int>(MOD_NAME, "DebugLogLimit");

    private void RegisterSettings()
    {
        ModSettings.Register(MOD_NAME, settings =>
        {
            settings.AddHeader($"BooAPeek v{MOD_VERSION}");
            settings.AddToggle("DebugLogging", "Debug Logging", false);
            settings.AddHeader("Ghost Awareness");
            settings.AddNumber("GhostZoneSize", "Zone Size (tiles)", 1, 11, 5);
            settings.AddSlider("GhostInitialPriority", "Initial Priority", 1f, 100f, 20f);
            settings.AddSlider("GhostDecay", "Decay Per Round", 0.1f, 1f, 0.5f);
            settings.AddNumber("GhostMaxRounds", "Max Rounds", 1, 10, 3);
            settings.AddNumber("GhostWaypointDist", "Waypoint Distance", 1, 20, 6);
            settings.AddHeader("Auto-Calibration");
            settings.AddSlider("GhostFraction", "Spread Fraction", 0.05f, 1f, 0.33f);
            settings.AddSlider("GhostMinFloor", "Minimum Bonus", 0.5f, 50f, 20f);
            settings.AddHeader("Diagnostics");
            settings.AddNumber("DebugLogLimit", "Log Lines Per Actor", 1, 50, 5);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Faction Discovery (direct Il2Cpp types, no reflection)
    // ═══════════════════════════════════════════════════════════════════

    private void DiscoverFactions()
    {
        try
        {
            var tm = TacticalManager.s_Singleton;
            if (tm == null)
            {
                Log.Warning("BooAPeek — TacticalManager singleton is null");
                return;
            }

            HostileAiFactions.Clear();
            AlliedAiFactions.Clear();
            PlayerFactions.Clear();

            for (int i = 0; i <= FACTION_PROBE_MAX; i++)
            {
                try
                {
                    var baseFaction = tm.GetFaction(i);
                    if (baseFaction == null) continue;

                    var aiFaction = baseFaction.TryCast<AIFaction>();
                    if (aiFaction == null)
                    {
                        // Non-AI faction — check if it has living actors (player side)
                        var actors = EntitySpawner.ListEntities(i);
                        if (actors != null && actors.Length > 0)
                            PlayerFactions.Add(i);
                        continue;
                    }

                    if (aiFaction.m_IsAlliedWithPlayer)
                        AlliedAiFactions.Add(i);
                    else
                        HostileAiFactions.Add(i);
                }
                catch { }
            }

            _ready = HostileAiFactions.Count > 0;

            if (_ready)
            {
                Log.Msg("BooAPeek — Faction discovery complete");
                LogFactionDiscovery();
            }
            else
                Log.Warning("BooAPeek — No hostile AI factions found, fog of war disabled");
        }
        catch (Exception ex)
        {
            Log.Error($"BooAPeek — Faction discovery failed: {ex.Message}");
            _ready = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Opponent Filtering (called from Harmony patch)
    // ═══════════════════════════════════════════════════════════════════

    internal void FilterOpponents(AIFaction aiFaction)
    {
        try
        {
            var opponents = aiFaction.m_Opponents;
            if (opponents == null || opponents.Count == 0) return;

            int factionIdx = aiFaction.GetIndex();
            var factionEnemies = GetLivingActorsForFaction(factionIdx);
            if (factionEnemies.Count == 0) return;

            if (!_awareness.TryGetValue(factionIdx, out var awareness))
            {
                awareness = new FactionAwareness();
                _awareness[factionIdx] = awareness;
            }

            var filtered = new Il2CppSystem.Collections.Generic.List<Opponent>();
            int kept = 0, stripped = 0, ghostsCreated = 0, ghostsRemoved = 0;

            for (int i = 0; i < opponents.Count; i++)
            {
                var opponent = opponents[i];
                if (opponent == null) continue;

                var actor = opponent.Actor;
                if (actor == null) continue;

                var targetObj = new GameObj(actor.Pointer);
                bool isVisible = false;

                foreach (var enemy in factionEnemies)
                {
                    if (LineOfSight.CanActorSee(enemy, targetObj))
                    {
                        isVisible = true;
                        break;
                    }
                }

                // Only track awareness for player faction opponents
                bool isPlayerUnit = false;
                try
                {
                    var actorEntity = new Entity(actor.Pointer);
                    int actorFaction = actorEntity.GetFactionID();
                    isPlayerUnit = PlayerFactions.Contains(actorFaction);
                }
                catch { }

                if (isVisible)
                {
                    filtered.Add(opponent);
                    kept++;

                    if (isPlayerUnit)
                    {
                        // Record current position — snapshot for ghost zone if LOS breaks later
                        var entity = new Entity(actor.Pointer);
                        var tile = entity.GetTile();
                        if (tile != null)
                            awareness.LastSeen[actor.Pointer] = (tile.GetX(), tile.GetZ());

                        // If re-sighted while ghost active, cancel the pursuit
                        if (awareness.Ghosts.TryGetValue(actor.Pointer, out var ghost))
                        {
                            awareness.Ghosts.Remove(actor.Pointer);
                            ghostsRemoved++;
                            Log.Msg($"[BooAPeek] Ghost cancelled — player re-sighted");
                        }
                    }
                }
                else
                {
                    stripped++;

                    // Player unit lost LOS — start ghost pursuit if we have a last-known position
                    if (isPlayerUnit
                        && awareness.LastSeen.TryGetValue(actor.Pointer, out var lastPos)
                        && !awareness.Ghosts.ContainsKey(actor.Pointer))
                    {
                        // Pre-multiply to compensate for the immediate decay in UpdateGhostWaypoints
                        // (which runs in the same OnTurnStart Postfix before ConsiderZones evaluates)
                        float initPriority = GhostInitialPriority / GhostDecay;
                        int maxRounds = GhostMaxRounds;
                        awareness.Ghosts[actor.Pointer] = new GhostMemory
                        {
                            TargetX = lastPos.x,
                            TargetZ = lastPos.z,
                            WaypointX = lastPos.x,
                            WaypointZ = lastPos.z,
                            RoundsRemaining = maxRounds,
                            Priority = initPriority
                        };
                        ghostsCreated++;
                        Log.Msg($"[BooAPeek] Ghost pursuit started → target ({lastPos.x},{lastPos.z}), priority {initPriority}, {maxRounds} rounds");
                    }
                }
            }

            if (stripped > 0)
            {
                aiFaction.m_Opponents = filtered;
                string factionName = TacticalController.GetFactionName((Menace.SDK.FactionType)factionIdx);
                Log.Msg($"[BooAPeek] {factionName}: stripped {stripped}, kept {kept}, ghosts +{ghostsCreated}/-{ghostsRemoved} (active: {awareness.Ghosts.Count})");
            }
            else if (DebugLogging)
            {
                Log.Msg($"[BooAPeek] Faction {factionIdx}: all {kept} opponent(s) visible");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] FilterOpponents error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Old UpdateGhostZones removed — replaced by UpdateGhostWaypoints + direct score injection

    // ═══════════════════════════════════════════════════════════════════
    //  Actor Enumeration
    // ═══════════════════════════════════════════════════════════════════

    private List<GameObj> GetLivingActorsForFaction(int factionIdx)
    {
        var result = new List<GameObj>();
        try
        {
            var actors = EntitySpawner.ListEntities(factionIdx);
            if (actors == null) return result;
            foreach (var actor in actors)
                if (!actor.IsNull && actor.IsAlive) result.Add(actor);
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] GetLivingActorsForFaction error: {ex.Message}");
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Debug Logging
    // ═══════════════════════════════════════════════════════════════════

    private void LogFactionDiscovery()
    {
        foreach (int id in HostileAiFactions)
        {
            string name = TacticalController.GetFactionName((Menace.SDK.FactionType)id);
            int count = GetLivingActorsForFaction(id).Count;
            Log.Msg($"[BooAPeek]   Hostile AI: {name} (faction {id}) — {count} unit(s) — filtering ACTIVE");
        }
        foreach (int id in AlliedAiFactions)
        {
            string name = TacticalController.GetFactionName((Menace.SDK.FactionType)id);
            int count = GetLivingActorsForFaction(id).Count;
            Log.Msg($"[BooAPeek]   Allied AI:  {name} (faction {id}) — {count} unit(s) — filtering SKIPPED");
        }
        foreach (int id in PlayerFactions)
        {
            string name = TacticalController.GetFactionName((Menace.SDK.FactionType)id);
            int count = GetLivingActorsForFaction(id).Count;
            Log.Msg($"[BooAPeek]   Player:     {name} (faction {id}) — {count} unit(s)");
        }
    }

    private void LogActorSummary()
    {
        try
        {
            var actors = EntitySpawner.ListEntities(-1);
            if (actors == null || actors.Length == 0)
            {
                Log.Msg("[BooAPeek] No actors found");
                return;
            }

            int playerCount = 0, enemyCount = 0;
            foreach (var actor in actors)
            {
                if (actor.IsNull || !actor.IsAlive) continue;
                int faction = actor.ReadInt("m_FactionID");
                if (HostileAiFactions.Contains(faction)) enemyCount++;
                else playerCount++;
            }
            Log.Msg($"[BooAPeek] Actors: {playerCount} player/allied, {enemyCount} hostile AI");
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] LogActorSummary error: {ex.Message}");
        }
    }
}

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
