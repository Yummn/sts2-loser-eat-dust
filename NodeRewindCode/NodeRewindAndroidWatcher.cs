using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Saves;

namespace NodeRewind;

/// <summary>
/// Android replacement for the save Harmony detour. Polling the authoritative
/// run save avoids MonoMod's intermittent ARM64 startup crash. On Android the
/// map markers receive input directly; no Harmony map detour is installed.
/// </summary>
internal sealed partial class NodeRewindAndroidWatcher : Node
{
    private const double SavePollInterval = 0.75;
    private static NodeRewindAndroidWatcher? _instance;
    private double _saveElapsed;
    private string? _capturedIdentity;
    private double _mapElapsed;
    private bool _mapWasVisible;

    internal static void Install()
    {
        if (GodotObject.IsInstanceValid(_instance)) return;
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        _instance = new NodeRewindAndroidWatcher { Name = "NodeRewindAndroidWatcher" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
    }

    public override void _Process(double delta)
    {
        _saveElapsed += delta;
        _mapElapsed += delta;

        if (_saveElapsed >= SavePollInterval)
        {
            _saveElapsed = 0;
            PollRunSave();
        }

        var screen = NMapScreen.Instance;
        var mapVisible = GodotObject.IsInstanceValid(screen) && screen.IsInsideTree() && screen.IsVisibleInTree();
        if (mapVisible && (!_mapWasVisible || _mapElapsed >= 0.25))
        {
            _mapElapsed = 0;
            NodeRewindMap.RefreshAllMarkers(force: !_mapWasVisible);
        }
        _mapWasVisible = mapVisible;
    }

    private void PollRunSave()
    {
        if (RecordNodeEntryPatch.SuppressCapture) return;
        try
        {
            var read = SaveManager.Instance.LoadRunSave();
            if (!read.Success || read.SaveData is null) return;
            var save = read.SaveData;
            var identity = $"{save.StartTime}|{save.CurrentActIndex}|{SnapshotStore.GetNodeKey(save)}";
            if (identity == _capturedIdentity) return;
            SnapshotStore.CaptureInitialState(save);
            _capturedIdentity = identity;
            NodeRewindMap.InvalidateAndRefreshDeferred();
        }
        catch
        {
            // SaveManager is not ready during the first startup frames. Leave
            // the identity unset so the next poll retries automatically.
        }
    }

}
