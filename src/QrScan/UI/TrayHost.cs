using QrScan.Core;
using QrScan.Core.Models;

namespace QrScan.UI;

/// <summary>
/// 托盘图标与右键菜单。
/// 这是本程序唯一常驻的 UI 实体 —— 常驻期不存在任何 Form。
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _scanItem;
    private readonly ToolStripMenuItem _recentItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly IStartupManager _startup;
    private readonly string _hotkeyDisplay;

    private bool _disposed;

    public event Action? ScanRequested;
    public event Action? QuitRequested;

    public TrayHost(IStartupManager startup, string hotkeyDisplay)
    {
        _startup = startup;
        _hotkeyDisplay = hotkeyDisplay;

        _scanItem = new ToolStripMenuItem($"扫码 ({hotkeyDisplay})");
        _scanItem.Click += (_, _) => ScanRequested?.Invoke();

        _recentItem = new ToolStripMenuItem("最近 10 条") { Enabled = false };

        _startupItem = new ToolStripMenuItem("开机自启");
        _startupItem.Click += (_, _) => ToggleStartup();

        var aboutItem = new ToolStripMenuItem("关于");
        aboutItem.Click += (_, _) => ShowAbout();

        var quitItem = new ToolStripMenuItem("退出");
        quitItem.Click += (_, _) => QuitRequested?.Invoke();

        _menu = new ContextMenuStrip();
        // 实测（.NET 8）：ToolStrip.ShowItemToolTips 的默认值**已经是 true**，
        // 所以下面这行不改变任何行为；它只是把"项级 ToolTipText 必须可见"这件事写明确，
        // 免得后人再问一次。"热键为何不可用"的可读原因正是靠 ToolTipText 承载的。
        _menu.ShowItemToolTips = true;
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _scanItem,
            _recentItem,
            new ToolStripSeparator(),
            _startupItem,
            aboutItem,
            new ToolStripSeparator(),
            quitItem,
        });

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,     // 占位图标；可替换为自定义 .ico
            Text = "QrScan",                    // NotifyIcon.Text 上限 63 字符
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => ScanRequested?.Invoke();

        RefreshStartupCheck();
    }

    /// <summary>
    /// 热键不可用时调用：把菜单项置灰，并把可读原因放进 ToolTip。
    ///
    /// 菜单文字里**不宣示任何具体原因** —— 调用点无法区分"被其它程序占用 / 系统保留组合
    /// （如 Ctrl+Alt+Delete）/ 配置写坏"三种情形，写死其中一种就会对另外两种说谎。
    /// 真原因走 <c>ToolStripItem.ToolTipText</c>，它依赖菜单已开启
    /// <c>ToolStrip.ShowItemToolTips</c>。
    /// </summary>
    public void SetHotkeyUnavailable(string reason)
    {
        if (_disposed) return;

        _scanItem.Text = "扫码 (热键不可用)";
        _scanItem.Enabled = false;
        _scanItem.ToolTipText = reason;
    }

    /// <summary>仅内存保留的最近记录；点选 = 复制到剪贴板（不自动打开，避免误触协议处理器）。</summary>
    public void SetRecent(IReadOnlyList<QrPayload> items)
    {
        if (_disposed) return;

        _recentItem.DropDownItems.Clear();

        if (items.Count == 0)
        {
            _recentItem.Text = "最近 10 条";
            _recentItem.Enabled = false;
            return;
        }

        _recentItem.Text = $"最近 {items.Count} 条";
        _recentItem.Enabled = true;

        foreach (var payload in items)
            _recentItem.DropDownItems.Add(BuildRecentEntry(payload));
    }

    /// <summary>供"复制"与"重试"复用的剪贴板写入，带重试。</summary>
    public event Action<string>? RecentItemChosen;

    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // ★ 不做这两步会留下"幽灵图标"：进程已退出，图标还赖在托盘里，
        //   要等鼠标划过才消失，用户点它毫无反应。
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private ToolStripMenuItem BuildRecentEntry(QrPayload payload)
    {
        string label = payload.Text.Length <= 60
            ? payload.Text.Replace("\r", " ").Replace("\n", " ")
            : payload.Text[..60].Replace("\r", " ").Replace("\n", " ") + "…";

        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => RecentItemChosen?.Invoke(payload.Text);
        return item;
    }

    private void ToggleStartup()
    {
        try
        {
            _startup.SetEnabled(!_startup.IsEnabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or System.Security.SecurityException
                                      or IOException)
        {
            ShowBalloon("无法修改开机自启", ex.Message);
        }

        RefreshStartupCheck();
    }

    private void RefreshStartupCheck() => _startupItem.Checked = _startup.IsEnabled;

    private void ShowAbout() => ShowBalloon(
        "QrScan",
        $"托盘二维码扫码器\n热键：{_hotkeyDisplay}\n单文件框架依赖版");
}
