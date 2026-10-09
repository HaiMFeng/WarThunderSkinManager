using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>清除范围（由用户在确认对话框里勾选）。</summary>
public sealed class ResetOptions
{
    /// <summary>资源库数据：<c>packages/</c> <c>blobs/</c> <c>imports/</c></summary>
    public bool ClearLibrary { get; init; }

    /// <summary>配置数据：<c>mappings/</c> <c>loadouts/</c> <c>previews/</c></summary>
    public bool ClearConfigData { get; init; }

    /// <summary>语言文件与程序配置：<c>lang/</c> <c>config.json</c>（会丢失目录设置与自定义文案）</summary>
    public bool ClearSettings { get; init; }

    /// <summary>游戏输出：<c>&lt;UserSkins&gt;/WTSM</c></summary>
    public bool ClearUserSkinsOutput { get; init; }
}

/// <summary>清除前的现状概览（用于确认对话框展示影响面）。</summary>
public sealed class ResetPlan
{
    public string ResourceDirectory { get; init; } = "";
    public string ConfigDirectory { get; init; } = "";
    public string UserSkinsWtsmPath { get; init; } = "";

    public int PackageCount { get; init; }
    public int VehicleCount { get; init; }
    public int BlobCount { get; init; }
    public long BlobBytes { get; init; }
    public int MappingCount { get; init; }
    public int CountryOverrideCount { get; init; }
    public int LoadoutCount { get; init; }
    public int PreviewCount { get; init; }
    public bool HasWtsmOutput { get; init; }
}

/// <summary>
/// 清除本程序累积的数据（设置页「危险操作」）。
/// 只删除程序自己管理的子目录/文件，绝不触碰 UserSkins 中未受管理的涂装；
/// 每一项都要求用户在界面上明确勾选后才删除。
/// </summary>
public static class DataResetService
{
    /// <summary>统计当前数据规模，供确认对话框展示。</summary>
    public static ResetPlan Plan(AppConfig config)
    {
        var resourceDir = config.ResourceDirectory ?? "";
        var configDir = config.ConfigDirectory ?? "";
        var userSkins = config.UserSkinsDirectory ?? "";

        var packages = resourceDir.Length > 0 && Directory.Exists(resourceDir)
            ? PackageStore.LoadAll(resourceDir)
            : new List<PackageMeta>();

        var blobs = resourceDir.Length > 0
            ? BlobStore.EnumerateBlobs(resourceDir)
            : Array.Empty<string>();

        long bytes = 0;
        foreach (var blob in blobs)
        {
            try { bytes += new FileInfo(blob).Length; }
            catch { /* 忽略单个文件读取失败 */ }
        }

        var mappings = 0;
        var countries = 0;
        if (configDir.Length > 0)
        {
            try { mappings = ConfigService.LoadVehicleMappings(configDir).Count; } catch { }
            try { countries = ConfigService.LoadVehicleCountries(configDir).Count; } catch { }
        }

        var wtsm = userSkins.Length > 0 ? Path.Combine(userSkins, "WTSM") : "";

        return new ResetPlan
        {
            ResourceDirectory = resourceDir,
            ConfigDirectory = configDir,
            UserSkinsWtsmPath = wtsm,
            PackageCount = packages.Count,
            VehicleCount = packages.Select(p => p.VehicleId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            BlobCount = blobs.Length,
            BlobBytes = bytes,
            MappingCount = mappings,
            CountryOverrideCount = countries,
            LoadoutCount = CountFiles(Path.Combine(configDir, "loadouts"), "*.json"),
            PreviewCount = CountFiles(Path.Combine(configDir, "previews"), "*.png"),
            HasWtsmOutput = wtsm.Length > 0 && Directory.Exists(wtsm)
        };
    }

    /// <summary>执行清除；返回失败原因（成功则为空列表）。</summary>
    public static List<string> Execute(AppConfig config, ResetOptions options)
    {
        var errors = new List<string>();

        if (options.ClearLibrary && !string.IsNullOrWhiteSpace(config.ResourceDirectory))
        {
            DeleteDirectory(Path.Combine(config.ResourceDirectory, "packages"), errors);
            DeleteDirectory(Path.Combine(config.ResourceDirectory, "blobs"), errors);
            DeleteDirectory(Path.Combine(config.ResourceDirectory, "imports"), errors);
        }

        var configDir = config.ConfigDirectory;
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            if (options.ClearConfigData)
            {
                DeleteDirectory(Path.Combine(configDir, "mappings"), errors);
                DeleteDirectory(Path.Combine(configDir, "loadouts"), errors);
                DeleteDirectory(Path.Combine(configDir, "previews"), errors);
            }

            if (options.ClearSettings)
            {
                DeleteDirectory(Path.Combine(configDir, "lang"), errors);
                DeleteFile(Path.Combine(configDir, "config.json"), errors);
            }
        }

        if (options.ClearUserSkinsOutput && !string.IsNullOrWhiteSpace(config.UserSkinsDirectory))
            DeleteDirectory(Path.Combine(config.UserSkinsDirectory, "WTSM"), errors);

        return errors;
    }

    /// <summary>把字节数格式化为易读文本。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void DeleteDirectory(string path, List<string> errors)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            errors.Add($"{path}：{ex.Message}");
        }
    }

    private static void DeleteFile(string path, List<string> errors)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add($"{path}：{ex.Message}");
        }
    }
}
