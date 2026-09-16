using QrScan.Core;

namespace QrScan.Core.Tests;

public class HotkeySpecTests
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    [Fact]
    public void Parses_default_hotkey()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+Shift+Q", out uint mods, out uint vk, out string? error));

        Assert.Null(error);
        Assert.Equal(ModControl | ModShift, mods);
        Assert.Equal(0x51u, vk);                       // 'Q'
    }

    [Theory]
    [InlineData("ctrl+shift+q", 0x51u)]
    [InlineData("CTRL+SHIFT+Q", 0x51u)]
    [InlineData("Ctrl + Shift + Q", 0x51u)]           // 容忍空格
    public void Parsing_is_case_and_space_insensitive(string spec, uint expectedVk)
    {
        Assert.True(HotkeySpec.TryParse(spec, out uint mods, out uint vk, out _));
        Assert.Equal(ModControl | ModShift, mods);
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("Alt+F1", ModAlt, 0x70u)]
    [InlineData("Ctrl+Alt+Delete", ModControl | ModAlt, 0x2Eu)]
    [InlineData("Win+Shift+S", ModWin | ModShift, 0x53u)]
    [InlineData("Ctrl+Shift+1", ModControl | ModShift, 0x31u)]
    [InlineData("Ctrl+Shift+F12", ModControl | ModShift, 0x7Bu)]
    public void Parses_various_modifiers_and_keys(string spec, uint expectedMods, uint expectedVk)
    {
        Assert.True(HotkeySpec.TryParse(spec, out uint mods, out uint vk, out _));
        Assert.Equal(expectedMods, mods);
        Assert.Equal(expectedVk, vk);
    }

    [Fact]
    public void Rejects_hotkey_without_modifier()
    {
        // 无修饰键的全局热键会抢走用户整个键盘上的那个键 —— 必须拒绝。
        Assert.False(HotkeySpec.TryParse("Q", out _, out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("修饰键", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Nosuchkey")]
    [InlineData("Hyper+Q")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Q+W")]
    [InlineData("Ctrl+Ctrl+Q")]
    public void Rejects_malformed_specs(string? spec)
    {
        Assert.False(HotkeySpec.TryParse(spec, out _, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_or_throw_reports_the_offending_spec()
    {
        var ex = Assert.Throws<FormatException>(() => HotkeySpec.Parse("Ctrl+Nosuchkey"));
        Assert.Contains("Ctrl+Nosuchkey", ex.Message);
    }
}
