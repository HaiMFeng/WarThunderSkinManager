using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>配置目录 JSON 读写。默认 LocalAppData/WarThunderSkinManager。</summary>
public static class ConfigService
{
    public static string DefaultConfigDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "WarThunderSkinManager");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ---- AppConfig：config.json ----
    /// <summary>
    /// 读取配置；文件损坏 / 被占用时**兜底为默认值**（与全项目「读失败不崩」模式一致，§4）。
    /// </summary>
    public static AppConfig Load(string configDir)
    {
        var path = Path.Combine(configDir, "config.json");
        if (File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                if (cfg != null) return cfg;
            }
            catch
            {
                // 损坏 → 用默认值继续；下次保存会覆盖
            }
        }
        return new AppConfig { ConfigDirectory = configDir };
    }

    public static void Save(string configDir, AppConfig cfg)
    {
        Directory.CreateDirectory(configDir);
        AtomicFile.WriteAllText(Path.Combine(configDir, "config.json"),
            JsonSerializer.Serialize(cfg, JsonOpts));
    }

    // ---- 载具标识映射：mappings/vehicles.json { vehicleId: displayName } ----
    public static string MappingFile(string configDir) => Path.Combine(configDir, "mappings", "vehicles.json");

    public static Dictionary<string, string> LoadVehicleMappings(string configDir)
    {
        var path = MappingFile(configDir);
        if (File.Exists(path))
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (d != null) return d;
            }
            catch
            {
                // 损坏 → 视为无映射（显示名回落译名表 / 标识）
            }
        }
        return new Dictionary<string, string>();
    }

    public static void SaveVehicleMappings(string configDir, Dictionary<string, string> map)
    {
        var dir = Path.Combine(configDir, "mappings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "vehicles.json"),
            JsonSerializer.Serialize(map, JsonOpts));
    }

    // ---- 载具所属国家覆盖：mappings/vehicle_countries.json { vehicleId: countryId } ----
    public static string VehicleCountriesFile(string configDir)
        => Path.Combine(configDir, "mappings", "vehicle_countries.json");

    public static Dictionary<string, string> LoadVehicleCountries(string configDir)
    {
        var path = VehicleCountriesFile(configDir);
        if (File.Exists(path))
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (d != null) return d;
            }
            catch
            {
                // 损坏 → 视为无归类（前缀推断兜底）
            }
        }
        return new Dictionary<string, string>();
    }

    public static void SaveVehicleCountries(string configDir, Dictionary<string, string> map)
    {
        var dir = Path.Combine(configDir, "mappings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(VehicleCountriesFile(configDir),
            JsonSerializer.Serialize(map, JsonOpts));
    }

    // ---- 载具激活设置：loadouts/<vehicleId>.json ----
    public static string ActivationFile(string configDir, string vehicleId)
        => Path.Combine(configDir, "loadouts", $"{vehicleId}.json");

    public static VehicleActivation? LoadActivation(string configDir, string vehicleId)
    {
        var path = ActivationFile(configDir, vehicleId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<VehicleActivation>(File.ReadAllText(path));
        }
        catch
        {
            // 损坏 → 视为未激活（该载具无输出），避免切载具即崩
            return null;
        }
    }

    public static void SaveActivation(string configDir, string vehicleId, VehicleActivation activation)
    {
        var dir = Path.Combine(configDir, "loadouts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(ActivationFile(configDir, vehicleId),
            JsonSerializer.Serialize(activation, JsonOpts));
    }

    // ---- 国家列表：countries.json [ { id, name } ] ----
    public static string CountriesFile(string configDir) => Path.Combine(configDir, "countries.json");

    public static List<Country> LoadCountries(string configDir)
    {
        var path = CountriesFile(configDir);
        if (File.Exists(path))
        {
            var list = JsonSerializer.Deserialize<List<Country>>(File.ReadAllText(path));
            if (list != null) return list;
        }
        return new List<Country>();
    }

    public static void SaveCountries(string configDir, List<Country> countries)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(CountriesFile(configDir),
            JsonSerializer.Serialize(countries, JsonOpts));
    }
}
