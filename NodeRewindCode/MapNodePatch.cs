using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace NodeRewind;

/// <summary>
/// Replaces the old pause-menu selector with direct map interaction. The
/// vanilla map disables traveled points, so RefreshVisualsInstantly re-enables
/// only points that have a room-entry checkpoint; OnRelease then consumes the
/// click before the normal map travel action can run.
/// </summary>
[HarmonyPatch(typeof(NMapPoint), "OnRelease")]
internal static class MapPointClickPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMapPoint __instance)
    {
        try
        {
            return !NodeRewindMap.TryHandleClick(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[节点回溯] map-node click failed: {ex.Message}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(NMapPoint), nameof(NMapPoint.RefreshVisualsInstantly))]
internal static class MapPointVisualPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapPoint __instance)
    {
        try { NodeRewindMap.RefreshPoint(__instance); }
        catch (Exception ex) { MainFile.Logger.Warn($"[节点回溯] map marker refresh failed: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class MapScreenOpenPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        NodeRewindMap.InvalidateAndRefreshDeferred();
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.RefreshAllPointVisuals))]
internal static class MapScreenRefreshPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        NodeRewindMap.InvalidateAndRefreshDeferred();
    }
}

internal static class NodeRewindMap
{
    private static readonly object Sync = new();
    private static Dictionary<MapCoord, NodeCheckpoint> _checkpoints = [];
    private static HashSet<MapCoord> _currentPath = [];
    private static MapCoord? _currentCoord;
    private static bool _cacheReady;
    private static bool _refreshQueued;

    public static bool TryHandleClick(NMapPoint point)
    {
        if (!IsSinglePlayer() || point.Point is null)
            return false;

        // A checkpoint on the next reachable node must not steal ordinary
        // forward travel. Otherwise a revisited branch discards the new
        // timeline's rewards and can never replace its old entry snapshot.
        if (CanNormallyTravel(point))
            return false;

        EnsureCache();
        if (!_checkpoints.TryGetValue(point.Point.coord, out var checkpoint))
            return false;

        if (!RunRestarter.CanRestore)
            return true;

        MainFile.Logger.Info($"[节点回溯] map node selected: {checkpoint.Key} ({checkpoint.DisplayName}).");
        RunRestarter.Restore(checkpoint);
        return true;
    }

    public static void RefreshPoint(NMapPoint point)
    {
        var marker = point.GetNodeOrNull<NodeRewindMarker>(NodeRewindMarker.NodeName);
        if (!IsSinglePlayer() || point.Point is null)
        {
            HideMarker(marker);
            return;
        }

        EnsureCache();
        if (!_checkpoints.TryGetValue(point.Point.coord, out var checkpoint))
        {
            // NMapPoint instances can be reused when the run advances to a
            // different act. A point without a checkpoint must actively hide
            // the marker created for the previous map; returning here leaves
            // the old act's ring visible and, on Android, still intercepting
            // touch input.
            HideMarker(marker);
            return;
        }

        // The original map marks all traveled nodes disabled. Historical
        // checkpoints are deliberately clickable even when the run is not in
        // a travel phase; the click patch consumes them before vanilla travel.
        // Android deliberately skips these Harmony patches. Its marker is an
        // input overlay, so leave the vanilla point disabled: forwarding a
        // Released signal would also execute ordinary map travel.
        if (!IsAndroid())
            point.Enable();
        marker ??= GetOrCreateMarker(point);
        marker.MouseFilter = IsAndroid() && !CanNormallyTravel(point)
            ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        var state = point.Point.coord == _currentCoord
            ? NodeRewindMarkerState.Current
            : _currentPath.Contains(point.Point.coord)
                ? NodeRewindMarkerState.CurrentRoute
                : NodeRewindMarkerState.RewoundBranch;
        marker.SetState(state, checkpoint.VisitNumber);
    }

    public static void InvalidateAndRefreshDeferred()
    {
        lock (Sync)
        {
            _cacheReady = false;
            _checkpoints = [];
            _currentPath = [];
            _currentCoord = null;
        }

        if (_refreshQueued)
            return;

        _refreshQueued = true;
        Callable.From(() =>
        {
            _refreshQueued = false;
            RefreshAllMarkers();
        }).CallDeferred();
    }

    public static void RefreshAllMarkers(bool force = true)
    {
        var screen = NMapScreen.Instance;
        if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree() || !screen.IsVisibleInTree())
            return;

        EnsureCache(force);
        var points = screen.GetNodeOrNull<Control>("TheMap/Points");
        if (points is null)
            return;

