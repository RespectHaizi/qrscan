using System.Drawing;
using QrScan.Core;
using QrScan.Core.Models;

namespace QrScan.Core.Tests;

public class QrDecoderTests
{
    private const string UrlA = "https://example.com/hello?from=qrscan";
    private const string UrlB = "https://example.org/second-code";

    private static QrDecoder New() => new();

    [Theory]
    [InlineData("standard.png", UrlA)]
    [InlineData("blurred.png", UrlA)]                              // 屏幕上被缩放过的图
    [InlineData("rotated.png", UrlA)]                              // 旋转 45 度
    [InlineData("tiny.png", "https://example.com/tiny")]           // 缩略图小码
    [InlineData("plaintext.png", "hello world, this is plain text")]
    [InlineData("wifi.png", "WIFI:T:WPA;S:mynet;P:s3cret;;")]
    public void Decodes_expected_text(string asset, string expected)
    {
        using var bmp = TestAssets.Load(asset);
        using var decoder = New();

        var result = decoder.Decode(bmp, null);

        Assert.Single(result);
        Assert.Equal(expected, result[0].Text);
    }

    [Fact]
    public void Detects_inverted_code_and_flags_it()
    {
        using var bmp = TestAssets.Load("inverted.png");
        using var decoder = New();

        var result = decoder.Decode(bmp, null);

        Assert.Single(result);
        Assert.Equal(UrlA, result[0].Text);
        Assert.True(result[0].IsInverted);
    }

    [Fact]
    public void Returns_all_codes_sorted_left_to_right()
    {
        // 回归：MaxNumberOfSymbols 的默认值是 1。若不显式设为 8，
        // 多码会被静默截断（且实测截断保留的是右侧那个码，不是左侧），
        // 于是"还有 N 个 ▸"会永远只有一个码可切。
        using var bmp = TestAssets.Load("multi.png");
        using var decoder = New();

        var result = decoder.Decode(bmp, null);

        Assert.Equal(2, result.Count);
        Assert.Equal(UrlA, result[0].Text);
        Assert.Equal(UrlB, result[1].Text);
        Assert.True(result[0].Left < result[1].Left, "结果必须按视觉位置从左到右排序");
    }

    [Fact]
    public void Returns_empty_when_no_code_present()
    {
        using var bmp = TestAssets.Load("nocode.png");
        using var decoder = New();

        Assert.Empty(decoder.Decode(bmp, null));
    }

    [Fact]
    public void Honours_crop_rectangle()
    {
        using var bmp = TestAssets.Load("multi.png");
        using var decoder = New();

        var leftHalf = new Rectangle(0, 0, bmp.Width / 2, bmp.Height);
        var result = decoder.Decode(bmp, leftHalf);

        Assert.Single(result);
        Assert.Equal(UrlA, result[0].Text);
    }

    [Fact]
    public void Returns_empty_for_degenerate_crop_without_touching_native_code()
    {
        // 非法尺寸传进原生 C++ 会直接杀进程（AccessViolation 不可捕获），
        // 所以必须在托管侧拦下并返回空结果。
        using var bmp = TestAssets.Load("standard.png");
        using var decoder = New();

        Assert.Empty(decoder.Decode(bmp, new Rectangle(0, 0, 0, 0)));
        Assert.Empty(decoder.Decode(bmp, new Rectangle(0, 0, -5, 10)));
        Assert.Empty(decoder.Decode(bmp, new Rectangle(-9999, -9999, 10, 10)));
    }

    [Fact]
    public void Crop_outside_bounds_is_clamped_not_thrown()
    {
        using var bmp = TestAssets.Load("standard.png");
        using var decoder = New();

        // 部分越界：应被 clamp 到边界内并成功识别
        var oversized = new Rectangle(0, 0, bmp.Width + 500, bmp.Height + 500);
        var result = decoder.Decode(bmp, oversized);

        Assert.Single(result);
        Assert.Equal(UrlA, result[0].Text);
    }

    [Fact]
    public void SelfTest_passes()
    {
        using var decoder = New();
        Assert.True(decoder.SelfTest(), "启动自检必须成功：内嵌 QRSCAN 样张解不出来说明原生链断了");
    }

    [Fact]
    public void Decode_throws_on_null_bitmap()
    {
        using var decoder = New();
        Assert.Throws<ArgumentNullException>(() => decoder.Decode(null!, null));
    }
}
