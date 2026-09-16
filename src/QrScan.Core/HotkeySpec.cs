namespace QrScan.Core;

/// <summary>
/// 热键字符串 → Win32 修饰键掩码 + 虚拟键码。
/// 确定性函数 → static（见规格 §4.1 的规则）。这里不含任何 P/Invoke，因此可单测。
/// </summary>
internal static class HotkeySpec
{
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    /// <summary>VK_DELETE。仅作具名主键支持，不做任何系统保留组合的拦截。</summary>
    private const uint VkDelete = 0x2E;

    private const string SupportedKeys = "A-Z、0-9、F1-F24、Delete";

    internal static bool TryParse(string? spec, out uint modifiers, out uint virtualKey, out string? error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "热键为空。";
            return false;
        }

        string[] parts = spec.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty))
        {
            error = $"热键 \"{spec}\" 含空的 \"+\" 分段。";
            return false;
        }

        if (parts.Length < 2)
        {
            error = $"热键 \"{spec}\" 缺少修饰键。为避免抢走全局按键，必须至少包含 Ctrl / Alt / Shift / Win 之一。";
            return false;
        }

        // 最后一段是主键，前面全部是修饰键
        for (int i = 0; i < parts.Length - 1; i++)
        {
            uint bit = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" or "windows" or "meta" => ModWin,
                _ => 0,
            };

            if (bit == 0)
            {
                error = $"热键 \"{spec}\" 含无法识别的修饰键 \"{parts[i]}\"。可用：Ctrl / Alt / Shift / Win。";
                return false;
            }

            if ((modifiers & bit) != 0)
            {
                error = $"热键 \"{spec}\" 重复了修饰键 \"{parts[i]}\"。";
                return false;
            }

            modifiers |= bit;
        }

        string keyToken = parts[^1];
        if (!TryParseKey(keyToken, out virtualKey))
        {
            error = $"热键 \"{spec}\" 的主键 \"{keyToken}\" 无法识别。支持 {SupportedKeys}。";
            return false;
        }

        return true;
    }

    internal static (uint Modifiers, uint VirtualKey) Parse(string spec)
    {
        if (!TryParse(spec, out uint modifiers, out uint virtualKey, out string? error))
            throw new FormatException(error ?? $"热键 \"{spec}\" 格式无效。");

        return (modifiers, virtualKey);
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);

            // A-Z 与 0-9 的虚拟键码恰好等于其 ASCII 大写值
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }

            return false;
        }

        // 唯一的具名主键：契约要求 Ctrl+Alt+Delete 能被解析。
        // 注意：该组合是系统保留的安全注意序列，RegisterHotKey 会失败 ——
        // 这属于注册层（HotkeyManager）该报出可读原因的事，解析层不做拦截。
        if (token.Equals("Delete", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = VkDelete;
            return true;
        }

        if ((token[0] is 'F' or 'f') && int.TryParse(token.AsSpan(1), out int functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);   // VK_F1 == 0x70
            return true;
        }

        return false;
    }
}
