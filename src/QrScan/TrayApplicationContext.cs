using System.Drawing;
using QrScan.Core;
using QrScan.Core.Models;
using QrScan.Native;
using QrScan.UI;

namespace QrScan;

/// <summary>
/// 应用的消息循环宿主。刻意不使用任何 Form —— 常驻期零窗体。
/// 它只负责**组装**依赖并把生命周期收干净；编排逻辑全在 <see cref="AppController"/>。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TrayHost _tray;
    private readonly HotkeyManager _hotkey;
    private readonly QrDecoder _decoder;
    private readonly AppController _controller;

    private readonly List<QrPayload> _recent = new();
    private readonly int _recentLimit;

    public TrayApplicationContext()
    {
        var configStore = new ConfigStore();
        var startup = new StartupManager();
        var launcher = new ShellLauncher();

        var loaded = configStore.Load();
        _recentLimit = loaded.Config.RecentLimit;

        _tray = new TrayHost(startup, loaded.Config.Hotkey);

        // 自启路径自动修复：用户把 exe 从下载文件夹挪到桌面后，注册表里的路径会永久
        // 指向旧位置，而托盘菜单的勾还稳稳地打着 —— 一个非常隐蔽的失效。
        //
        // 放在创建 TrayHost **之后**：失败时必须能提示用户，而气泡要有托盘图标才发得出来。
        // 修复成功仍然不提示（那属于用户无从处理的内部一致性修正，弹提示只会制造噪音）；
        // 失败**必须可见** —— 否则托盘勾会继续显示"已启用"、开机却不会启动，
        // 只是把一个隐蔽失效換成了另一个隐蔽法。
        if (!StartupPathRepair.RepairUsingCurrentExecutable(startup))
            _tray.ShowBalloon("无法修复开机自启",
                "启动项指向的路径与当前位置不一致，但无法重写注册表。请手动取消并重新勾选「开机自启」。");

        // 托盘"最近 N 条"点选 = 复制到剪贴板。走 ResultPresenter 的带重试实现
        // （裸 Clipboard.SetDataObject 没有重试，失败会走到 WinForms
        // 默认的未处理异常对话框上）。
        // 失败必须可见 —— "绝不静默失败"是全局约束：静默失败会让用户以为复制成功了，
        // 然后粘贴出上一次的旧内容。
        _tray.RecentItemChosen += text =>
        {
            if (!ResultPresenter.TrySetClipboardText(text))
                _tray.ShowBalloon("复制失败", "剪贴板被其他程序占用，请稍后重试。");
        };

        // QuitRequested / ScanRequested / Pressed 都是零参数 Action?
        _tray.QuitRequested += () => ExitThread();

        if (loaded.Status == ConfigLoadStatus.RecoveredFromCorrupt)
            _tray.ShowBalloon("配置文件已损坏", "已重置为默认设置。");

        // 提示条构造在这里（而不是紧跟着 _decoder）：下面"首次落盘失败"也要走自绘通道，
        // 而不只是托盘气泡 —— 理由与热键失败那段相同（Win11 下气泡正文可能根本没被渲染）。
        // 构造函数只存两个字段，没有副作用，提前到这里是安全的。
        var presenter = new ResultPresenter(launcher, loaded.Config.ToastDismissMs);

        // 首次运行把默认配置落盘。**必须在创建 HotkeyManager 之前** ——
        // 热键失败消息里给出的就是 config.json 的完整路径，而本程序没有设置界面，
        // 那是唯一入口；文件不存在时那句提示无法执行。
        if (loaded.Status == ConfigLoadStatus.CreatedDefault)
        {
            try
            {
                configStore.Save(loaded.Config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 落盘失败不影响本次运行（默认值已经在内存里生效），但**必须可见** ——
                // "绝不静默失败"是全局约束。而且在"落盘失败 且 热键注册也失败"的双故障下，
                // 下面那句"请修改配置文件中的 hotkey"会指向一个并不存在的文件：
                // 用户需要知道它没写成功、以及写的是哪个路径。
                string message = $"无法写入 {configStore.FilePath}：{ex.Message}";

                _tray.ShowBalloon("配置保存失败", message);
                presenter.ShowMessage(message, ToastKind.Failure, StartupToastAnchor());
            }
        }

        _decoder = new QrDecoder();

        // 启动自检：把"原生库缺失"从"扫第一个码时静默无反应"变成"启动即告知"
        if (loaded.Config.SelfTestOnStartup && !_decoder.SelfTest())
            _tray.ShowBalloon("识别引擎加载失败", "ZXing.dll 缺失或损坏，扫码功能不可用。");

        _hotkey = new HotkeyManager(loaded.Config.Hotkey);

        if (!_hotkey.IsRegistered)
        {
            string reason = _hotkey.RegistrationError ?? "无法注册全局热键。";

            _tray.SetHotkeyUnavailable(reason);
            _tray.ShowBalloon("热键不可用", reason);

            // ★ 除托盘气泡之外**再**用自绘提示条显示一次。
            //   实测发现：Win11 下两条**内容不同**的气泡截图逐像素完全一致，
            //   强证据表明气泡正文可能根本没被渲染 —— 那样用户只会看到一个标题为 "QrScan"
            //   的空通知，而"热键注册失败必须对用户可见"正是本功能存在的全部意义。
            //   提示条是自绘文字，绕开整个系统通知栈（专注助手、通知开关、渲染差异）。
            presenter.ShowMessage(reason, ToastKind.Failure, StartupToastAnchor());
        }

        _controller = new AppController(
            capture: new ScreenCapture(),
            decoder: _decoder,
            presenter: presenter,
            hotkey: _hotkey,
            tray: _tray,
            overlayFactory: (frozen, bounds) => new OverlayWindow(frozen, bounds),
            onPayloadDecoded: AddRecent);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.Dispose();
            _hotkey.Dispose();
            _decoder.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 启动期提示条的锚点：虚拟桌面右下角内侧。
    ///
    /// 刻意**不用指针位置** —— 启动时指针在哪儿与本次提示毫无关系，
    /// 而且提示条落在指针底下会立刻触发"悬停暂停淡出"。
    /// <see cref="ToastWindow"/> 自己会把越界的位置 clamp 回可见区域。
    /// </summary>
    private static Point StartupToastAnchor()
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        return new Point(vs.Right - 32, vs.Bottom - 32);
    }

    private void AddRecent(QrPayload payload)
    {
        _recent.Insert(0, payload);
        while (_recent.Count > _recentLimit)
            _recent.RemoveAt(_recent.Count - 1);

        _tray.SetRecent(_recent);
    }
}
