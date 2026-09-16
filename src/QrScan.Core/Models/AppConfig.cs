namespace QrScan.Core.Models;

/// <summary>v1 的全部配置字段，不多不少。修改 config.json 需重启生效。</summary>
public sealed record AppConfig
{
    /// <summary>全局热键，形如 "Ctrl+Shift+Q"。必须含至少一个修饰键。</summary>
    public string Hotkey { get; init; } = "Ctrl+Shift+Q";

    /// <summary>提示条自动淡出的毫秒数；鼠标悬停时暂停计时。</summary>
    public int ToastDismissMs { get; init; } = 2000;

    /// <summary>托盘菜单中保留的最近条数（仅内存，不落盘）。</summary>
    public int RecentLimit { get; init; } = 10;

    /// <summary>启动时是否做原生引擎自检（排查时可关）。</summary>
    public bool SelfTestOnStartup { get; init; } = true;
}
