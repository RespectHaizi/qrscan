using QrScan.Core.Models;

namespace QrScan.Core;

/// <summary>
/// 把识别出的文本分类，并决定能否「打开」。
/// 确定性函数 → static（见规格 §4.1 的规则）。副作用在 <see cref="IShellLauncher"/>。
/// </summary>
public static class PayloadClassifier
{
    /// <summary>
    /// 「打开」白名单。**刻意收得很窄**：二维码内容来自不可信来源，
    /// ShellExecute 会把任意自定义 scheme 交给系统中注册的协议处理器执行，
    /// 因此这里刻意把白名单收得很窄：只放行明确列出的少数 scheme。
    /// </summary>
    private static readonly string[] OpenableSchemes = { "http", "https", "mailto", "tel" };

    private const int MaxPathProbeLength = 260;

    public static PayloadKind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return PayloadKind.PlainText;

        string trimmed = text.Trim();

        // Windows 绝对路径必须先于 scheme 判定。
        // `C:\...` 里冒号前的 `C` 在 RFC 3986 下是合法的单字母 scheme，
        // 若不先跳过 scheme 分支，任何盘符路径都会被当成"未知 scheme"
        // 而落入 PlainText —— 本地文件/目录将永远无法打开。
        if (!LooksLikeWindowsAbsolutePath(trimmed))
        {
            int colon = trimmed.IndexOf(':');
            if (colon > 0 && IsSchemeToken(trimmed.AsSpan(0, colon)))
            {
                string scheme = trimmed[..colon].ToLowerInvariant();
                return OpenableSchemes.Contains(scheme) ? PayloadKind.Url : PayloadKind.PlainText;
            }
        }

        return IsExistingLocalPath(trimmed) ? PayloadKind.FilePath : PayloadKind.PlainText;
    }

    public static bool CanOpen(PayloadKind kind) => kind is PayloadKind.Url or PayloadKind.FilePath;

    /// <summary>
    /// `<盘符>:\` / `<盘符>:/` 或 UNC 前缀 `\\`。
    /// 只认**绝对**形式（盘符后必须跟分隔符），所以 `c:relative` 不会被当成路径。
    ///
    /// UNC 仍然命中这里，目的只是把它挡在 scheme 分支之外；
    /// **真正阻止它碰磁盘的是 <see cref="IsProbeablePath"/>**（UNC 探测会触发网络访问）。
    /// </summary>
    private static bool LooksLikeWindowsAbsolutePath(string text)
    {
        if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' &&
            (text[2] == '\\' || text[2] == '/'))
            return true;

        return text.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>RFC 3986 的 scheme 语法：字母开头，其后为字母/数字/+/-/.</summary>
    private static bool IsSchemeToken(ReadOnlySpan<char> token)
    {
        if (token.Length == 0 || !char.IsAsciiLetter(token[0]))
            return false;

        foreach (char c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return false;
        }

        return true;
    }

    /// <summary>
    /// 判断某个字符串是否值得做文件系统探测。**纯函数，不碰磁盘。**
    /// 把它抽出来，是为了让"UNC 不被探测"这条安全属性可以被自动化断言，
    /// 而不是只能靠读代码判断"它没碰磁盘"。
    ///
    /// <para>
    /// **UNC 路径（<c>\\</c> 或 <c>//</c>）一律返回 <c>false</c>。**
    /// 探测它会向**二维码指定的主机**发起网络连接 —— 而那个主机由二维码作者决定，
    /// 属于不可信输入。关键在于是"分类"阶段触发而不是"打开"阶段触发：
    /// "打开"阶段 —— 扫到码就发生，不需要用户点任何按钮，而扫码器天然会去扫
    /// 来源不可信的码。
    /// </para>
    ///
    /// <para>
    /// **已知限制**：盘符路径仍会被探测，而盘符可能被映射到网络共享
    /// （<c>net use Z: \\server\share</c>），那种情况下探测仍会发起 SMB 连接。
    /// 要彻底消除需要查 <c>DriveInfo.DriveType</c>，本版按 YAGNI 不做。
    /// </para>
    /// </summary>
    internal static bool IsProbeablePath(string text)
    {
        // 太短的不可能是绝对路径；太长的是纯文本二维码，不值得碰磁盘。
        // 这行同时保证了下面 text[0..2] 的安全访问（能走到后面的，长度 >= 3）。
        if (text.Length is <= 2 or > MaxPathProbeLength)
            return false;

        if (text.Contains('\n') || text.Contains('\r'))
            return false;

        // ★ UNC：绝不探测
        if (text.StartsWith(@"\\", StringComparison.Ordinal) ||
            text.StartsWith("//", StringComparison.Ordinal))
            return false;

        // 只认盘符绝对路径：<盘符>:<分隔符>。`C:relative` 不是绝对路径，不探测。
        return char.IsAsciiLetter(text[0])
            && text[1] == ':'
            && (text[2] == '\\' || text[2] == '/');
    }

    private static bool IsExistingLocalPath(string text)
    {
        // 先过谓词，**再**碰磁盘。顺序反了等于没修 ——
        // 探测这个动作本身就足以产生一次对不可信主机的网络访问。
        if (!IsProbeablePath(text))
            return false;

        try
        {
            return File.Exists(text) || Directory.Exists(text);
        }
        catch (ArgumentException)
        {
            return false;   // 含有非法路径字符
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}
