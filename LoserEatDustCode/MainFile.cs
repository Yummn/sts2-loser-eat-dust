using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace LoserEatDust;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "LoserEatDust";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        new Harmony(ModId).PatchAll();
        Logger.Info("[败者食尘] loaded: every visited node in the current act can be restored to its initial state; BaseLib not required.");
    }
}
