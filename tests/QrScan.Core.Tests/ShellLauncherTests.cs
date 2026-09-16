using System.Diagnostics;
using QrScan.Core;

namespace QrScan.Core.Tests;

/// <summary>
/// <see cref="ShellLauncher"/> 的安全边界测试。
///
/// 这些测试**只构造 <see cref="ProcessStartInfo"/>，不启动任何进程** ——
/// 全部决策都收敛在纯函数 <c>BuildStartInfo</c> 里，所以这条边界
/// （而不是"真的去开一个东西"）可以被自动化断言。
/// </summary>
public class ShellLauncherTests
{
    [Theory]
    [InlineData("ms-msdt:/id PCWDiagnostic")]                  // 系统自带的协议处理器
    [InlineData("vbs:malware")]
    [InlineData("search-ms:query=secret")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("hello world, this is plain text")]
    public void Refuses_targets_outside_the_whitelist(string target)
    {
        // 安全边界：二维码内容来自不可信来源。ShellExecute 会把自定义 scheme
        // 交给系统中注册的任意协议处理器执行，因此一律不可打开。
        var ex = Assert.Throws<InvalidOperationException>(() => ShellLauncher.BuildStartInfo(target));

        Assert.Contains("拒绝打开非白名单目标", ex.Message);
    }

    [Fact]
    public void Blank_target_is_a_caller_error()
    {
        Assert.Throws<ArgumentException>(() => ShellLauncher.BuildStartInfo("   "));
    }

    [Fact]
    public void Directory_is_opened_directly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"qrscan-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var psi = ShellLauncher.BuildStartInfo(dir);

            Assert.Equal("explorer.exe", psi.FileName);
            Assert.Equal($"\"{dir}\"", psi.Arguments);   // 裸引号路径 → 打开文件夹
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".exe")]        // ← 安全相关的那个：绝不能被一步执行
    [InlineData(".bat")]
    public void File_is_revealed_in_explorer_not_handed_to_shellexecute(string extension)
    {
        // 回归测试：旧行为是 explorer.exe "<文件>"，这会把参数交给 ShellExecute
        // 走该文件的默认处理程序 —— .exe/.bat 会被**直接执行**。
        // 正确行为是在资源管理器里定位并选中，由用户自己决定是否运行。
        string file = Path.Combine(Path.GetTempPath(), $"qrscan-launcher-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(file, "x");
        try
        {
            var psi = ShellLauncher.BuildStartInfo(file);

            Assert.Equal("explorer.exe", psi.FileName);
            Assert.Equal($"/select,\"{file}\"", psi.Arguments);   // 注意 /select, 后无空格
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("https://example.com/hello?from=qrscan")]
    [InlineData("http://example.com")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+8613800138000")]
    public void Whitelisted_url_uses_the_system_default_handler(string url)
    {
        var psi = ShellLauncher.BuildStartInfo(url);

        Assert.True(psi.UseShellExecute);
        Assert.Equal(url, psi.FileName);
    }

    [Fact]
    public void Target_is_trimmed_so_classification_and_launching_agree()
    {
        // 回归测试：分类走的是 PayloadClassifier.Classify（内部会 Trim），
        // 而 BuildStartInfo 原先用的是**未 trim 的原串** ——
        // "  C:\some\dir  " 会被判为 FilePath，但 Directory.Exists("  C:\some\dir  ") 为假，
        // 于是落进 /select, 分支（本该打开文件夹）。
        string dir = Path.Combine(Path.GetTempPath(), $"qrscan-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var psi = ShellLauncher.BuildStartInfo($"  {dir}  ");

            Assert.Equal("explorer.exe", psi.FileName);
            Assert.Equal($"\"{dir}\"", psi.Arguments);      // 不带空格
            Assert.DoesNotContain("/select,", psi.Arguments);   // 且不该落进"文件"分支
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Target_containing_a_quote_cannot_reach_the_explorer_branch()
    {
        // 发现 4 的硬证据，补在真正危险的那一层（参数拼接处）：
        // `"` 是 Windows 文件名非法字符 → Classify 得 PlainText → 白名单守卫拒绝，
        // 所以这个字串根本到不了 explorer.exe 的参数里。
        var ex = Assert.Throws<InvalidOperationException>(
            () => ShellLauncher.BuildStartInfo("C:\\tmp\\a\"b.txt"));

        Assert.Contains("拒绝打开非白名单目标", ex.Message);
    }
}
