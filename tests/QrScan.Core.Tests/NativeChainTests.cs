using System.Drawing;
using System.Drawing.Imaging;
using ZXingCpp;
using ZxImageFormat = ZXingCpp.ImageFormat;

namespace QrScan.Core.Tests;

/// <summary>
/// 原生识别链哨兵：证明 ZXingCpp 托管包装 + 原生 ZXing.dll 在当前环境可用。
/// 这个测试直接调用 ZXingCpp，不经过 QrDecoder —— 它回答的是"原生链通不通"，
/// 而不是"我们的封装对不对"（后者由 QrDecoderTests 回答）。
/// </summary>
public class NativeChainTests
{
    [Fact]
    public void Native_engine_decodes_embedded_selftest_image()
    {
        using var bmp = TestAssets.Load("selftest.png");
        var pixels = ToLum8(bmp, out int w, out int h);

        var view = new ImageView(pixels.AsSpan(), w, h, ZxImageFormat.Lum, w, 1);
        using var reader = new BarcodeReader { Formats = BarcodeFormats.Parse("QRCode") };
        var found = reader.From(view);

        Assert.Single(found);
        Assert.Equal("QRSCAN", found[0].Text);
    }

    private static byte[] ToLum8(Bitmap src, out int width, out int height)
    {
        width = src.Width;
        height = src.Height;
        var buffer = new byte[width * height];

        using var argb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(argb))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(src, new Rectangle(0, 0, width, height));
        }

        var data = argb.LockBits(new Rectangle(0, 0, width, height),
                                 ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < height; y++)
                {
                    byte* row = basePtr + y * data.Stride;
                    int outRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int b = row[x * 4 + 0];
                        int g = row[x * 4 + 1];
                        int r = row[x * 4 + 2];
                        buffer[outRow + x] = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
                    }
                }
            }
        }
        finally
        {
            argb.UnlockBits(data);
        }

        return buffer;
    }
}