        foreach (var point in points.GetChildren().OfType<NMapPoint>())
            RefreshPoint(point);
    }

    private static void HideMarker(NodeRewindMarker? marker)
    {
        if (marker is null || !GodotObject.IsInstanceValid(marker))
            return;

        marker.HideForMapRefresh();
    }

    private static void EnsureCache(bool force = false)
    {
        if (_cacheReady && !force)
            return;

        lock (Sync)
        {
            if (_cacheReady && !force)
                return;

            try
            {
                var read = SaveManager.Instance.LoadRunSave();
                if (!read.Success || read.SaveData is null)
                {
                    _checkpoints = [];
                    _currentPath = [];
                    _currentCoord = null;
                    _cacheReady = true;
                    return;
                }

                var save = read.SaveData;
                SnapshotStore.CaptureInitialState(save);
                var checkpoints = SnapshotStore.GetCheckpoints(save);
                _checkpoints = checkpoints
                    .Where(checkpoint => checkpoint.Coord.HasValue)
                    .GroupBy(checkpoint => checkpoint.Coord!.Value)
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.VisitNumber).First());
                _currentPath = save.VisitedMapCoords?.ToHashSet() ?? [];
                _currentCoord = save.VisitedMapCoords is { Count: > 0 } coords ? coords[^1] : null;
            }
            catch (Exception ex)
            {
                _checkpoints = [];
                _currentPath = [];
                _currentCoord = null;
                MainFile.Logger.Warn($"[节点回溯] map checkpoint cache failed: {ex.Message}");
            }

            _cacheReady = true;
        }
    }

    private static NodeRewindMarker GetOrCreateMarker(NMapPoint point)
    {
        if (point.GetNodeOrNull<NodeRewindMarker>(NodeRewindMarker.NodeName) is { } existing)
            return existing;

        var marker = new NodeRewindMarker { Name = NodeRewindMarker.NodeName };
        point.AddChild(marker);
        return marker;
    }

    private static bool IsSinglePlayer()
    {
        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Singleplayer;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsAndroid() => OS.HasFeature("android");

    private static bool CanNormallyTravel(NMapPoint point) =>
        GodotObject.IsInstanceValid(NMapScreen.Instance) &&
        NMapScreen.Instance.IsTravelEnabled && point.Get("IsTravelable").AsBool();
}

internal enum NodeRewindMarkerState
{
    CurrentRoute,
    Current,
    RewoundBranch
}

internal partial class NodeRewindMarker : Control
{
    internal const string NodeName = "NodeRewindMarker";
    private NodeRewindMarkerState _state;
    private int _visitNumber;
    private bool _pressed;
    private Vector2 _pressPosition;

    public override void _Ready()
    {
        MouseFilter = NodeRewindMap.IsAndroid() ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        ZIndex = 25;
        SetAnchorsPreset(LayoutPreset.Center);
        OffsetLeft = -72f;
        OffsetTop = -72f;
        OffsetRight = 72f;
        OffsetBottom = 72f;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent input)
    {
        if (!NodeRewindMap.IsAndroid()) return;
        Vector2 position;
        bool pressed;
        if (input is InputEventScreenTouch touch)
        {
            position = touch.Position;
            pressed = touch.Pressed;
        }
        else if (input is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
        {
            position = mouse.Position;
            pressed = mouse.Pressed;
        }
        else if (input is InputEventScreenDrag drag)
        {
            if (drag.Position.DistanceTo(_pressPosition) > 18f) _pressed = false;
            return;
        }
        else if (input is InputEventMouseMotion motion)
        {
            if (motion.Position.DistanceTo(_pressPosition) > 18f) _pressed = false;
            return;
        }
        else return;

        AcceptEvent();
        if (pressed)
        {
            _pressPosition = position;
            _pressed = true;
            return;
        }
        var shouldRestore = _pressed && position.DistanceTo(_pressPosition) <= 18f;
        _pressed = false;
        if (shouldRestore && GetParent() is NMapPoint point)
            NodeRewindMap.TryHandleClick(point);
    }

    internal void SetState(NodeRewindMarkerState state, int visitNumber)
    {
        _state = state;
        _visitNumber = visitNumber;
        Visible = true;
        QueueRedraw();
    }

    internal void HideForMapRefresh()
    {
        _pressed = false;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var center = new Vector2(72f, 72f);
        var color = _state switch
        {
            NodeRewindMarkerState.Current => new Color(1f, 0.88f, 0.35f, 0.98f),
            NodeRewindMarkerState.CurrentRoute => new Color(0.96f, 0.63f, 0.20f, 0.92f),
            _ => new Color(0.48f, 0.67f, 0.94f, 0.88f)
        };

        DrawArc(center, 54f, 0f, Mathf.Tau, 48, new Color(0.12f, 0.045f, 0.015f, 0.82f), 8f, true);
        DrawArc(center, 54f, 0f, Mathf.Tau, 48, color, 4f, true);
        DrawArc(center, 61f, -0.18f, 1.6f, 18, new Color(color, 0.65f), 2f, true);
        DrawArc(center, 61f, 2.95f, 4.68f, 18, new Color(color, 0.65f), 2f, true);

        if (_state == NodeRewindMarkerState.Current)
            DrawCircle(center, 5f, new Color(1f, 0.95f, 0.68f, 0.95f));
    }
}
