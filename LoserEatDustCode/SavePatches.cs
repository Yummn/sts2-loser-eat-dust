using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using System.Reflection;

namespace LoserEatDust;

[HarmonyPatch]
internal static class RecordNodeEntryPatch
{
    internal static bool SuppressCapture { get; set; }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        // v0.107+ writes SerializableRun directly; the Android v0.103 branch
        // only exposes SaveRun(AbstractRoom). Select whichever exists instead
        // of letting PatchAll abort on an undefined overload.
        var direct = AccessTools.Method(
            typeof(RunSaveManager),
            nameof(RunSaveManager.SaveRun),
            [typeof(SerializableRun), typeof(bool)]);
        if (direct is not null)
            yield return direct;

        var roomBased = AccessTools.Method(
            typeof(RunSaveManager),
            nameof(RunSaveManager.SaveRun),
            [typeof(AbstractRoom)]);
        if (roomBased is not null)
            yield return roomBased;
    }

    [HarmonyPrefix]
    private static void Prefix(object[] __args)
    {
        if (SuppressCapture)
            return;

        var save = __args.OfType<SerializableRun>().FirstOrDefault();
        var isMultiplayer = __args.OfType<bool>().FirstOrDefault();
        if (save is null || isMultiplayer)
            return;

        try
        {
            SnapshotStore.CaptureInitialState(save);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[败者食尘] node snapshot failed: {ex}");
        }
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task __result, object[] __args)
    {
        // v0.103 has no SerializableRun argument. Capture the completed save
        // produced from AbstractRoom instead.
        if (SuppressCapture || __args.OfType<SerializableRun>().Any())
            return;

        __result = RecordCompletedSavePatch.CaptureAfterSave(__result);
    }
}

// A second capture point at the public save facade protects mobile builds whose
// save call path differs from desktop. Existing node snapshots are immutable.
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun), typeof(AbstractRoom), typeof(bool))]
internal static class RecordCompletedSavePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result)
    {
        if (RecordNodeEntryPatch.SuppressCapture)
            return;

        __result = CaptureAfterSave(__result);
    }

    internal static async Task CaptureAfterSave(Task original)
    {
        await original;

        try
        {
            var read = SaveManager.Instance.LoadRunSave();
            if (read.Success && read.SaveData is not null)
                SnapshotStore.CaptureInitialState(read.SaveData);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[败者食尘] completed-save snapshot fallback failed: {ex}");
        }
    }
}
