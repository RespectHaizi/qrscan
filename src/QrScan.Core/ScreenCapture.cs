using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QrScan.Core;

/// <summary>
/// 抓取整个虚拟桌面的冻结位图。位图原点 == 虚拟桌面左上角。
/// **必须在进程已设为 PerMonitorV2 DPI 感知后调用**，否则坐标会被系统虚拟化，
/// 在 150% 缩放的显示器上产生系统性偏移。
/// </summary>
public sealed class ScreenCapture : IScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int SRCCOPY = 0x00CC0020;

    /// <summary>
    /// 让 BitBlt 把 <c>WS_EX_LAYERED</c> 窗口的内容也一并抓取。
    /// <c>Graphics.CopyFromScreen</c> 不带这个标志，会漏掉聊天工具浮层、
    /// 部分输入法候选框等分层窗口 —— 现象是它们"凭空消失"，极难归因。
    /// </summary>
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    public Rectangle VirtualScreenPhysical() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public Bitmap CaptureVirtualScreen() => CaptureRegion(VirtualScreenPhysical());

    /// <summary>
    /// 内部接缝：让测试能用**非 (0,0) 原点**的矩形驱动真实 <c>BitBlt</c>。
    ///
    /// <para>
    /// 为什么不直接测 <see cref="CaptureVirtualScreen"/>：本机是单显示器，
    /// <see cref="VirtualScreenPhysical"/> 返回的原点就是 <c>(0,0)</c> —— 而单屏下
    /// 把 <c>TryBitBlt</c> 的源坐标写成字面量 <c>0</c> 也不会有任何区别，于是那条路径
    /// 零自动化覆盖。暴露本接缝之后，测试可以传一个带偏移的矩形，把"源坐标必须用
    /// 入参原点"这个不变量变成可被变异检出的断言。
    /// </para>
    ///
    /// <para>
    /// <b>已知边界</b>：单屏机器造不出"负源坐标"的矩形，且 GDI 对越界源矩形的行为未定义，
    /// 因此本接缝只能覆盖<b>正偏移</b>。<b>负号本身</b>（副屏在主屏左/上时虚拟桌面原点为负）
    /// 仍只能靠真机手测兜底。
    /// </para>
    /// </summary>
    internal Bitmap CaptureRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new InvalidOperationException("无法确定虚拟桌面尺寸。");

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        try
        {
            if (!TryBitBlt(bitmap, region))
                FallbackCopyFromScreen(bitmap, region);

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static bool TryBitBlt(Bitmap target, Rectangle bounds)
    {
        using var graphics = Graphics.FromImage(target);
        IntPtr destDc = graphics.GetHdc();
        try
        {
            IntPtr sourceDc = GetDC(IntPtr.Zero);
            if (sourceDc == IntPtr.Zero)
                return false;

            try
            {
                return BitBlt(destDc, 0, 0, bounds.Width, bounds.Height,
                              sourceDc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, sourceDc);
            }
        }
        finally
        {
            graphics.ReleaseHdc(destDc);
        }
    }

    /// <summary>
    /// BitBlt 失败时的兜底。
    /// **刻意不做"全黑即重试"的启发式**：CopyFromScreen 走同一条 GDI 路径，
    /// 对驱动导致的黑屏没有帮助；而真正的黑屏（锁屏/DRM）是合法截图，
    /// 启发式只会误判并重复截屏。
    /// </summary>
    private static void FallbackCopyFromScreen(Bitmap target, Rectangle bounds)
    {
        try
        {
            using var graphics = Graphics.FromImage(target);
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException)
        {
            throw new ScreenCaptureFailedException("截屏失败，请重试。", ex);
        }
    }
}

/// <summary>截屏失败。上层据此显示"截屏失败，请重试"并回到 Idle。</summary>
public sealed class ScreenCaptureFailedException : Exception
{
    public ScreenCaptureFailedException(string message, Exception inner) : base(message, inner) { }
    public ScreenCaptureFailedException(string message) : base(message) { }
}
