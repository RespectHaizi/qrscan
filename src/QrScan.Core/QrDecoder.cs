using System.Drawing;
using System.Drawing.Imaging;
using QrScan.Core.Models;
using ZXingCpp;
using ZxImageFormat = ZXingCpp.ImageFormat;   // 与 System.Drawing.Imaging.ImageFormat 同名，必须起别名

namespace QrScan.Core;

/// <summary>
/// ZXing-C++ 识别引擎的封装。这是本项目中唯一接触非托管代码的单元，
/// 因此它被设计成可以不依赖 UI 直接单测。
/// </summary>
public sealed class QrDecoder : IDisposable
{
    /// <summary>同时最多返回的码数。**必须显式设置**：引擎默认值是 1。</summary>
    private const int MaxSymbols = 8;

    /// <summary>启动自检样张的内容。</summary>
    internal const string SelfTestExpectedText = "QRSCAN";

    private readonly BarcodeReader _reader;

    public QrDecoder()
    {
        _reader = new BarcodeReader
        {
            // 只开二维码：这是扫码器不是条码枪。少跑候选解码器 → 更快且更少误报。
            Formats = BarcodeFormats.Parse("QRCode"),
            // 深色主题网页/终端里的反白码极其常见，不开就是白扫。
            TryInvert = true,
            // 用一点时间换检出率 —— 这正是选 ZXing-C++ 而非 ZXing.NET 的初衷。
            TryHarder = true,
            // 默认值是 1！不设置会让多码功能静默失效。
            MaxNumberOfSymbols = MaxSymbols,
            // 其余参数保持默认（Binarizer.LocalAverage 等）。
        };
    }

    /// <summary>
    /// 识别图像（可先按 <paramref name="crop"/> 裁剪）。
    /// 结果按 <c>Position.TopLeft.X</c> 再 <c>Y</c> 升序排列，即视觉上从左到右、从上到下。
    /// 识别不到时返回空列表，不抛异常。
    /// </summary>
    public IReadOnlyList<QrPayload> Decode(Bitmap image, Rectangle? crop)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (crop is not { } requested)
            return DecodeCore(image);

        var clamped = Clamp(requested, image.Width, image.Height);
        if (clamped.Width <= 0 || clamped.Height <= 0)
            return Array.Empty<QrPayload>();

        using var cropped = image.Clone(clamped, PixelFormat.Format32bppArgb);
        return DecodeCore(cropped);
    }

    /// <summary>
    /// 启动自检：用内嵌的 21x21 二维码验证整条原生链是否可用。
    /// 该样张由硬编码矩阵绘制，**不依赖 ZXing-C++ 的写端**，
    /// 因此不会出现"写端坏了导致自检假失败"。
    /// </summary>
    public bool SelfTest()
    {
        using var bitmap = BuildSelfTestBitmap();
        var payloads = DecodeCore(bitmap);
        return payloads.Any(p => p.Text == SelfTestExpectedText);
    }

    public void Dispose() => _reader.Dispose();

    private IReadOnlyList<QrPayload> DecodeCore(Bitmap bitmap)
    {
        var pixels = LuminanceConverter.ToLum8(bitmap, out int width, out int height);

        // 原生调用前置校验：非法尺寸/长度传进 C++ 会直接杀掉进程，
        // AccessViolationException 不可捕获，因此这里是唯一的防线。
        if (width <= 0 || height <= 0 || pixels.Length < width * height)
            return Array.Empty<QrPayload>();

        var view = new ImageView(pixels.AsSpan(), width, height, ZxImageFormat.Lum, width, 1);
        var found = _reader.From(view);

        if (found.Length == 0)
            return Array.Empty<QrPayload>();

        return found
            .OrderBy(b => b.Position.TopLeft.X)
            .ThenBy(b => b.Position.TopLeft.Y)
            .Select(b => new QrPayload
            {
                Text = b.Text,
                Format = b.Format.ToString(),
                IsInverted = b.IsInverted,
                Left = b.Position.TopLeft.X,
                Top = b.Position.TopLeft.Y,
            })
            .ToArray();
    }

    private static Rectangle Clamp(Rectangle r, int maxWidth, int maxHeight)
    {
        int left = Math.Max(0, r.Left);
        int top = Math.Max(0, r.Top);
        int right = Math.Min(maxWidth, r.Right);
        int bottom = Math.Min(maxHeight, r.Bottom);

        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>内容为 "QRSCAN" 的 version-1（21x21）二维码矩阵，纠错等级 M。</summary>
    private static readonly string[] SelfTestMatrix =
    {
        "111111100110101111111",
        "100000101100001000001",
        "101110100010001011101",
        "101110100100101011101",
        "101110100101001011101",
        "100000101111001000001",
        "111111101010101111111",
        "000000001000100000000",
        "000011110101101100010",
        "111100011101011100001",
        "101101101101111110011",
        "010110000100100010101",
        "010001110101010001010",
        "000000001000110000111",
        "111111101011001001010",
        "100000101101100100011",
        "101110101000110111011",
        "101110100101011001111",
        "101110100011111111100",
        "100000100101100110001",
        "111111100010110000111",
    };

    private static Bitmap BuildSelfTestBitmap()
    {
        const int moduleSize = 4;
        const int quietZone = 4;
        int modules = SelfTestMatrix.Length;                // 21
        int side = (modules + quietZone * 2) * moduleSize;  // 116

        var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            for (int y = 0; y < modules; y++)
            {
                string row = SelfTestMatrix[y];
                for (int x = 0; x < modules; x++)
                {
                    if (row[x] == '1')
                        graphics.FillRectangle(brush,
                            (x + quietZone) * moduleSize,
                            (y + quietZone) * moduleSize,
                            moduleSize, moduleSize);
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
