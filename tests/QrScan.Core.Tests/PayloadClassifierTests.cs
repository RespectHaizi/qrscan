using QrScan.Core;
using QrScan.Core.Models;

namespace QrScan.Core.Tests;

public class PayloadClassifierTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/hello?from=qrscan")]
    [InlineData("HTTPS://EXAMPLE.COM")]                 // scheme 大小写不敏感
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+8613800138000")]
    public void Recognises_openable_urls(string text)
    {
        var kind = PayloadClassifier.Classify(text);
        Assert.Equal(PayloadKind.Url, kind);
        Assert.True(PayloadClassifier.CanOpen(kind));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]   // 文件 scheme 不白名单
    [InlineData("ms-msdt:/id PCWDiagnostic")]              // 系统自带的协议处理器
    [InlineData("search-ms:query=secret")]
    [InlineData("vbs:malware")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vscode://file/c:/x")]
    [InlineData("weixin://dl/business")]
    public void Refuses_to_open_custom_schemes(string text)
    {
        // 安全边界：二维码内容来自不可信来源。ShellExecute 会把自定义 scheme
        // 交给系统中注册的任意协议处理器执行，因此一律不可打开。
        var kind = PayloadClassifier.Classify(text);
        Assert.Equal(PayloadKind.PlainText, kind);
        Assert.False(PayloadClassifier.CanOpen(kind));
    }

    [Theory]
    [InlineData("hello world, this is plain text")]
    [InlineData("WIFI:T:WPA;S:mynet;P:s3cret;;")]          // WiFi 码不是"可打开"内容
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nFN:A\nEND:VCARD")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Treats_non_openable_text_as_plain(string? text)
    {
        Assert.Equal(PayloadKind.PlainText, PayloadClassifier.Classify(text));
    }

    [Fact]
    public void Recognises_existing_local_file_as_openable_path()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qrscan-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");
        try
        {
            var kind = PayloadClassifier.Classify(path);
            Assert.Equal(PayloadKind.FilePath, kind);
            Assert.True(PayloadClassifier.CanOpen(kind));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Recognises_existing_directory_as_openable_path()
    {
        var kind = PayloadClassifier.Classify(Path.GetTempPath().TrimEnd('\\'));
        Assert.Equal(PayloadKind.FilePath, kind);
    }

    [Fact]
    public void Non_existent_looking_path_is_not_openable()
    {
        var kind = PayloadClassifier.Classify(@"C:\definitely\not\here\12345.exe");
        Assert.Equal(PayloadKind.PlainText, kind);
    }

    [Fact]
    public void Long_text_is_not_probed_as_a_file_path()
    {
        // 纯文本二维码可能很长；不应对任意长文本做文件系统探测
        var kind = PayloadClassifier.Classify(@"C:\" + new string('a', 400));
        Assert.Equal(PayloadKind.PlainText, kind);
    }

    [Fact]
    public void Classify_trims_surrounding_whitespace()
    {
        Assert.Equal(PayloadKind.Url, PayloadClassifier.Classify("  https://example.com  "));
    }

    [Fact]
    public void Path_containing_a_quote_cannot_reach_the_explorer_branch()
    {
        // 把"含引号的目标进不了 explorer 分支"从推理变成可执行断言：
        // `"` 是 Windows 文件名非法字符，File.Exists / Directory.Exists 对它返回 false，
        // 因此目标根本进不到资源管理器分支 —— Arguments 里不可能出现能改变
        // Explorer 命令行的字符。
        Assert.Equal(PayloadKind.PlainText, PayloadClassifier.Classify("C:\\tmp\\a\"b.txt"));
    }

    [Fact]
    public void Unc_path_is_never_probed()
    {
        // 回归测试（安全）：UNC 路径绝不能触发文件系统探测。
        // 探测它会向**二维码指定的主机**发起网络连接 —— 该主机由二维码作者决定，
        // 属于不可信输入，因此探测必须在碰磁盘之前被拦下。
        // 关键在于泄露发生在"分类"阶段而不是"打开"阶段 —— 扫到码就触发，
        // 不需要用户点任何按钮。扫码器天然会去扫来源不可信的码，
        // 所以这条必须彻底堵死。
        Assert.False(PayloadClassifier.IsProbeablePath(@"\\attacker.example.com\share\secret.txt"));
        Assert.False(PayloadClassifier.IsProbeablePath("//attacker.example.com/share/secret.txt"));

        Assert.Equal(PayloadKind.PlainText,
            PayloadClassifier.Classify(@"\\attacker.example.com\share\secret.txt"));
    }

    [Fact]
    public void Local_drive_path_is_still_probed()
    {
        // 反向断言：堵 UNC 不能把本工具的核心用途一起砍掉。
        Assert.True(PayloadClassifier.IsProbeablePath(@"C:\Windows"));
        Assert.Equal(PayloadKind.FilePath, PayloadClassifier.Classify(@"C:\Windows"));
    }

    [Fact]
    public void Probeability_rejects_oversized_multiline_relative_and_short_text()
    {
        Assert.False(PayloadClassifier.IsProbeablePath(@"C:\" + new string('a', 400)));   // 超长
        Assert.False(PayloadClassifier.IsProbeablePath("C:\\tmp\\a\nb.txt"));            // 含换行
        Assert.False(PayloadClassifier.IsProbeablePath("hello world"));                    // 不是绝对形态
        Assert.False(PayloadClassifier.IsProbeablePath("C:relative"));                     // 盘符但非绝对
        Assert.False(PayloadClassifier.IsProbeablePath("C:"));                             // 太短
        Assert.False(PayloadClassifier.IsProbeablePath(string.Empty));
    }
}
