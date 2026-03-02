using System;
using Menace.SDK;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Menace.BooAPeek;

public partial class BooAPeekPlugin
{
    // ═══════════════════════════════════════════════════════════════════
    //  Opponent Filtering — strips unseen opponents, manages ghost lifecycle
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

            EnsureAwareness(factionIdx);

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

                if (isVisible)
                {
                    filtered.Add(opponent);
                    kept++;

                    try
                    {
                        var entity = new Entity(actor.Pointer);
                        var tile = entity.GetTile();
                        if (tile != null)
                        {
                            bool ghostCancelled = RecordSighting(factionIdx, actor.Pointer, tile.GetX(), tile.GetZ());
                            if (ghostCancelled)
                            {
                                ghostsRemoved++;
                                Log.Msg($"[BooAPeek] Ghost cancelled — opponent re-sighted");
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    stripped++;

                    var ghost = RecordLOSLost(factionIdx, actor.Pointer);
                    if (ghost != null)
                    {
                        ghostsCreated++;
                        Log.Msg($"[BooAPeek] Ghost pursuit started → target ({ghost.TargetX},{ghost.TargetZ}), priority {ghost.Priority}, {ghost.RoundsRemaining} rounds");
                    }
                }
            }

            if (stripped > 0)
            {
                aiFaction.m_Opponents = filtered;
                string factionName = TacticalController.GetFactionName((Menace.SDK.FactionType)factionIdx);
                Log.Msg($"[BooAPeek] {factionName}: stripped {stripped}, kept {kept}, ghosts +{ghostsCreated}/-{ghostsRemoved} (active: {GetGhostCount(factionIdx)})");
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
}
