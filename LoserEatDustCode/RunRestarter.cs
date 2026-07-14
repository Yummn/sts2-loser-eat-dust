using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace LoserEatDust;

internal static class RunRestarter
{
    private static bool _loading;

    public static bool CanRestore =>
        !_loading &&
        RunManager.Instance.NetService.Type == NetGameType.Singleplayer &&
        SaveManager.Instance.HasRunSave;

    public static void Restore(NodeCheckpoint checkpoint) =>
        TaskHelper.RunSafely(RestoreAsync(checkpoint));

    private static async Task RestoreAsync(NodeCheckpoint checkpoint)
    {
        if (!CanRestore || NGame.Instance is null)
            return;

        _loading = true;
        try
        {
            var serializableRun = checkpoint.Save;

            RecordNodeEntryPatch.SuppressCapture = true;
            try
            {
                SnapshotStore.ReplaceCurrentRun(serializableRun);
            }
            finally
            {
                RecordNodeEntryPatch.SuppressCapture = false;
            }

            var runState = RunState.FromSerializable(serializableRun);
            RunManager.Instance.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            NAudioManager.Instance?.StopMusic();

            await NGame.Instance.Transition.FadeOut();
            RunManager.Instance.CleanUp(graceful: true);
            var setupMethod = AccessTools.Method(
                                  typeof(RunManager),
                                  "SetUpSavedSingleplayer",
                                  [typeof(RunState), typeof(SerializableRun)])
                              ?? AccessTools.Method(
                                  typeof(RunManager),
                                  "SetUpSavedSinglePlayer",
                                  [typeof(RunState), typeof(SerializableRun)])
                              ?? throw new MissingMethodException("RunManager.SetUpSavedSinglePlayer");
            var setupResult = setupMethod.Invoke(RunManager.Instance, [runState, serializableRun]);
            if (setupResult is Task setupTask)
                await setupTask;
            NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await NGame.Instance.LoadRun(runState, serializableRun.PreFinishedRoom);
            MainFile.Logger.Info($"[败者食尘] restored {checkpoint.Key}: {checkpoint.DisplayName}.");
        }
        catch (Exception ex)
        {
            RecordNodeEntryPatch.SuppressCapture = false;
            MainFile.Logger.Error($"[败者食尘] restore failed: {ex}");
        }
        finally
        {
            try
            {
                await NGame.Instance.Transition.FadeIn();
            }
            catch
            {
                // The room load may replace the transition node.
            }

            _loading = false;
        }
    }
}
