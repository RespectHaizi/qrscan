using QrScan.Core.Models;

namespace QrScan.Core;

public enum ConfigLoadStatus
{
    /// <summary>成功读到并解析。</summary>
    Loaded,

    /// <summary>文件不存在，已用默认值。</summary>
    CreatedDefault,

    /// <summary>文件存在但无法解析，已回退默认值。调用方应提示用户。</summary>
    RecoveredFromCorrupt,
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadStatus Status);

public interface IConfigStore
{
    string FilePath { get; }

    /// <summary>
    /// 读取配置。**永不抛异常** —— 配置坏掉不该让常驻程序起不来。
    ///
    /// <para>
    /// 返回 <see cref="ConfigLoadResult"/> 而非裸的 <see cref="AppConfig"/>，是为了让调用方
    /// 能区分「文件不存在（首次运行，静默用默认值即可）」与「文件损坏（应当提示用户）」——
    /// 这两种情况在托盘上的表现不同，不能混为一谈。
    /// </para>
    /// </summary>
    ConfigLoadResult Load();

    void Save(AppConfig config);
}
