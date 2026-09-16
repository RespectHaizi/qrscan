using System.Diagnostics;
using QrScan.Core.Models;

namespace QrScan.Core;

/// <summary>
/// 唯一的「执行外部动作」实现：把可打开的目标交给系统。
///
/// 安全契约由 <see cref="BuildStartInfo"/> **强制**，不依赖调用方的自觉：
/// 目标必须先通过 <see cref="PayloadClassifier.CanOpen"/>（即白名单
/// <c>http</c>/<c>https</c>/<c>mailto</c>/<c>tel</c>，或"确实存在的本地文件/目录"），
/// 否则直接拒绝，绝不触达 <see cref="Process.Start(ProcessStartInfo)"/>。
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    private const string ExplorerFileName = "explorer.exe";

    public void Open(string target) => Process.Start(BuildStartInfo(target))?.Dispose();

    /// <summary>
    /// 构造启动参数。**纯函数，不启动任何进程**，因此这条安全边界可以被自动化断言。
    ///
    /// 三路分流：
    /// <list type="bullet">
    ///   <item>白名单 URL → 交给系统默认处理程序</item>
    ///   <item>本地目录 → <c>explorer.exe "&lt;目录&gt;"</c>（打开文件夹）</item>
    ///   <item>本地文件 → <c>explorer.exe /select,"&lt;文件&gt;"</c>（定位并选中，不执行）</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">目标为 null / 空白（调用方 bug）。</exception>
    /// <exception cref="InvalidOperationException">目标不在白名单内。</exception>
    internal static ProcessStartInfo BuildStartInfo(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        // 与 PayloadClassifier.Classify 用同一个规范化形式。
        // 分类内部会 Trim，若这里仍用未 trim 的原串，
        // "  C:\some\dir  " 会被判为 FilePath，但 Directory.Exists("  C:\some\dir  ") 为假，
        // 于是落进 /select, 分支（本该打开文件夹），而且空格会被原样带进参数。
        target = target.Trim();

        var kind = PayloadClassifier.Classify(target);

        // 强制契约，而非仅靠接口文档声明。万一将来有人写错调用点，
        // 这里立刻炸，而不是静默把一个自定义 scheme 交给协议处理器执行。
        if (!PayloadClassifier.CanOpen(kind))
            throw new InvalidOperationException($"拒绝打开非白名单目标：{target}");

        if (kind != PayloadKind.FilePath)
            return new ProcessStartInfo(target) { UseShellExecute = true };

        // 本地路径一律经资源管理器，**绝不把路径本身交给 ShellExecute** ——
        // `explorer.exe "<文件>"` 会把参数交给该文件的默认处理程序，`.exe`/`.bat` 会被直接执行。
        // 目录 → 打开文件夹；文件 → 定位并选中，是否运行由用户自己决定。
        //
        // 路径不可能含 `"`：那在 Windows 文件名里是非法字符，会令
        // PayloadClassifier 的 File.Exists/Directory.Exists 返回 false，
        // 目标根本进不到这个分支 —— 因此这里不存在参数注入。
        return Directory.Exists(target)
            ? ExplorerStartInfo($"\"{target}\"")
            : ExplorerStartInfo($"/select,\"{target}\"");
    }

    private static ProcessStartInfo ExplorerStartInfo(string arguments) => new(ExplorerFileName)
    {
        Arguments = arguments,
        UseShellExecute = true,
    };
}
