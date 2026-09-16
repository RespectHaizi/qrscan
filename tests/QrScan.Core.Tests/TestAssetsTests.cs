using System.Drawing;
using System.Drawing.Imaging;

namespace QrScan.Core.Tests;

/// <summary>
/// 回归测试：TestAssets.Load 返回的位图必须与内嵌资源流解耦。
/// System.Drawing.Bitmap(Stream) 的契约要求流在 Bitmap 生命周期内保持打开；
/// 若 Load 在返回前释放了流，后续 Clone/Save 会抛出 GDI+ 的误导性
/// OutOfMemoryException（真实含义是"状态非法"）。解码器的裁剪路径依赖 Clone。
/// </summary>
public class TestAssetsTests
{
    [Fact]
    public void Loaded_asset_survives_clone()
    {
        using var original = TestAssets.Load("standard.png");

        using var clone = original.Clone(
            new Rectangle(0, 0, original.Width, original.Height),
            PixelFormat.Format32bppArgb);

        Assert.Equal(original.Size, clone.Size);
    }

    [Fact]
    public void Loaded_asset_survives_save()
    {
        using var original = TestAssets.Load("standard.png");
        string path = Path.Combine(Path.GetTempPath(), $"qrscan-asset-{Guid.NewGuid():N}.png");
        try
        {
            original.Save(path, ImageFormat.Png);
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
