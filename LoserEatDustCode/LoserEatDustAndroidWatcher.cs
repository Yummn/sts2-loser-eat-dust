using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Saves;

namespace LoserEatDust;

/// <summary>
/// Android replacement for the three Harmony detours used on desktop. Polling
/// the authoritative run save and visible pause menu is cheap, deterministic,
/// and avoids MonoMod's intermittent ARM64 startup crash.
/// </summary>
internal sealed partial class LoserEatDustAndroidWatcher : Node
{
    private const double SavePollInterval = 0.75;
    private const double UiPollInterval = 0.25;
    private static LoserEatDustAndroidWatcher? _instance;
    private double _saveElapsed;
    private double _uiElapsed;
    private string? _capturedIdentity;
    private NPauseMenu? _visiblePauseMenu;

    internal static void Install()
    {
        if (GodotObject.IsInstanceValid(_instance)) return;
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        _instance = new LoserEatDustAndroidWatcher { Name = "LoserEatDustAndroidWatcher" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
    }

    public override void _Process(double delta)
    {
        _saveElapsed += delta;
        _uiElapsed += delta;

        if (_saveElapsed >= SavePollInterval)
        {
            _saveElapsed = 0;
            PollRunSave();
        }

        if (_uiElapsed >= UiPollInterval)
        {
            _uiElapsed = 0;
            PollPauseMenu();
        }
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
        }
        catch
        {
            // SaveManager is not ready during the first startup frames. Leave
            // the identity unset so the next poll retries automatically.
        }
    }

    private void PollPauseMenu()
    {
        var found = FindVisiblePauseMenu(GetTree()?.Root);
        if (!GodotObject.IsInstanceValid(found))
        {
            _visiblePauseMenu = null;
            return;
        }

        var newlyOpened = !ReferenceEquals(found, _visiblePauseMenu);
        _visiblePauseMenu = found;
        try
        {
            PauseMenuPatch.EnsureInstalled(found!);
            if (newlyOpened)
            {
                PauseMenuPatch.RefreshButtons(
                    found!.GetNode<Control>("%ButtonContainer"),
                    selectCurrentNode: true);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[败者食尘] Android pause watcher retrying: {ex.Message}");
        }
    }

    private static NPauseMenu? FindVisiblePauseMenu(Node? node)
    {
        if (node is NPauseMenu pause && pause.IsVisibleInTree()) return pause;
        if (node is null) return null;
        foreach (var child in node.GetChildren())
        {
            var found = FindVisiblePauseMenu(child);
            if (found is not null) return found;
        }
        return null;
    }
}
