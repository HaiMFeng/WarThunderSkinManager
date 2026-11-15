using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **附属载具（子载具）** 映射（功能设计 §3.19）：<c>&lt;配置目录&gt;/subvehicles.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 场景：游戏里有 <c>us_sl_amraam_fcs</c> 与 <c>us_sl_amraam_launcher</c> 两个 blk，
/// 但玩家只能在 fcs 那栏选涂装，游戏会自动到**同一个文件夹**里找 launcher 的 blk。
/// 因此本程序把子载具的激活输出**重定向**到父载具的目录（文件名仍是子载具自己）。
/// </para>
/// <para>
/// 约束（已与用户确认）：
/// 一个子载具**只能属于一个父**；父载具**不能再当别人的子**（禁止链式）；不能自引用。
/// </para>
/// <para>
/// 内置组合（<see cref="BuiltInSubVehicles"/>）在载入相关包时自动建立；
/// 用户手动解绑后记入 <c>dismissed</c>，**不再自动重建**（否则会跟用户对着干）。
/// </para>
/// </remarks>
public static class SubVehicleService
{
    public const string FileName = "subvehicles.json";

    private static readonly object Gate = new();

    private static readonly Dictionary<string, List<string>> Parents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ChildToParent = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Dismissed = new(StringComparer.OrdinalIgnoreCase);

    private static string _configDir = "";
    private static bool _loaded;

    public static void Configure(string? configDir)
    {
        lock (Gate)
        {
            var dir = configDir ?? "";
            if (string.Equals(dir, _configDir, StringComparison.OrdinalIgnoreCase) && _loaded) return;

            _configDir = dir;
            _loaded = false;
            Parents.Clear();
            ChildToParent.Clear();
            Dismissed.Clear();
        }
    }

    public static string FilePath(string configDir)
        => string.IsNullOrWhiteSpace(configDir) ? "" : Path.Combine(configDir, FileName);

