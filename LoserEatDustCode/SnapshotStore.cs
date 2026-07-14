using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Saves;
using System.Reflection;

namespace LoserEatDust;

internal sealed record NodeCheckpoint(
    string Key,
    string Path,
    SerializableRun Save,
    DateTime CapturedAtUtc)
{
    public int VisitNumber => Save.VisitedMapCoords?.Count ?? 0;

    public MapCoord? Coord => Save.VisitedMapCoords is { Count: > 0 } coords ? coords[^1] : null;

    public string DisplayName
    {
        get
        {
            if (Coord is null)
                return "阶段起点";

            var floor = VisitNumber;
            var pointType = Save.Acts.ElementAtOrDefault(Save.CurrentActIndex)?
                .SavedMap?.Points?
                .FirstOrDefault(point => point.Coord == Coord.Value)?
                .PointType.ToString();
            var room = pointType switch
            {
                "Monster" or "Combat" or "Normal" => "普通战斗",
                "Elite" => "精英战斗",
                "Boss" => "首领战斗",
                "Event" or "Unknown" => "事件",
                "Merchant" or "Shop" => "商店",
                "RestSite" or "Rest" => "休息处",
                "Treasure" or "Chest" => "宝箱",
                "Ancient" => "先古之民",
                _ => "地图节点"
            };
            return $"第{floor}层 · {room}";
        }
    }
}

internal static class SnapshotStore
{
    private static readonly object Sync = new();
    private static readonly FieldInfo SaveStoreField =
        AccessTools.Field(typeof(SaveManager), "_saveStore");

    // Use the game's own save backend. On Android this resolves to the correct
    // writable profile storage instead of relying on System.IO + GlobalizePath.
    private static ISaveStore Store =>
        (ISaveStore)(SaveStoreField.GetValue(SaveManager.Instance) ??
                     throw new InvalidOperationException("SaveManager save store is not initialized."));

    private static string RootDirectory =>
        SaveManager.Instance.GetProfileScopedPath("loser_eat_dust");

    private static string MarkerPath => JoinPath(RootDirectory, "current_act.txt");

    public static void CaptureInitialState(SerializableRun save)
    {
        if (save.StartTime == 0)
            return;

        lock (Sync)
        {
            EnsureRunAndAct(save);
            var key = GetNodeKey(save);
            var path = JoinPath(RootDirectory, $"node_{key}.save");

            // The first save made at a coordinate is its room-entry state. Never
            // overwrite it with combat-end, event-end, or save-and-quit data.
            if (Store.FileExists(path))
                return;

            Store.WriteFile(path, SaveManager.ToJson(save));
            MainFile.Logger.Info($"[败者食尘] recorded {key}: act={save.CurrentActIndex + 1}, visit={save.VisitedMapCoords?.Count ?? 0}.");
        }
    }

    public static void ReplaceCurrentRun(SerializableRun save)
    {
        lock (Sync)
        {
            var managerField = AccessTools.Field(typeof(SaveManager), "_runSaveManager");
            var runSaveManager = managerField.GetValue(SaveManager.Instance)
                ?? throw new InvalidOperationException("RunSaveManager is unavailable.");
            var pathProperty = AccessTools.Property(runSaveManager.GetType(), "CurrentRunSavePath")
                ?? throw new MissingMemberException("RunSaveManager.CurrentRunSavePath");
            var path = (string?)pathProperty.GetValue(runSaveManager)
                ?? throw new InvalidOperationException("Current run save path is unavailable.");

            Store.WriteFile(path, SaveManager.ToJson(save));
        }
    }

    public static IReadOnlyList<NodeCheckpoint> GetCheckpoints(SerializableRun current)
    {
        lock (Sync)
        {
            if (!MarkerMatches(current) || !Store.DirectoryExists(RootDirectory))
                return Array.Empty<NodeCheckpoint>();

            var result = new List<NodeCheckpoint>();
            foreach (var entry in Store.GetFilesInDirectory(RootDirectory))
            {
                try
                {
                    var path = ResolveStorePath(entry);
                    var fileName = GetFileName(path);
                    if (!fileName.StartsWith("node_", StringComparison.Ordinal) ||
                        !fileName.EndsWith(".save", StringComparison.Ordinal))
                        continue;

                    var json = Store.ReadFile(path);
                    if (string.IsNullOrEmpty(json))
                        continue;

                    var parsed = SaveManager.FromJson<SerializableRun>(json);
                    if (!parsed.Success || parsed.SaveData is null)
                        continue;

                    var save = parsed.SaveData;
                    if (save.StartTime != current.StartTime || save.CurrentActIndex != current.CurrentActIndex)
                        continue;

                    result.Add(new NodeCheckpoint(
                        GetNodeKey(save),
                        path,
                        save,
                        Store.GetLastModifiedTime(path).UtcDateTime));
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[败者食尘] ignored unreadable node snapshot: {ex.Message}");
                }
            }

            return result
                .OrderBy(checkpoint => checkpoint.VisitNumber)
                .ThenBy(checkpoint => checkpoint.Coord?.row ?? -1)
                .ThenBy(checkpoint => checkpoint.CapturedAtUtc)
                .ToArray();
        }
    }

    public static string GetNodeKey(SerializableRun save)
    {
        if (save.VisitedMapCoords is not { Count: > 0 } coords)
            return "start";

        var coord = coords[^1];
        return $"r{coord.row}_c{coord.col}";
    }

    private static void EnsureRunAndAct(SerializableRun save)
    {
        Store.CreateDirectory(RootDirectory);
        if (!MarkerMatches(save))
        {
            foreach (var entry in Store.GetFilesInDirectory(RootDirectory))
                Store.DeleteFile(ResolveStorePath(entry));
        }

        Store.WriteFile(MarkerPath, Marker(save));
    }

    private static bool MarkerMatches(SerializableRun save)
    {
        try
        {
            return Store.FileExists(MarkerPath) && Store.ReadFile(MarkerPath) == Marker(save);
        }
        catch
        {
            return false;
        }
    }

    private static string Marker(SerializableRun save) => $"{save.StartTime}|{save.CurrentActIndex}";

    private static string JoinPath(string directory, string fileName) =>
        $"{directory.TrimEnd('/', '\\')}/{fileName}";

    private static string ResolveStorePath(string entry)
    {
        var normalizedEntry = entry.Replace('\\', '/');
        var normalizedRoot = RootDirectory.Replace('\\', '/').TrimEnd('/');
        return normalizedEntry.StartsWith(normalizedRoot + "/", StringComparison.Ordinal)
            ? normalizedEntry
            : JoinPath(normalizedRoot, GetFileName(normalizedEntry));
    }

    private static string GetFileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }
}
