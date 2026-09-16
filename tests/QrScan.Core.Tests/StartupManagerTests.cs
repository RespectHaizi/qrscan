using Microsoft.Win32;
using QrScan.Core;

namespace QrScan.Core.Tests;

/// <summary>
/// 真读写 HKCU，但指向测试专用子键并在测后清理。
/// 键路径可注入是本设计与生俱来的可测性接缝（规格 §4.3⑨）。
/// </summary>
public class StartupManagerTests : IDisposable
{
    private const string TestSubKey = @"Software\QrScan\Test";
    private const string ParentSubKey = @"Software\QrScan";
    private const string TestValueName = "QrScanTest";

    private static StartupManager New()
    {
        // 前置清理：让每个用例都从确定状态开始。
        // 若上一次运行在 SetEnabled(true) 之后崩溃（Dispose 未执行），残留值会让
        // Is_disabled_by_default 失败，而报错会指向实现 —— 实际是环境残留，会误导排查。
        using (var key = Registry.CurrentUser.OpenSubKey(TestSubKey, writable: true))
            key?.DeleteValue(TestValueName, throwOnMissingValue: false);

        return new StartupManager(Registry.CurrentUser, TestSubKey, TestValueName);
    }

    public void Dispose()
    {
        TryDeleteTestArtifacts();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 只清理测试自己创建的东西：先删 <c>Software\QrScan\Test</c> 子键，
    /// 再在父键 <c>Software\QrScan</c> **完全为空**（无子键、无值）时才删它。
    ///
    /// **绝不无条件删父树** —— <c>HKCU\Software\&lt;产品名&gt;</c> 是安装器、文件关联与
    /// shell 集成的常规落点；越界清理会在将来某个任务往那里写状态后，把产品数据静默毁掉。
    /// </summary>
    private static void TryDeleteTestArtifacts()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestSubKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return;   // 别的进程在用或权限不足：安全判断也做不了，直接放弃
        }

        try
        {
            // 先读计数再关句柄，避免“键被打开着删不掉”
            bool parentIsEmpty;
            using (var parent = Registry.CurrentUser.OpenSubKey(ParentSubKey))
                parentIsEmpty = parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0;

            if (parentIsEmpty)
                Registry.CurrentUser.DeleteSubKey(ParentSubKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            /* 父键清不掉不影响测试结果 */
        }
    }

    [Fact]
    public void Is_disabled_by_default()
    {
        var manager = New();
        Assert.False(manager.IsEnabled);
        Assert.Null(manager.RegisteredPath);
    }

    [Fact]
    public void Empty_registered_value_counts_as_disabled()
    {
        // Run 键下可能存在一个空的 REG_SZ 值（别的程序写入后未清理，或写入被中断）。
        // 空值必须等同于"未启用"：否则托盘菜单会显示一个勾，而开机并不会启动 —— 一个静默的假象，
        // 且用户会以为自启已经工作了。
        var manager = New();

        // 写值必须在 New() 之后：New() 会做前置清理，先写会被它删掉。
        using (var key = Registry.CurrentUser.CreateSubKey(TestSubKey, writable: true))
            key!.SetValue(TestValueName, string.Empty, RegistryValueKind.String);

        // 先确认这个空值确实写进去了。没有这一步，若值不存在，RegisteredPath 同样是 null，
        // 本用例会变成"值不存在"的假通过，而它的全部意义就在于区分这两种情况。
        using (var verify = Registry.CurrentUser.OpenSubKey(TestSubKey))
            Assert.Equal(string.Empty, verify!.GetValue(TestValueName));

        Assert.False(manager.IsEnabled);
        Assert.Null(manager.RegisteredPath);
    }

    [Fact]
    public void Enable_writes_quoted_current_exe_path()
    {
        var manager = New();

        manager.SetEnabled(true);

        Assert.True(manager.IsEnabled);
        string? registered = manager.RegisteredPath;
        Assert.False(string.IsNullOrWhiteSpace(registered));
        Assert.Equal(StartupManager.CurrentExecutablePath(), registered);

        using var key = Registry.CurrentUser.OpenSubKey(TestSubKey);
        string raw = (string)key!.GetValue(TestValueName)!;
        Assert.StartsWith("\"", raw);
        Assert.EndsWith("\"", raw);
    }

    [Fact]
    public void Disable_removes_the_value_and_does_not_throw_when_absent()
    {
        var manager = New();
        manager.SetEnabled(true);

        manager.SetEnabled(false);

        Assert.False(manager.IsEnabled);
        Assert.Null(manager.RegisteredPath);
        Assert.Null(Registry.CurrentUser.OpenSubKey(TestSubKey)?.GetValue(TestValueName));
    }

    [Fact]
    public void Disable_is_idempotent()
    {
        var manager = New();
        manager.SetEnabled(false);
        manager.SetEnabled(false);
        Assert.False(manager.IsEnabled);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\QrScan\QrScan.exe""", @"C:\Program Files\QrScan\QrScan.exe")]
    [InlineData(@"C:\QrScan\QrScan.exe", @"C:\QrScan\QrScan.exe")]
    [InlineData(@"""C:\QrScan\QrScan.exe"" --flag", @"C:\QrScan\QrScan.exe")]
    public void Registered_path_tolerates_quoting_variants(string raw, string expected)
    {
        Assert.Equal(expected, StartupManager.NormalizeRegisteredPath(raw));
    }

    [Fact]
    public void Quote_wraps_path_so_spaces_do_not_break_autostart()
    {
        // 路径含空格时不加引号，Windows 会把 "C:\Program" 当成程序名，开机静默启动失败。
        Assert.Equal(@"""C:\Program Files\QrScan\QrScan.exe""",
                     StartupManager.Quote(@"C:\Program Files\QrScan\QrScan.exe"));
    }

    [Fact]
    public void Current_exe_path_uses_ProcessPath_so_single_file_publish_works()
    {
        // 契约测试：锁住"返回进程可执行文件路径而非程序集路径"。
        // 单文件发布下的端到端验证在任务 16 的手测第 11 项（用发布的 exe 跑）。
        // 单文件发布下 Assembly.Location 是空字符串，必须用 Environment.ProcessPath
        string path = StartupManager.CurrentExecutablePath();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.Equal(Environment.ProcessPath, path);
    }
}
