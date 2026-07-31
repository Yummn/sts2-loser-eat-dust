using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace LoserEatDust;

/// <summary>
/// A detour-free death interceptor. Registering an AbstractModel as a combat
/// hook listener makes the feature work on both desktop and Android without
/// patching CreatureCmd, NRun, or SaveManager.
/// </summary>
public sealed class LoserEatDustDeathHook : AbstractModel
{
    public override bool ShouldReceiveCombatHooks => true;

    public override bool ShouldDieLate(Creature creature) =>
        DeathInterceptController.ShouldAllowDeath(creature);

    public override Task AfterPreventingDeath(Creature creature) =>
        DeathInterceptController.HandlePreventedDeathAsync(creature);
}

internal static class DeathInterceptController
{
    private static bool _installed;
    private static Creature? _allowNextDeathFor;
    private static Creature? _handlingCreature;

    internal static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        ModHelper.SubscribeForCombatStateHooks(
            $"{MainFile.ModId}.DeathIntercept",
            _ =>
            [
                ModelDb.GetById<LoserEatDustDeathHook>(
                    ModelDb.GetId<LoserEatDustDeathHook>())
            ]);
        MainFile.Logger.Info("[败者食尘] detour-free death interception registered.");
    }

    internal static bool ShouldAllowDeath(Creature creature)
    {
        if (ReferenceEquals(_allowNextDeathFor, creature))
        {
            _allowNextDeathFor = null;
            return true;
        }

        if (!creature.IsPlayer ||
            RunManager.Instance.NetService.Type != NetGameType.Singleplayer ||
            ReferenceEquals(_handlingCreature, creature))
        {
            return true;
        }

        _handlingCreature = creature;
        return false;
    }

    internal static async Task HandlePreventedDeathAsync(Creature creature)
    {
        if (!ReferenceEquals(_handlingCreature, creature))
            return;

        NodeCheckpoint? checkpoint;
        try
        {
            checkpoint = SnapshotStore.GetCurrentCheckpoint();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[败者食尘] failed to prepare death checkpoint: {ex}");
            checkpoint = null;
        }

        if (checkpoint is null)
        {
            MainFile.Logger.Warn("[败者食尘] no current-node checkpoint was available; continuing normal death flow.");
            AllowNormalDeath(creature);
            return;
        }

        RunManager.Instance.ActionExecutor.Pause();
        var retry = await ShowRetryDialogAsync();
        if (!retry)
        {
            RunManager.Instance.ActionExecutor.Unpause();
            AllowNormalDeath(creature);
            return;
        }

        // KillWithoutCheckingWinCondition checks IsDead after this hook returns.
        // Temporarily revive to one HP so it exits cleanly; the room-entry
        // snapshot replaces the whole combat on the following process frame.
        creature.SetCurrentHpInternal(1m);
        _handlingCreature = null;
        MainFile.Logger.Info($"[败者食尘] death intercepted; restoring {checkpoint.Key}.");
        _ = TaskHelper.RunSafely(RestoreOnNextFrameAsync(checkpoint));
    }

    private static void AllowNormalDeath(Creature creature)
    {
        _handlingCreature = null;
        _allowNextDeathFor = creature;
    }

    private static async Task<bool> ShowRetryDialogAsync()
    {
        Node? parent = NRun.Instance;
        parent ??= (Engine.GetMainLoop() as SceneTree)?.Root;
        if (parent is null)
            return true;

        var completion = new TaskCompletionSource<bool>();
        var dialog = new AcceptDialog
        {
            Name = "LoserEatDustDeathIntercept",
            Title = "败者食尘：死亡拦截",
            DialogText = "你被击败了，但本局尚未结束。\n可以从当前节点初始状态重新开始。",
            OkButtonText = "从本节点重来",
            Exclusive = true,
            ProcessMode = Node.ProcessModeEnum.Always,
            MinSize = new Vector2I(620, 300)
        };
        var giveUp = dialog.AddCancelButton("接受死亡并结束本局");

        void Finish(bool retry)
        {
            if (!completion.TrySetResult(retry))
                return;
            dialog.QueueFree();
        }

        dialog.Confirmed += () => Finish(true);
        dialog.Canceled += () => Finish(false);
        parent.AddChild(dialog);

        var retryButton = dialog.GetOkButton();
        if (retryButton is not null)
        {
            retryButton.CustomMinimumSize = new Vector2(260f, 72f);
            retryButton.SelfModulate = new Color(0.62f, 0.9f, 0.56f);
        }
        giveUp.CustomMinimumSize = new Vector2(260f, 72f);
        giveUp.SelfModulate = new Color(0.95f, 0.58f, 0.52f);

        dialog.PopupCentered();
        MainFile.Logger.Info("[败者食尘] death dialog opened; run save and node snapshots remain intact.");
        return await completion.Task;
    }

    private static async Task RestoreOnNextFrameAsync(NodeCheckpoint checkpoint)
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        RunRestarter.Restore(checkpoint);
    }
}
