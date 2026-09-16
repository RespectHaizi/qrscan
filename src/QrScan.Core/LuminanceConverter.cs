using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QrScan.Core;

/// <summary>
/// 把任意 <see cref="Bitmap"/> 转成 ZXing-C++ 需要的 8 位灰度缓冲。
/// 确定性函数（同输入必得同输出），因此是 static —— 见规格 §4.1 的规则。
/// </summary>
internal static class LuminanceConverter
{
    /// <summary>
    /// 使用 ITU-R BT.601 加权（299/587/114，除以 1000，四舍五入）转灰度。
    /// 输出**紧密排列**：<c>result.Length == width * height</c>，
    /// 可直接以 <c>rowStride = width, pixStride = 1</c> 交给 <c>ImageView</c>。
    /// </summary>
    internal static byte[] ToLum8(Bitmap source, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(source);

        width = source.Width;
        height = source.Height;

        if (width <= 0 || height <= 0)
            throw new ArgumentException("位图尺寸必须为正数。", nameof(source));

        var buffer = new byte[width * height];

        // 统一成 32bppArgb 再取像素，避免逐像素 GetPixel（极慢）与格式分支。
        // SourceCopy 保证不透明的源不会被 alpha 混合影响，结果与源像素一一对应。
        using var argb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(argb))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
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
                    // 源按 stride 走（可能含行尾填充），目标按 width 紧密排列
                    byte* row = basePtr + y * data.Stride;
                    int outputRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int b = row[x * 4 + 0];
                        int g = row[x * 4 + 1];
                        int r = row[x * 4 + 2];
                        buffer[outputRow + x] = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
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
