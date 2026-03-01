using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using Menace.ModpackLoader;
using Menace.SDK;
using Il2CppInterop.Runtime.InteropTypes;

namespace Menace.BooAPeek;

public class BooAPeekPlugin : IModpackPlugin
{
    private static MelonLogger.Instance Log;
    private bool _inTactical;
    private int _initDelay;

    private const string MOD_NAME = "BooAPeek";
    private const string MOD_VERSION = "1.1.1";

    // Faction constants
    private const int FACTION_PLAYER_1 = 1;
    private const int FACTION_PLAYER_2 = 2;
    private const int FACTION_AI_MIN = 3;
    private const int FACTION_AI_MAX = 9;

    // Turn tracking
    private int _lastFactionId = -1;
    private int _lastRound = -1;

    // Reflection cache (initialized once per tactical scene)
    private bool _reflectionReady;
    private Type _aiFactType;
    private Type _oppType;
    private MethodInfo _getOpponents;
    private MethodInfo _setOpponents;
    private MethodInfo _getFaction;
    private PropertyInfo _oppActor;
    private MethodInfo _isKnown;
    private ConstructorInfo _listCtor;   // List<Opponent>() parameterless
    private MethodInfo _listAdd;         // List<Opponent>.Add(Opponent)
    private PropertyInfo _listCount;     // List<Opponent>.Count
    private PropertyInfo _listItem;      // List<Opponent>[int]
    private object _tmSingleton;

    // ═══════════════════════════════════════════════════════════════════
    //  Plugin Lifecycle
    // ═══════════════════════════════════════════════════════════════════

    public void OnInitialize(MelonLogger.Instance logger, HarmonyLib.Harmony harmony)
    {
        Log = logger;
        RegisterSettings();
        Log.Msg($"BooAPeek v{MOD_VERSION} initialized");
    }

