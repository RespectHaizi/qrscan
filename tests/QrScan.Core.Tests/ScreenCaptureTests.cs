using System.Drawing;
using QrScan.Core;

namespace QrScan.Core.Tests;

public class ScreenCaptureTests
{
    [Fact]
    public void Virtual_screen_rectangle_is_valid()
    {
        var capture = new ScreenCapture();
        var vs = capture.VirtualScreenPhysical();

        Assert.True(vs.Width > 0, "虚拟桌面宽度必须为正");
        Assert.True(vs.Height > 0, "虚拟桌面高度必须为正");
    }

    [Fact]
    public void Capture_produces_bitmap_matching_virtual_screen_size()
    {
        var capture = new ScreenCapture();
        var vs = capture.VirtualScreenPhysical();

        using var bmp = capture.CaptureVirtualScreen();

        Assert.Equal(vs.Width, bmp.Width);
        Assert.Equal(vs.Height, bmp.Height);
    }

    [Fact]
    public void Capture_is_not_blank()
    {
        var capture = new ScreenCapture();
        using var bmp = capture.CaptureVirtualScreen();

        // 采样若干点，断言至少存在两个不同的颜色值。
        // 屏幕全黑是合法结果（锁屏/DRM），但在一台正常运行且已登录的机器上不该发生。
        var seen = new HashSet<int>();
        for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 16))
            for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 16))
                seen.Add(bmp.GetPixel(x, y).ToArgb());

        Assert.True(seen.Count > 1, $"截屏结果疑似空白（仅 {seen.Count} 种颜色）");
    }

    [Fact]
    public void Capture_can_be_repeated()
    {
        var capture = new ScreenCapture();

        using var first = capture.CaptureVirtualScreen();
        using var second = capture.CaptureVirtualScreen();

        Assert.Equal(first.Size, second.Size);
    }

    /// <summary>
    /// <c>BitBlt</c> 的源坐标必须用**入参矩形的原点**，而不是字面量 <c>0</c>。
    ///
    /// <para>
    /// 这条测试是<b>单屏机器上唯一能覆盖该路径的手段</b>：<c>CaptureVirtualScreen()</c>
    /// 走的是虚拟桌面矩形，单屏下原点恰好是 <c>(0,0)</c>，把源坐标写成 <c>0</c> 也不会有
    /// 任何区别，于是四个旧用例全绿。<see cref="ScreenCapture.CaptureRegion"/> 这个接缝
    /// 让我们能传一个带偏移的矩形，把"偏移截取 == 全屏截取的对应子区域"变成真的断言。
    /// </para>
    ///
    /// <para>
    /// <b>已知边界</b>：本用例只覆盖<b>正偏移</b>。单屏造不出负源坐标矩形，而且 GDI 对越界
    /// 源矩形的行为未定义 —— 因此<b>负号本身</b>（副屏在主屏左/上时虚拟桌面原点为负）
    /// 仍只能靠 README 手测第 2/3/4 项真机兜底。
    /// </para>
    /// </summary>
    [Fact]
    public void CaptureRegion_uses_region_origin_as_source_coordinates()
    {
        var capture = new ScreenCapture();
        Rectangle vs = capture.VirtualScreenPhysical();

        const int Offset = 100;
        var region = new Rectangle(vs.X + Offset, vs.Y + Offset,
                                   vs.Width - Offset * 2, vs.Height - Offset * 2);

        Assert.True(region.Width > 200 && region.Height > 200,
            $"虚拟桌面 {vs.Width}×{vs.Height} 太小，无法做 {Offset}px 偏移的截取比对");

        using var full = capture.CaptureVirtualScreen();
        using var offset = capture.CaptureRegion(region);

        Assert.Equal(region.Width, offset.Width);
        Assert.Equal(region.Height, offset.Height);

        // 逐点比对：偏移图 (x,y) 必须等于全屏图 (x+Offset, y+Offset)。
        // 容忍屏幕上会动的像素（锁屏时钟、动画）—— 两次抓图不是同一瞬间。
        int total = 0, hit = 0;
        for (int y = 20; y < region.Height - 20; y += 40)
        {
            for (int x = 20; x < region.Width - 20; x += 40)
            {
                total++;
                if (full.GetPixel(x + Offset, y + Offset) == offset.GetPixel(x, y))
                    hit++;
            }
        }

        Assert.True(total >= 100, $"采样点数太少（{total}），无法得出可靠结论");

        double rate = (double)hit / total;
        Assert.True(rate >= 0.9,
            $"偏移截取与全屏截取的对应子区域应当一致，实际命中 {hit}/{total} = {rate:P1}。" +
            "大面积不符说明 BitBlt 的源坐标没有使用 region 的原点" +
            "（把 TryBitBlt 里的 region.X/region.Y 改成字面量 0 会让本用例变红）。");
    }
}
