using System.ComponentModel;
using System.Runtime.InteropServices;
using QrScan.Core;

namespace QrScan.Native;

/// <summary>
/// 全局热键。用一个不可见的 <see cref="NativeWindow"/> 接收 <c>WM_HOTKEY</c>，
/// 把 Win32 消息转成 C# 事件。
///
/// 本类存在的意义是**让注册失败对用户可见**：一旦 <c>RegisterHotKey</c> 失败，
/// 用户按热键将毫无反应；若不给可读原因，只会被当成"这个软件坏了"。
/// 因此失败消息不仅说明可能的原因，还给出 hotkey 的**唯一修改入口的完整路径**
/// （本程序没有设置界面）。
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    /// <summary>任意非零 id，只需在 <c>RegisterHotKey</c> 与 <c>UnregisterHotKey</c> 之间保持一致。</summary>
    private const int HotkeyId = 0xB0B;

    /// <summary><c>ERROR_HOTKEY_ALREADY_REGISTERED</c>。</summary>
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly HotkeyWindow _window;
    private bool _registered;
    private bool _disposed;

    /// <summary>热键被按下。**零参数** —— 订阅处请写 <c>() =&gt;</c>，不是 <c>(_, _) =&gt;</c>。</summary>
    public event Action? Pressed;

    public bool IsRegistered => _registered;

    /// <summary>注册失败时的可读原因；成功时为 null。</summary>
    public string? RegistrationError { get; }

    public HotkeyManager(string spec)
    {
        _window = new HotkeyWindow();
        _window.HotkeyReceived += id =>
        {
            if (id == HotkeyId)
                Pressed?.Invoke();
        };

        if (!HotkeySpec.TryParse(spec, out uint modifiers, out uint virtualKey, out string? parseError))
        {
            // 这个分支最可能的原因就是用户在 config.json 里把 hotkey 写坏了，
            // 所以必须给出文件的完整路径 —— 本程序没有设置界面，那是唯一入口。
            RegistrationError = $"{parseError}请修改配置文件中的 hotkey 后重启 QrScan：{ConfigStore.DefaultPath()}";
            return;
        }

        if (User32.RegisterHotKey(_window.Handle, HotkeyId, modifiers, virtualKey))
        {
            _registered = true;
            return;
        }

        RegistrationError = BuildRegistrationError(spec, Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            // 不注销会在本进程内留下一个仍被占用的热键。
            // （进程退出时 Windows 也会自动释放，但优雅退出这条路径必须自己收干净。）
            User32.UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }

        _window.DestroyHandle();
    }

    /// <summary>
    /// 组装可读的失败原因。**两种可能都要说清**：
    /// 错误码 1409 只说明"这个组合已被注册"，而 <c>Ctrl+Alt+Delete</c> 这类系统保留组合
    /// （由内核截获的安全注意序列）同样会让 <c>RegisterHotKey</c> 失败 —— 它并不是被别的
    /// 程序占用。只说"被占用"会误导用户去逐个关掉别的程序，然后毫无收获地回来。
    /// </summary>
    private static string BuildRegistrationError(string spec, int errorCode)
    {
        string detail = errorCode == ErrorHotkeyAlreadyRegistered
            ? "该组合已被注册"
            : $"注册被系统拒绝（{new Win32Exception(errorCode).Message}）";

        return $"热键 {spec} 无法注册：{detail}。可能已被其它程序占用，也可能是系统保留的组合"
             + "（例如 Ctrl+Alt+Delete 由内核截获，任何程序都无法注册）。"
             + $"请改用其它组合，或修改配置文件中的 hotkey 后重启 QrScan：{ConfigStore.DefaultPath()}";
    }

    /// <summary>只用来接收 <c>WM_HOTKEY</c> 的隐藏窗口：无样式、不可见、不属于任何父窗口。</summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        public event Action<int>? HotkeyReceived;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "QrScan.HotkeyWindow",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = 0,
                ExStyle = 0,
                Parent = IntPtr.Zero,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
                HotkeyReceived?.Invoke(m.WParam.ToInt32());

            base.WndProc(ref m);
        }
    }
}
