using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 涂装包落盘读写：<c>&lt;资源目录&gt;/packages/&lt;Id&gt;/</c>，
/// 内含 <c>source.blk</c>（导入原始 blk，程序不重写）与 <c>meta.json</c>（见功能设计 §6.5）。
/// </summary>
public static class PackageStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string PackagesDirectory(string resourceDir) => Path.Combine(resourceDir, "packages");

    public static string PackageDirectory(string resourceDir, string id)
        => Path.Combine(PackagesDirectory(resourceDir), id);

    public static string SourceBlkPath(string resourceDir, string id)
        => Path.Combine(PackageDirectory(resourceDir, id), "source.blk");

    public static string MetaPath(string resourceDir, string id)
        => Path.Combine(PackageDirectory(resourceDir, id), "meta.json");

    /// <summary>写入包的 source.blk 与 meta.json（已存在则覆盖）。</summary>
    public static void Save(string resourceDir, PackageMeta meta, string sourceBlkPath)
    {
        if (string.IsNullOrWhiteSpace(meta.Id))
            throw new ArgumentException("包 Id 不能为空", nameof(meta));

        var dir = PackageDirectory(resourceDir, meta.Id);
        Directory.CreateDirectory(dir);

        File.Copy(sourceBlkPath, SourceBlkPath(resourceDir, meta.Id), overwrite: true);
        File.WriteAllText(MetaPath(resourceDir, meta.Id),
            JsonSerializer.Serialize(meta, JsonOpts), new UTF8Encoding(false));
    }

    public static PackageMeta? Load(string resourceDir, string id)
    {
        var path = MetaPath(resourceDir, id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PackageMeta>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>扫描 packages/*/meta.json，加载全部包元数据。</summary>
    public static List<PackageMeta> LoadAll(string resourceDir)
    {
        var list = new List<PackageMeta>();
        var root = PackagesDirectory(resourceDir);
        if (!Directory.Exists(root)) return list;

        // 排序保证顺序稳定（Directory.GetDirectories 顺序不保证）
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var meta = Load(resourceDir, Path.GetFileName(dir));
            if (meta != null) list.Add(meta);
        }
        return list;
    }
}
