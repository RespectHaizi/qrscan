using System.Drawing;
using QrScan.Core;
using QrScan.Core.Models;
using QrScan.Native;
using QrScan.UI;

namespace QrScan;

/// <summary>
/// 编排状态。
///
/// **只有 <see cref="Capturing"/> 是可观察的** —— 因为 <see cref="OverlayWindow.ShowDialog"/>
/// 会泵消息，所以框选期间到达的热键确实能被处理。另两个状态在同步解码期间无法被观察到
/// （UI 线程正忙于解码，<c>WM_HOTKEY</c> 排队到之后才处理），它们作为**防御性分支**保留，
/// 把设计意图留在代码里。这是一处已登记的、有意的规格偏离，不要"修"它。
/// </summary>
internal enum AppState
{
    Idle,
    Capturing,
    Decoding,
    Presenting,
}

/// <summary>
/// 唯一的状态持有者与编排者。它认识所有单元，其他单元互不认识 —— 见规格 §3.3 的依赖方向。
///
/// 全程序只有两处坐标换算，一正一反：
/// 正向的在 <see cref="OverlayWindow"/>（客户区 → 屏幕物理像素），
/// 反向的就是这里的 <see cref="ScreenToLocal"/>（屏幕物理像素 → 冻结位图局部坐标）。
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly IScreenCapture _capture;
    private readonly QrDecoder _decoder;
    private readonly ResultPresenter _presenter;
    private readonly HotkeyManager _hotkey;
    private readonly TrayHost _tray;
    private readonly Func<Bitmap, Rectangle, OverlayWindow> _overlayFactory;
    private readonly Action<QrPayload> _onPayloadDecoded;

    private AppState _state = AppState.Idle;
    private bool _disposed;

    public AppController(
        IScreenCapture capture,
        QrDecoder decoder,
        ResultPresenter presenter,
        HotkeyManager hotkey,
        TrayHost tray,
        Func<Bitmap, Rectangle, OverlayWindow> overlayFactory,
        Action<QrPayload> onPayloadDecoded)
    {
        _capture = capture;
        _decoder = decoder;
        _presenter = presenter;
        _hotkey = hotkey;
        _tray = tray;
        _overlayFactory = overlayFactory;
        _onPayloadDecoded = onPayloadDecoded;

        _hotkey.Pressed += OnHotkey;
        _tray.ScanRequested += OnHotkey;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hotkey.Pressed -= OnHotkey;
        _tray.ScanRequested -= OnHotkey;
    }

    /// <summary>
    /// 屏幕物理像素 → 冻结位图局部坐标。
    ///
    /// **必须用 <paramref name="virtualBounds"/> 的原点，不能写死 0**：虚拟桌面的原点
    /// 在"副屏位于主屏左侧/上方"时是**负数**（规格 §5.2 / §6.5 的第 1 条陷阱）。
    /// 写死 0 的后果是整体截错区域 —— 截图里出现相邻显示器的一部分，而不是框选的内容，
    /// 且只在多显示器布局下暴露。
    ///
    /// 位图原点 == 虚拟桌面左上角，所以这个减法是全程序唯一一处反向换算。
    /// 输入保证落在虚拟桌面内（<see cref="OverlayWindow"/> 的选区由鼠标客户区坐标加原点得来），
    /// 因此结果必然非负。
    /// </summary>
    internal static Rectangle ScreenToLocal(Rectangle physical, Rectangle virtualBounds) => new(
        physical.X - virtualBounds.X,
        physical.Y - virtualBounds.Y,
        physical.Width,
        physical.Height);

    /// <summary>
    /// 状态机入口。两条不可协商的边界规则（规格 §3.4）：
    /// 1. <see cref="AppState.Capturing"/> 期间再按热键 = **取消**（不是叠加第二层遮罩）。
    ///    否则连续按两次热键会得到两个 <c>TopMost</c> 遮罩互相抢 Z 序，屏幕看起来"卡死"。
    /// 2. <see cref="AppState.Decoding"/> / <see cref="AppState.Presenting"/> 期间再按热键 = **忽略**
    ///    （禁止重入原生调用；按第二下热键几乎不可能是"再扫一次"）。
    /// </summary>
    private void OnHotkey()
    {
        switch (_state)
        {
            case AppState.Idle:
                StartCapture();
                break;

            case AppState.Capturing:
                CancelCapture();
                break;

            case AppState.Decoding:
            case AppState.Presenting:
                break;
        }
    }

    private void StartCapture()
    {
        Bitmap? frozen = null;
        OverlayWindow? overlay = null;

        try
        {
            _state = AppState.Capturing;

            Rectangle bounds = _capture.VirtualScreenPhysical();

            // ★ 顺序不可交换：必须在遮罩 Show 之前截屏。
            //   反过来会把遮罩自己截进去 —— 送去解码的是一张半透明黑图，永远扫不出来。
            frozen = CaptureOrThrow();

            overlay = _overlayFactory(frozen, bounds);
            overlay.ShowDialog();

            Rectangle? selection = overlay.SelectedRegionPhysical;
            if (selection is not { } physical)
            {
                _state = AppState.Idle;
                return;
            }

            Rectangle local = ScreenToLocal(physical, bounds);

            _state = AppState.Decoding;

            var payloads = _decoder.Decode(frozen, local);

            if (payloads.Count == 0)
            {
                _presenter.ShowMessage("未识别到二维码，按热键重试", ToastKind.Info, Cursor.Position);
                _state = AppState.Idle;
                return;
            }

            _state = AppState.Presenting;

            // 提示条贴在选区的右下角 ≈ 用户松开鼠标的位置（视线所在），
            // 而不是实时 Cursor.Position —— 后者在解码耗时较长时可能已经移开。
            Point anchor = new(physical.Right, physical.Bottom);
            _presenter.ShowSuccess(payloads, anchor);

            foreach (var payload in payloads)
                _onPayloadDecoded(payload);

            _state = AppState.Idle;
        }
        catch (ScreenCaptureFailedException ex)
        {
            _tray.ShowBalloon("截屏失败", ex.Message);
            _state = AppState.Idle;
        }
        catch (OutOfMemoryException)
        {
            _tray.ShowBalloon("内存不足", "虚拟桌面过大，无法分配截图缓冲。请关闭部分程序后重试。");
            _state = AppState.Idle;
        }
        catch (Exception ex)
        {
            _tray.ShowBalloon("扫码失败", ex.Message);
            _state = AppState.Idle;
        }
        finally
        {
            // ★ 四条退出路径（取消 / 无码 / 成功 / 抛异常）都在这里释放。
            //   双 2K 屏的冻结位图约 29 MB，双 4K 约 66 MB —— 漏一条就是内存泄漏。
            //   不做"成功路径顺手释放一下"：那只是四条里的一条。
            overlay?.Dispose();
            frozen?.Dispose();
        }
    }

    /// <summary>
    /// 截屏，并把"截屏阶段"的异常归一化成 <see cref="ScreenCaptureFailedException"/>。
    ///
    /// 为什么需要这一步：<see cref="ScreenCapture"/> 在虚拟桌面尺寸非法时抛的是
    /// <see cref="InvalidOperationException"/>（不是 <see cref="ScreenCaptureFailedException"/>），
    /// 它会落到泛 <c>catch (Exception)</c> → 用户看到"扫码失败"，**归因错误**
    /// （问题出在截屏，不在识别）。
    ///
    /// try 的范围刻意只包住**一次**截屏调用（<see cref="IScreenCapture.CaptureVirtualScreen"/>）：
    /// 它内部的 <c>BitBlt</c> 与兜底的 <c>CopyFromScreen</c> 都在实现里，这里的 try 看不到；
    /// 因此范围内的 <see cref="InvalidOperationException"/> 只能来自那一层，而非本方法的其他语句。
    /// </summary>
    private Bitmap CaptureOrThrow()
    {
        try
        {
            return _capture.CaptureVirtualScreen();
        }
        catch (InvalidOperationException ex)
        {
            throw new ScreenCaptureFailedException("截屏失败，请重试。", ex);
        }
    }

    /// <summary>
    /// 取消正在进行的框选：关掉最顶层的遮罩，让 <see cref="Form.ShowDialog"/> 返回，
    /// 于是 <c>SelectedRegionPhysical</c> 保持 null → 走"取消"路径。
    ///
    /// 通过 <see cref="Application.OpenForms"/> 定位而不是自己持有引用：
    /// 遮罩由工厂创建、由 ShowDialog 阻塞，这里就活在同一个消息泵里，
    /// 因此它必然在 OpenForms 中；这样 AppController 与遮罩之间不需要互相引用。
    /// </summary>
    private void CancelCapture()
    {
        foreach (Form form in Application.OpenForms)
        {
            if (form is OverlayWindow active)
            {
                active.Close();
                return;
            }
        }

        _state = AppState.Idle;
    }
}
