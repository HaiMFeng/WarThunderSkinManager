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

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ---- AppConfig：config.json ----
    public static AppConfig Load(string configDir)
    {
        var path = Path.Combine(configDir, "config.json");
        if (File.Exists(path))
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
            if (cfg != null) return cfg;
        }
        return new AppConfig { ConfigDirectory = configDir };
    }

    public static void Save(string configDir, AppConfig cfg)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"),
            JsonSerializer.Serialize(cfg, JsonOpts));
    }

    // ---- 载具标识映射：mappings/vehicles.json { vehicleId: displayName } ----
    public static string MappingFile(string configDir) => Path.Combine(configDir, "mappings", "vehicles.json");

    public static Dictionary<string, string> LoadVehicleMappings(string configDir)
    {
        var path = MappingFile(configDir);
        if (File.Exists(path))
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (d != null) return d;
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

    // ---- 激活输出：loadouts/<vehicleId>.json ----
    public static string LoadoutFile(string configDir, string vehicleId)
        => Path.Combine(configDir, "loadouts", $"{vehicleId}.json");

    public static ActiveLoadout? LoadLoadout(string configDir, string vehicleId)
    {
        var path = LoadoutFile(configDir, vehicleId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ActiveLoadout>(File.ReadAllText(path))
            : null;
    }

    public static void SaveLoadout(string configDir, string vehicleId, ActiveLoadout loadout)
    {
        var dir = Path.Combine(configDir, "loadouts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(LoadoutFile(configDir, vehicleId),
            JsonSerializer.Serialize(loadout, JsonOpts));
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
