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

public partial class BooAPeekPlugin : IModpackPlugin
{
    internal static MelonLogger.Instance Log;
    internal static BooAPeekPlugin Instance;

    private HarmonyLib.Harmony _harmony;
    private bool _inTactical;
    private int _initDelay;
    private bool _ready;

    private const string MOD_NAME = "BooAPeek";
    private const string MOD_VERSION = "2.2.0";
    private const int FACTION_PROBE_MAX = 15;

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
            ClearAllAwareness();
            ClearCalibration();
            Log.Msg("BooAPeek — Tactical scene loaded, waiting for init...");
        }
        else
        {
            if (_inTactical)
                Log.Msg($"BooAPeek — Left tactical (now: {sceneName})");
            _inTactical = false;
            _ready = false;
            ClearAllAwareness();
            ClearCalibration();
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
}
