using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Menace.SDK;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Menace.BooAPeek;

public partial class BooAPeekPlugin
{
    // ═══════════════════════════════════════════════════════════════════
    //  Awareness Recorder — mid-move sighting + faction discovery
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called from SetTile patch when any entity changes tile.
    /// If the entity moved into hostile AI vision, record the sighting.
    /// </summary>
    internal void OnEntityTileChanged(Entity entity)
    {
        try
        {
            int entityFaction = entity.GetFactionID();
            // Skip hostile AI factions — we only track targets, not the AI's own units
            if (HostileAiFactions.Contains(entityFaction)) return;

            var tile = entity.GetTile();
            if (tile == null) return;
            int x = tile.GetX(), z = tile.GetZ();

            var targetObj = new GameObj(entity.Pointer);
            bool isPlayer = PlayerFactions.Contains(entityFaction);

            foreach (int hostileFaction in HostileAiFactions)
            {
                var enemies = GetLivingActorsForFaction(hostileFaction);
                bool seen = false;
                foreach (var enemy in enemies)
                {
                    if (LineOfSight.CanActorSee(enemy, targetObj))
                    {
                        seen = true;
                        break;
                    }
                }

                if (seen)
                {
                    var awareness = EnsureAwareness(hostileFaction);
                    var prev = awareness.LastSeen.ContainsKey(entity.Pointer);
                    awareness.LastSeen[entity.Pointer] = (x, z);
                    if (!prev)
                    {
                        string tag = isPlayer ? "Player unit" : "NPC";
                        Log.Msg($"[BooAPeek] {tag} spotted mid-move at ({x},{z}) by faction {hostileFaction}");
                    }
                }
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Faction Discovery
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
