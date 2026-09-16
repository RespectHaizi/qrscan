using System.Runtime.InteropServices;
using QrScan.Native;

// 占用 Ctrl+Shift+Q 直到用户按回车释放。
//
// 用途：让「热键不可用」这条路径**可重复验证**，而不是靠碰运气去找一个
// 恰好占用该热键的程序。
//
// 与 QrScan 共用同一份 RegisterHotKey 声明（编入真实的 src/QrScan/Native/User32.cs），
// 因此"本工具能占住"与"QrScan 注册失败"必然出自同一个 API 的行为。

const int HotkeyId = 1;
const uint ModControl = 0x0002;
const uint ModShift = 0x0004;
const uint VkQ = 0x51;

const int ErrorHotkeyAlreadyRegistered = 1409;

// GetConsoleWindow 不在 User32.cs 里（QrScan 自己用不到它），此处单独声明，不算重复。
//
// 注意 DLL 名：GetConsoleWindow 由 **kernel32.dll** 导出，不在 user32.dll 里。
// 写成 user32.dll 编译期不会报错，只会在运行时抛 EntryPointNotFoundException —— 
// 这是本工具第一版踩到的坑，实测发现。
[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

IntPtr hwnd = GetConsoleWindow();

if (!User32.RegisterHotKey(hwnd, HotkeyId, ModControl | ModShift, VkQ))
{
    int error = Marshal.GetLastWin32Error();
    Console.WriteLine($"注册 Ctrl+Shift+Q 失败（错误码 {error}）");
    Console.WriteLine(error == ErrorHotkeyAlreadyRegistered
        ? "该热键已被占用（1409）。若 QrScan 正在运行，先退出它再跑本工具。"
        : "原因不是「已被占用」，请检查本次调用的参数。");
    return 1;
}

Console.WriteLine("已占用 Ctrl+Shift+Q。");
Console.WriteLine("现在启动 QrScan，应当看到：");
Console.WriteLine("  · 托盘气泡「热键不可用」，正文含 config.json 的完整路径");
Console.WriteLine("  · 托盘菜单首项为「扫码 (热键不可用)」并置灰");
Console.WriteLine("  · 鼠标悬停该项时 ToolTip 显示完整原因");
Console.WriteLine();
Console.WriteLine("按回车释放并退出。");
Console.ReadLine();

User32.UnregisterHotKey(hwnd, HotkeyId);
Console.WriteLine("已释放 Ctrl+Shift+Q。");
return 0;
