using System.Text.Json;
using QrScan.Core.Models;

namespace QrScan.Core;

/// <summary>config.json 读写。有文件系统副作用 → 实例 + 接口（见规格 §4.1 的规则）。</summary>
public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // 写出 camelCase，并在读入时容忍大小写差异
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string FilePath { get; }

    public ConfigStore() : this(DefaultPath()) { }

    public ConfigStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QrScan", "config.json");

    public ConfigLoadResult Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new ConfigLoadResult(new AppConfig(), ConfigLoadStatus.CreatedDefault);

            string text = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(text, SerializerOptions);

            return config is null
                ? new ConfigLoadResult(new AppConfig(), ConfigLoadStatus.RecoveredFromCorrupt)
                : new ConfigLoadResult(config, ConfigLoadStatus.Loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or ArgumentException
                                      or NotSupportedException)
        {
            return new ConfigLoadResult(new AppConfig(), ConfigLoadStatus.RecoveredFromCorrupt);
        }
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
