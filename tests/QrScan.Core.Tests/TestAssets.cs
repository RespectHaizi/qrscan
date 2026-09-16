using System.Drawing;

namespace QrScan.Core.Tests;

internal static class TestAssets
{
    /// <summary>从内嵌资源加载测试位图。name 形如 "selftest.png"。</summary>
    internal static Bitmap Load(string name)
    {
        var assembly = typeof(TestAssets).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"内嵌资源中找不到 {name}。已加载：{string.Join(", ", assembly.GetManifestResourceNames())}");

        using var stream = assembly.GetManifestResourceStream(resource)!;

        // Bitmap(Stream) 不拥有流的所有权：必须复制一份再让 stream 释放，
        // 否则后续 Clone/Save 会抛 GDI+ 的 OutOfMemoryException。
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }
}
