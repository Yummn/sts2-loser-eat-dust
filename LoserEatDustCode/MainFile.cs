using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using System.Reflection;

namespace LoserEatDust;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "LoserEatDust";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        var android = IsAndroidRuntime();
        if (android)
        {
            // Harmony's ARM64 native detour builder is unstable while several
            // mods are initialized back-to-back. Android can observe both the
            // save file and pause-menu scene directly, so avoid every native
            // detour here instead of merely removing one redundant save hook.
            LoserEatDustAndroidWatcher.Install();
            Logger.Info("[败者食尘] loaded v0.2.4: Android uses a detour-free save/pause watcher; every visited node in the current act can be restored; BaseLib not required.");
            return;
        }

        var harmony = new Harmony(ModId);
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes()
                     .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            Logger.Info($"[败者食尘] patching {type.FullName}");
            harmony.CreateClassProcessor(type).Patch();
            Logger.Info($"[败者食尘] patched {type.FullName}");
        }
        Logger.Info("[败者食尘] loaded v0.2.4: desktop save hooks enabled; every visited node in the current act can be restored; BaseLib not required.");
    }

    private static bool IsAndroidRuntime()
    {
        try { return OS.HasFeature("android") || string.Equals(OS.GetName(), "Android", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
