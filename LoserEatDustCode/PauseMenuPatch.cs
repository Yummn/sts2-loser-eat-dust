using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace LoserEatDust;

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu._Ready))]
internal static class PauseMenuPatch
{
    internal const string SelectorName = "LoserEatDustSelector";
    internal const string RestoreName = "LoserEatDustRestore";

    private static List<NodeCheckpoint> _checkpoints = [];
    private static string? _selectedKey;

    [HarmonyPostfix]
    private static void Postfix(NPauseMenu __instance)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Singleplayer)
            return;

        try
        {
            var container = __instance.GetNode<Control>("%ButtonContainer");
            if (container.GetNodeOrNull<NPauseMenuButton>(SelectorName) is not null)
                return;

            var template = container.GetNode<NPauseMenuButton>("Settings");
            var giveUp = container.GetNode<NPauseMenuButton>("GiveUp");
            var insertionIndex = giveUp.GetIndex();

            var selector = CreateButton(template, SelectorName, "败者食尘：选择节点");
            container.AddChild(selector);
            container.MoveChild(selector, insertionIndex++);
            selector.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ =>
                {
                    CycleToPreviousNode();
                    RefreshButtons(container);
                }));

            var restore = CreateButton(template, RestoreName, "从所选节点重新开始");
            container.AddChild(restore);
            container.MoveChild(restore, insertionIndex);
            restore.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ =>
                {
                    var selected = GetSelected();
                    if (selected is null)
                        return;

                    restore.Disable();
                    RunRestarter.Restore(selected);
                }));

            RefreshButtons(container, selectCurrentNode: true);
            RebuildFocusNeighbors(container);
            MainFile.Logger.Info("[败者食尘] pause-menu node selector added.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[败者食尘] failed to create pause-menu controls: {ex}");
        }
    }

    internal static void RefreshButtons(Control container, bool selectCurrentNode = false)
    {
        var selector = container.GetNodeOrNull<NPauseMenuButton>(SelectorName);
        var restore = container.GetNodeOrNull<NPauseMenuButton>(RestoreName);
        if (selector is null || restore is null)
            return;

        var currentRead = SaveManager.Instance.LoadRunSave();
        if (!currentRead.Success || currentRead.SaveData is null)
        {
            selector.Disable();
            restore.Disable();
            return;
        }

        var current = currentRead.SaveData;
        // Seed the current room-entry save before listing checkpoints. This
        // also covers Android sessions where the mod was enabled mid-run or a
        // platform-specific save path bypassed the normal capture hook.
        try
        {
            SnapshotStore.CaptureInitialState(current);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[败者食尘] pause-menu snapshot fallback failed: {ex}");
        }

        _checkpoints = SnapshotStore.GetCheckpoints(current).ToList();
        if (_checkpoints.Count == 0)
        {
            selector.GetNode<MegaLabel>("Label").SetTextAutoSize("败者食尘：暂无节点记录");
            selector.Disable();
            restore.Disable();
            return;
        }

        if (selectCurrentNode)
        {
            var currentKey = SnapshotStore.GetNodeKey(current);
            if (_checkpoints.Any(checkpoint => checkpoint.Key == currentKey))
                _selectedKey = currentKey;
        }

        if (_selectedKey is null || _checkpoints.All(checkpoint => checkpoint.Key != _selectedKey))
            _selectedKey = _checkpoints[^1].Key;

        var selectedIndex = _checkpoints.FindIndex(checkpoint => checkpoint.Key == _selectedKey);
        var selected = _checkpoints[selectedIndex];
        selector.GetNode<MegaLabel>("Label")
            .SetTextAutoSize($"节点 {selectedIndex + 1}/{_checkpoints.Count}：{selected.DisplayName}");
        restore.GetNode<MegaLabel>("Label").SetTextAutoSize("败者食尘：从这里重来");

        selector.Enable();
        if (RunRestarter.CanRestore) restore.Enable();
        else restore.Disable();
    }

    private static void CycleToPreviousNode()
    {
        if (_checkpoints.Count == 0)
            return;

        var index = _checkpoints.FindIndex(checkpoint => checkpoint.Key == _selectedKey);
        index = index <= 0 ? _checkpoints.Count - 1 : index - 1;
        _selectedKey = _checkpoints[index].Key;
    }

    private static NodeCheckpoint? GetSelected() =>
        _checkpoints.FirstOrDefault(checkpoint => checkpoint.Key == _selectedKey);

    private static NPauseMenuButton CreateButton(NPauseMenuButton template, string name, string label)
    {
        var button = (NPauseMenuButton)template.Duplicate();
        button.Name = name;

        var image = button.GetNode<TextureRect>("ButtonImage");
        if (image.Material is ShaderMaterial material)
        {
            image.Material = (ShaderMaterial)material.Duplicate();
            AccessTools.Field(typeof(NPauseMenuButton), "_hsv")?.SetValue(button, image.Material);
        }

        button.GetNode<MegaLabel>("Label").SetTextAutoSize(label);
        button.Enable();
        return button;
    }

    private static void RebuildFocusNeighbors(Control container)
    {
        var buttons = container.GetChildren()
            .OfType<NPauseMenuButton>()
            .Where(button => button is { Visible: true, IsEnabled: true })
            .ToList();

        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            var self = button.GetPath();
            button.FocusNeighborLeft = self;
            button.FocusNeighborRight = self;
            button.FocusNeighborTop = i > 0 ? buttons[i - 1].GetPath() : self;
            button.FocusNeighborBottom = i + 1 < buttons.Count ? buttons[i + 1].GetPath() : self;
        }
    }
}

[HarmonyPatch(typeof(NPauseMenu), "Initialize")]
internal static class PauseMenuOpenRefreshPatch
{
    [HarmonyPostfix]
    private static void Postfix(NPauseMenu __instance)
    {
        try
        {
            PauseMenuPatch.RefreshButtons(
                __instance.GetNode<Control>("%ButtonContainer"),
                selectCurrentNode: true);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[败者食尘] pause-menu refresh failed: {ex.Message}");
        }
    }
}
