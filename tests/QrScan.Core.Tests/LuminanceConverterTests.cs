using System.Drawing;
using System.Drawing.Imaging;
using QrScan.Core;

namespace QrScan.Core.Tests;

public class LuminanceConverterTests
{
    private static Bitmap Solid(int w, int h, Color color)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    [Fact]
    public void White_maps_to_255()
    {
        using var bmp = Solid(4, 4, Color.White);
        var lum = LuminanceConverter.ToLum8(bmp, out int w, out int h);
        Assert.Equal(4, w);
        Assert.Equal(4, h);
        Assert.All(lum, v => Assert.Equal(255, v));
    }

    [Fact]
    public void Black_maps_to_0()
    {
        using var bmp = Solid(4, 4, Color.Black);
        var lum = LuminanceConverter.ToLum8(bmp, out _, out _);
        Assert.All(lum, v => Assert.Equal(0, v));
    }

    [Theory]
    [InlineData(255, 0, 0, 76)]     // 红：(255*299 + 500) / 1000
    [InlineData(0, 255, 0, 150)]    // 绿：(255*587 + 500) / 1000
    [InlineData(0, 0, 255, 29)]     // 蓝：(255*114 + 500) / 1000
    public void Uses_bt601_weights(int r, int g, int b, int expected)
    {
        using var bmp = Solid(2, 2, Color.FromArgb(255, r, g, b));
        var lum = LuminanceConverter.ToLum8(bmp, out _, out _);
        Assert.All(lum, v => Assert.Equal(expected, v));
    }

    [Fact]
    public void Output_is_tightly_packed_for_widths_not_divisible_by_four()
    {
        // 源 32bppArgb 的 stride 恒为 width*4（已 4 字节对齐），但目标必须是
        // 紧密排列的 Lum8 缓冲：长度恰为 w*h，像素按 y*width+x 索引。
        // 这里用 3x2 的小图并让两个黑像素落在不同行，来验证按行重排的正确性。
        const int w = 3, h = 2;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 0, 0, 1, 1);   // 仅左上角为黑
            g.FillRectangle(Brushes.Black, 2, 1, 1, 1);   // 右下角为黑
        }

        var lum = LuminanceConverter.ToLum8(bmp, out int outW, out int outH);

        Assert.Equal(w, outW);
        Assert.Equal(h, outH);
        Assert.Equal(w * h, lum.Length);                  // 紧密排列，长度恰好 w*h
        Assert.Equal(0, lum[0]);                          // (0,0) 黑
        Assert.Equal(0, lum[1 * w + 2]);                  // (2,1) 黑
        Assert.Equal(255, lum[1]);                        // (1,0) 白
        Assert.Equal(255, lum[1 * w + 0]);                // (0,1) 白
    }

    [Fact]
    public void Handles_one_by_one_bitmap()
    {
        using var bmp = Solid(1, 1, Color.White);
        var lum = LuminanceConverter.ToLum8(bmp, out int w, out int h);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(new byte[] { 255 }, lum);
    }

    [Fact]
    public void Throws_on_null()
    {
        Assert.Throws<ArgumentNullException>(() => LuminanceConverter.ToLum8(null!, out _, out _));
    }
}