    public void OnSceneLoaded(int buildIndex, string sceneName)
    {
        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _initDelay = 60;
            _lastFactionId = -1;
            _lastRound = -1;
            _reflectionReady = false;
            Log.Msg("BooAPeek — Tactical scene loaded, waiting for init...");
        }
        else
        {
            if (_inTactical)
                Log.Msg($"BooAPeek — Left tactical (now: {sceneName})");
            _inTactical = false;
            _reflectionReady = false;
            _tmSingleton = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Settings
    // ═══════════════════════════════════════════════════════════════════

    private static bool DebugLogging => ModSettings.Get<bool>(MOD_NAME, "DebugLogging");

    private void RegisterSettings()
    {
        ModSettings.Register(MOD_NAME, settings =>
        {
            settings.AddHeader($"BooAPeek v{MOD_VERSION}");
            settings.AddToggle("DebugLogging", "Debug Logging", false);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Main Loop
    // ═══════════════════════════════════════════════════════════════════

    public void OnUpdate()
    {
        if (!_inTactical)
            return;

        if (_initDelay > 0)
        {
            _initDelay--;
            if (_initDelay == 0)
            {
                InitReflectionCache();
                if (DebugLogging)
                {
                    Log.Msg("BooAPeek — Init complete");
                    LogActorSummary();
                }
            }
            return;
        }

        DetectTurnTransitions();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Reflection Cache
    // ═══════════════════════════════════════════════════════════════════

    private void InitReflectionCache()
    {
        try
        {
            // TacticalManager singleton
            var tmGameType = GameType.Find("Menace.Tactical.TacticalManager");
            var tmManagedType = tmGameType.ManagedType;
            var singletonProp = tmManagedType.GetProperty("s_Singleton",
                BindingFlags.Public | BindingFlags.Static);
            _tmSingleton = singletonProp.GetValue(null);
            _getFaction = tmManagedType.GetMethod("GetFaction", new[] { typeof(int) });

            // AIFaction type
            var aiFactionGameType = GameType.Find("Menace.Tactical.AI.AIFaction");
            _aiFactType = aiFactionGameType.ManagedType;
            _getOpponents = _aiFactType.GetMethod("get_m_Opponents");
            _setOpponents = _aiFactType.GetMethod("set_m_Opponents");

            // Opponent type
            var oppGameType = GameType.Find("Menace.Tactical.AI.Opponent");
            _oppType = oppGameType.ManagedType;
            _oppActor = _oppType.GetProperty("Actor");
            _isKnown = _oppType.GetMethod("IsKnown");

            // Get list type from a live faction to discover the correct generic List<Opponent>
            var testFaction = GetAIFactionProxy(6); // pirates usually exist
            if (testFaction != null)
            {
                var testList = _getOpponents.Invoke(testFaction, null);
                if (testList != null)
                {
                    var listType = testList.GetType();
                    _listCtor = listType.GetConstructor(new Type[0]);
                    _listAdd = listType.GetMethod("Add");
                    _listCount = listType.GetProperty("Count");
                    _listItem = listType.GetProperty("Item");
                }
            }

            _reflectionReady = _tmSingleton != null && _getFaction != null &&
                               _getOpponents != null && _setOpponents != null &&
                               _oppActor != null && _isKnown != null &&
                               _listCtor != null && _listAdd != null;

            if (_reflectionReady)
                Log.Msg("BooAPeek — Reflection cache ready");
            else
                Log.Warning("BooAPeek — Reflection cache incomplete, fog of war disabled");
        }
        catch (Exception ex)
        {
            Log.Error($"BooAPeek — Reflection init failed: {ex.Message}");
            _reflectionReady = false;
        }
    }

    private object GetAIFactionProxy(int factionIdx)
    {
        try
        {
            var factionObj = _getFaction.Invoke(_tmSingleton, new object[] { factionIdx });
            if (factionObj == null) return null;

            var ptrCtor = _aiFactType.GetConstructor(new[] { typeof(IntPtr) });
            return ptrCtor.Invoke(new object[] { ((Il2CppObjectBase)factionObj).Pointer });
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Turn Detection + Fog of War
    // ═══════════════════════════════════════════════════════════════════

    private void DetectTurnTransitions()
    {
        int currentFaction = TacticalController.GetCurrentFaction();
        int currentRound = TacticalController.GetCurrentRound();

        if (currentFaction < 0)
            return;

        bool factionChanged = currentFaction != _lastFactionId;
        bool roundChanged = currentRound != _lastRound;

        if (factionChanged || roundChanged)
        {
            // Always log turn transitions for validation
            if (roundChanged && currentRound > _lastRound)
                Log.Msg($"[BooAPeek] === Round {currentRound} ===");

            var factionType = (FactionType)currentFaction;
            string factionName = TacticalController.GetFactionName(factionType);
            Log.Msg($"[BooAPeek] Turn: {factionName} (faction {currentFaction})");

            _lastFactionId = currentFaction;
            _lastRound = currentRound;

            // Apply fog of war on AI faction turn start
            if (_reflectionReady &&
                currentFaction >= FACTION_AI_MIN && currentFaction <= FACTION_AI_MAX)
            {
                FilterOpponents(currentFaction);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Fog of War — Opponent Filtering
    // ═══════════════════════════════════════════════════════════════════

    private void FilterOpponents(int factionIdx)
    {
        try
        {
            var aiFaction = GetAIFactionProxy(factionIdx);
            if (aiFaction == null) return;

            var currentList = _getOpponents.Invoke(aiFaction, null);
            if (currentList == null) return;

            int count = (int)_listCount.GetValue(currentList);
            if (count == 0) return;

            var factionEnemies = GetLivingActorsForFaction(factionIdx);
            if (factionEnemies.Count == 0)
                return;

            var filteredList = _listCtor.Invoke(null);
            int kept = 0;
            int stripped = 0;

            for (int i = 0; i < count; i++)
            {
                var opponent = _listItem.GetValue(currentList, new object[] { i });
                if (opponent == null) continue;

                var actorManaged = _oppActor.GetValue(opponent);
                if (actorManaged == null) continue;

                var actorPtr = ((Il2CppObjectBase)actorManaged).Pointer;
                if (actorPtr == IntPtr.Zero) continue;

                var actorGameObj = new GameObj(actorPtr);

                bool isVisible = false;
                foreach (var enemy in factionEnemies)
                {
                    if (LineOfSight.CanActorSee(enemy, actorGameObj))
                    {
                        isVisible = true;
                        break;
                    }
                }

                if (isVisible)
                {
                    _listAdd.Invoke(filteredList, new object[] { opponent });
                    kept++;
                }
                else
                {
                    stripped++;
                }
            }

            if (stripped > 0)
            {
                _setOpponents.Invoke(aiFaction, new object[] { filteredList });
                string factionName = TacticalController.GetFactionName((FactionType)factionIdx);
                Log.Msg($"[BooAPeek] {factionName}: stripped {stripped} unseen opponent(s), kept {kept}");
            }
            else
            {
                Log.Msg($"[BooAPeek] Faction {factionIdx}: all {kept} opponent(s) visible, no filtering needed");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] FilterOpponents(f{factionIdx}) error: {ex.Message}");
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
            {
                if (!actor.IsNull && actor.IsAlive)
                    result.Add(actor);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] GetLivingActorsForFaction error: {ex.Message}");
        }
        return result;
    }

    private List<GameObj> GetLivingPlayerUnits()
    {
        var result = new List<GameObj>();
        try
        {
            var actors1 = EntitySpawner.ListEntities(FACTION_PLAYER_1);
            var actors2 = EntitySpawner.ListEntities(FACTION_PLAYER_2);

            if (actors1 != null)
                foreach (var a in actors1)
                    if (!a.IsNull && a.IsAlive) result.Add(a);

            if (actors2 != null)
                foreach (var a in actors2)
                    if (!a.IsNull && a.IsAlive) result.Add(a);
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] GetLivingPlayerUnits error: {ex.Message}");
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Debug Logging
    // ═══════════════════════════════════════════════════════════════════

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

            int playerCount = 0;
            int enemyCount = 0;

            foreach (var actor in actors)
            {
                if (actor.IsNull || !actor.IsAlive)
                    continue;

                int faction = actor.ReadInt("m_FactionID");
                if (faction == FACTION_PLAYER_1 || faction == FACTION_PLAYER_2)
                    playerCount++;
                else
                    enemyCount++;
            }

            Log.Msg($"[BooAPeek] Actors: {playerCount} player, {enemyCount} enemy");
        }
        catch (Exception ex)
        {
            Log.Error($"[BooAPeek] LogActorSummary error: {ex.Message}");
        }
    }
}
