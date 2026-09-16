using Microsoft.Win32;

namespace QrScan.Core;

/// <summary>
/// 通过 HKCU 的 Run 键管理开机自启。有注册表副作用 → 实例 + 接口（见规格 §4.1 的规则）。
/// 键路径可注入：这是本设计中唯一为可测性刻意引入的构造参数。
/// </summary>
public sealed class StartupManager : IStartupManager
{
    internal const string DefaultSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string DefaultValueName = "QrScan";

    private readonly RegistryKey _root;
    private readonly string _subKeyPath;
    private readonly string _valueName;

    public StartupManager() : this(Registry.CurrentUser, DefaultSubKey, DefaultValueName) { }

    public StartupManager(RegistryKey root, string subKeyPath, string valueName)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        _root = root;
        _subKeyPath = subKeyPath;
        _valueName = valueName;
    }

    public bool IsEnabled => RegisteredPath is not null;

    public string? RegisteredPath
    {
        get
        {
            try
            {
                using var key = _root.OpenSubKey(_subKeyPath);

                // 空 / 全空白值等同于"未启用"：Run 键下可能存在一个空的 REG_SZ 值
                // （别的程序写入后未清理，或写入被中断）。若把它当成已启用，托盘菜单
                // 会显示一个勾，而开机并不会启动 —— 一个静默的假象。
                return key?.GetValue(_valueName) is string raw && !string.IsNullOrWhiteSpace(raw)
                    ? NormalizeRegisteredPath(raw)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or System.Security.SecurityException)
            {
                return null;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = _root.CreateSubKey(_subKeyPath, writable: true)
            ?? throw new InvalidOperationException($"无法打开注册表键 {_subKeyPath}。");

        if (enabled)
            key.SetValue(_valueName, Quote(CurrentExecutablePath()), RegistryValueKind.String);
        else
            key.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 当前进程的可执行文件路径。
    /// **必须用 <see cref="Environment.ProcessPath"/>** —— 单文件发布下
    /// <c>Assembly.Location</c> 是空字符串。
    /// </summary>
    public static string CurrentExecutablePath() =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定当前可执行文件路径。");

    /// <summary>路径必须加引号：含空格时不加会让 Windows 把 "C:\Program" 当成程序名，开机静默失败。</summary>
    public static string Quote(string path) => $"\"{path.Trim().Trim('"')}\"";

    /// <summary>把注册表里的原始值规范成纯路径，容忍有无引号以及尾随参数。</summary>
    public static string NormalizeRegisteredPath(string raw)
    {
        string trimmed = raw.Trim();

        if (trimmed.StartsWith('"'))
        {
            int closing = trimmed.IndexOf('"', 1);
            return closing > 0 ? trimmed[1..closing] : trimmed.Trim('"');
        }

        // 无引号：取到第一个空格为止（"C:\QrScan\QrScan.exe" 这类无空格路径）
        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