    /// <summary>子载具 → 父载具（没有则 null）。</summary>
    public static string? ParentOf(string? vehicleId)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(vehicleId)) return null;

        lock (Gate) return ChildToParent.TryGetValue(vehicleId, out var parent) ? parent : null;
    }

    /// <summary>父载具 → 子载具列表（副本）。</summary>
    public static IReadOnlyList<string> ChildrenOf(string? vehicleId)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(vehicleId)) return Array.Empty<string>();

        lock (Gate)
            return Parents.TryGetValue(vehicleId, out var children) ? children.ToList() : Array.Empty<string>();
    }

    public static bool IsParent(string? vehicleId)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(vehicleId)) return false;

        lock (Gate) return Parents.TryGetValue(vehicleId, out var children) && children.Count > 0;
    }

    public static bool IsChild(string? vehicleId) => ParentOf(vehicleId) != null;

    /// <summary>
    /// 某个载具的激活输出应该落在谁的目录里：自己是子载具 → 父的目录；否则自己的目录。
    /// </summary>
    public static string OutputParentOf(string? vehicleId)
        => ParentOf(vehicleId) ?? vehicleId ?? "";

    /// <summary>
    /// 与 <paramref name="vehicleId"/> 共用一个输出目录的全部载具（父 + 全部子；自己单独时只有自己）。
    /// 顺序：父在前，其后按登记顺序。
    /// </summary>
    public static IReadOnlyList<string> GroupOf(string? vehicleId)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return Array.Empty<string>();

        var parent = OutputParentOf(vehicleId);
        var others = ChildrenOf(parent);

        if (others.Count == 0) return new[] { parent };

        var group = new List<string> { parent };
        group.AddRange(others.Where(child => !string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)));
        return group;
    }

    /// <summary>
    /// 建立父子关系。失败时返回错误码（界面据此取文案）：
    /// <c>self</c> / <c>missing</c> / <c>parentIsChild</c> / <c>childIsParent</c> / <c>hasParent</c>。
    /// </summary>
    public static (bool Ok, string? Error) Bind(string? parentId, string? childId, bool auto = false)
    {
        EnsureLoaded();

        var parent = (parentId ?? "").Trim();
        var child = (childId ?? "").Trim();

        if (parent.Length == 0 || child.Length == 0) return (false, "missing");
        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)) return (false, "self");

        lock (Gate)
        {
            // 禁止链式：父载具本身不能是别人的子；子载具也不能已经是别人的父
            if (ChildToParent.ContainsKey(parent)) return (false, "parentIsChild");
            if (Parents.TryGetValue(child, out var grandChildren) && grandChildren.Count > 0)
                return (false, "childIsParent");

            if (ChildToParent.TryGetValue(child, out var existing))
            {
                if (string.Equals(existing, parent, StringComparison.OrdinalIgnoreCase)) return (true, null);
                return (false, "hasParent");
            }

            if (!Parents.TryGetValue(parent, out var list))
            {
                list = new List<string>();
                Parents[parent] = list;
            }

            if (!list.Contains(child, StringComparer.OrdinalIgnoreCase)) list.Add(child);

            ChildToParent[child] = parent;
            Dismissed.Remove(child); // 重新绑上就取消"别再自动加"的记名

            SaveLocked();
            return (true, null);
        }
    }

    /// <summary>
    /// 解绑某个子载具：从父的子列表移除。内置组合解绑后记入 <c>dismissed</c>，
    /// 之后载入相关包**不会**再自动加上（尊重用户的选择）。
    /// </summary>
    public static bool Unbind(string? childId)
    {
        EnsureLoaded();
        var child = (childId ?? "").Trim();
        if (child.Length == 0) return false;

        lock (Gate)
        {
            if (!ChildToParent.TryGetValue(child, out var parent)) return false;

            ChildToParent.Remove(child);

            if (Parents.TryGetValue(parent, out var siblings))
            {
                siblings.RemoveAll(id => string.Equals(id, child, StringComparison.OrdinalIgnoreCase));
                if (siblings.Count == 0) Parents.Remove(parent);
            }

            if (BuiltInSubVehicles.IsBuiltInPair(parent, child)) Dismissed.Add(child);

            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// 载入某载具的包时尝试建立**内置**父子关系（父子都存在于载具表才绑）。
    /// 已手动解绑过（<c>dismissed</c>）、子已有别的父、或会造成链式 → 不动。
    /// </summary>
    public static bool TryAutoBind(string? vehicleId, Func<string, bool> exists)
    {
        EnsureLoaded();

        var pair = BuiltInSubVehicles.FindPair(vehicleId);
        if (pair == null) return false;

        var (parent, child) = pair.Value;

        lock (Gate)
        {
            if (Dismissed.Contains(child)) return false;

            // 父子两个载具都必须存在于载具表
            if (!exists(parent) || !exists(child)) return false;

            if (ChildToParent.ContainsKey(child)) return false;   // 已有父（含手动绑定）
            if (ChildToParent.ContainsKey(parent)) return false;  // 会形成链式
            if (Parents.TryGetValue(child, out var grandChildren) && grandChildren.Count > 0) return false;
        }

        return Bind(parent, child, auto: true).Ok;
    }

    /// <summary>把某载具的**手动**绑定关系清掉（仅供自检 / 数据重置用）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Parents.Clear();
            ChildToParent.Clear();
            Dismissed.Clear();
            _loaded = false;
        }
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            var path = FilePath(_configDir);
            if (path.Length == 0 || !File.Exists(path)) return;

            try
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                if (document == null) return;

                foreach (var pair in document.Parents)
                {
                    var parent = pair.Key.Trim();
                    if (parent.Length == 0) continue;

                    var children = new List<string>();

                    foreach (var raw in pair.Value)
                    {
                        var child = (raw ?? "").Trim();
                        if (child.Length == 0) continue;
                        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ChildToParent.ContainsKey(child)) continue; // 一条子只能有一个父，先到先得
                        if (Parents.ContainsKey(child)) continue;       // 文件里出现链式 → 忽略该条

                        ChildToParent[child] = parent;
                        children.Add(child);
                    }

                    if (children.Count > 0) Parents[parent] = children;
                }

                foreach (var raw in document.Dismissed)
                    if (!string.IsNullOrWhiteSpace(raw)) Dismissed.Add(raw.Trim());
            }
            catch
            {
                // 文件损坏 → 当空映射（**不触发任何清理**：安全第一）
                Parents.Clear();
                ChildToParent.Clear();
                Dismissed.Clear();
            }
        }
    }

    private static void SaveLocked()
    {
        var path = FilePath(_configDir);
        if (path.Length == 0) return;

        var document = new Document
        {
            Version = 1,
            Parents = Parents.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase),
            Dismissed = Dismissed.ToList()
        };

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // 写失败不影响本次操作（内存里的映射仍然有效），下次改动会再试
        }
    }

    private sealed class Document
    {
        [JsonPropertyName("version")] public int Version { get; set; }

        /// <summary>父载具 → 子载具列表。</summary>
        [JsonPropertyName("parents")] public Dictionary<string, List<string>> Parents { get; set; } = new();

        /// <summary>用户手动解绑过的内置子载具（不再自动重建）。</summary>
        [JsonPropertyName("dismissed")] public List<string> Dismissed { get; set; } = new();
    }
}
