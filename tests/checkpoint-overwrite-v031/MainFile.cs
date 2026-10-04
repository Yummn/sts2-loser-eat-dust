using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace CodexLoserEatDustOverwriteTest;

[ModInitializer(nameof(Initialize))]
public static partial class MainFile
{
    private static readonly MegaCrit.Sts2.Core.Logging.Logger Log = new("CodexLoserEatDustOverwriteTest", LogType.Generic);
    public static void Initialize()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.Root.CallDeferred(Node.MethodName.AddChild, new Runner());
    }

    private sealed partial class Runner : Node
    {
        public override void _Ready() => _ = RunAsync();
        private async Task RunAsync()
        {
            try
            {
                for (var i = 0; i < 120; i++)
                {
                    var read = SaveManager.Instance.LoadRunSave();
                    if (read.Success && read.SaveData is not null)
                    {
                        Run(read.SaveData);
                        return;
                    }
                    await ToSignal(GetTree().CreateTimer(.25), SceneTreeTimer.SignalName.Timeout);
                }
                throw new InvalidOperationException("run save unavailable");
            }
            catch (Exception ex) { Log.Error($"[CodexLoserEatDustOverwriteTest] FAIL: {ex}"); }
        }

        private static void Run(SerializableRun current)
        {
            if (current.VisitedMapCoords.Count == 0)
                throw new InvalidOperationException("test requires at least one visited map node");

            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "LoserEatDust");
            var store = assembly.GetType("LoserEatDust.SnapshotStore", true)!;
            var checkpointType = assembly.GetType("LoserEatDust.NodeCheckpoint", true)!;
            var clone = Clone(current);
            clone.VisitedMapCoords.RemoveAt(clone.VisitedMapCoords.Count - 1);
            var checkpoint = Activator.CreateInstance(
                checkpointType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                ["test-cutoff", "test", clone, DateTime.UtcNow, null],
                null) ?? throw new InvalidOperationException("checkpoint construction failed");

            AccessTools.Method(store, "BeginRewoundTimeline")!.Invoke(null, [checkpoint]);
            var key = (string)AccessTools.Method(store, "GetNodeKey")!.Invoke(null, [current])!;

            current.Players[0].Gold = 111111;
            AccessTools.Method(store, "CaptureInitialState")!.Invoke(null, [current]);
            current.Players[0].Gold = 222222;
            AccessTools.Method(store, "CaptureInitialState")!.Invoke(null, [current]);

            var checkpoints = (IEnumerable)AccessTools.Method(store, "GetCheckpoints")!.Invoke(null, [current])!;
            var saved = checkpoints.Cast<object>().Single(c =>
                string.Equals((string)AccessTools.Property(c.GetType(), "Key")!.GetValue(c)!, key, StringComparison.Ordinal));
            var savedRun = (SerializableRun)AccessTools.Property(saved.GetType(), "Save")!.GetValue(saved)!;
            var actual = savedRun.Players[0].Gold;
            if (actual != 111111)
                throw new InvalidOperationException($"expected first revisited state 111111, got {actual}");

            Log.Info("[CodexLoserEatDustOverwriteTest] PASS: first save after rewind replaced the old checkpoint; the second same-node save did not overwrite the new room-entry state.");
        }

        private static SerializableRun Clone(SerializableRun save)
        {
            var parsed = SaveManager.FromJson<SerializableRun>(SaveManager.ToJson(save));
            return parsed.Success && parsed.SaveData is not null
                ? parsed.SaveData
                : throw new InvalidOperationException("save clone failed");
        }
    }
}
