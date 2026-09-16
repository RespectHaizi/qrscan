using QrScan.Core;
using QrScan.Core.Models;

namespace QrScan.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qrscan-cfg-{Guid.NewGuid():N}");
    private string Path_ => Path.Combine(_dir, "config.json");

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_yields_defaults_and_reports_created()
    {
        var store = new ConfigStore(Path_);

        var result = store.Load();

        Assert.Equal(ConfigLoadStatus.CreatedDefault, result.Status);
        Assert.Equal("Ctrl+Shift+Q", result.Config.Hotkey);
        Assert.Equal(2000, result.Config.ToastDismissMs);
        Assert.Equal(10, result.Config.RecentLimit);
        Assert.True(result.Config.SelfTestOnStartup);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[]")]                        // 合法 JSON 但是错的形状
    [InlineData("null")]
    [InlineData("{\"hotkey\": 12345}")]       // 类型不对
    public void Corrupt_file_yields_defaults_without_throwing(string content)
    {
        File.WriteAllText(Path_, content);
        var store = new ConfigStore(Path_);

        var result = store.Load();

        Assert.Equal(ConfigLoadStatus.RecoveredFromCorrupt, result.Status);
        Assert.Equal("Ctrl+Shift+Q", result.Config.Hotkey);
    }

    [Fact]
    public void Round_trips_all_fields()
    {
        var store = new ConfigStore(Path_);
        var config = new AppConfig
        {
            Hotkey = "Alt+Shift+Z",
            ToastDismissMs = 3500,
            RecentLimit = 5,
            SelfTestOnStartup = false,
        };

        store.Save(config);
        var result = store.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(config, result.Config);
    }

    [Fact]
    public void Written_file_is_camel_case_json()
    {
        var store = new ConfigStore(Path_);
        store.Save(new AppConfig());

        string text = File.ReadAllText(Path_);

        Assert.Contains("\"hotkey\"", text);
        Assert.Contains("\"toastDismissMs\"", text);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        File.WriteAllText(Path_, """
        { "hotkey": "Ctrl+Alt+R", "somethingFromTheFuture": true }
        """);
        var store = new ConfigStore(Path_);

        var result = store.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal("Ctrl+Alt+R", result.Config.Hotkey);
    }

    [Fact]
    public void Save_creates_missing_directory()
    {
        string nested = Path.Combine(_dir, "a", "b", "config.json");
        var store = new ConfigStore(nested);

        store.Save(new AppConfig());

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Default_path_is_under_appdata_qrscan()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QrScan", "config.json");

        Assert.Equal(expected, ConfigStore.DefaultPath());
    }

    /// <summary>
    /// <c>Load()</c> 永不抛异常是这个类的核心契约，而 I/O 失败是"配置取不到"的第二大成因
    /// （第一是 JSON 损坏）。上面那 5 个 Theory 输入**全部经由 JsonException/null** 抵达
    /// <c>RecoveredFromCorrupt</c>，于是 <c>catch</c> 里的 <c>IOException</c> 分支零覆盖 ——
    /// 把异常过滤器改成只留 <c>JsonException</c> 也仍然全绿。
    ///
    /// <para>
    /// 这里用一个<b>独占句柄</b>制造真实的共享冲突：<c>File.ReadAllText</c> 会以
    /// <c>FileShare.Read</c> 打开，而该文件已被 <c>FileShare.None</c> 持有，于是必然抛
    /// <see cref="IOException"/>。不依赖任何平台特有的权限设置。
    /// </para>
    /// </summary>
    [Fact]
    public void Unreadable_file_yields_defaults_without_throwing()
    {
        File.WriteAllText(Path_, """{ "hotkey": "Ctrl+Alt+R" }""");
        var store = new ConfigStore(Path_);

        // 先确认"能读的时候确实读得到" —— 否则这个用例可能只是因为别的原因碰巧过了。
        Assert.Equal(ConfigLoadStatus.Loaded, store.Load().Status);

        using (new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = store.Load();

            Assert.Equal(ConfigLoadStatus.RecoveredFromCorrupt, result.Status);
            Assert.Equal("Ctrl+Shift+Q", result.Config.Hotkey);
        }

        // 句柄释放后应当又回到正常读取。
        Assert.Equal(ConfigLoadStatus.Loaded, store.Load().Status);
    }
}
