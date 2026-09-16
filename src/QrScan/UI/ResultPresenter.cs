using System.Runtime.InteropServices;
using QrScan.Core;
using QrScan.Core.Models;

namespace QrScan.UI;

/// <summary>
/// 把识别结果呈现给用户：写剪贴板 + 弹提示条。
///
/// 全部业务判断（"这条内容能不能打开"）委托给 <see cref="PayloadClassifier"/> ——
/// 本类不做任何分类，只按分类结果决定渲染哪几个按钮。
/// 它是 <see cref="IShellLauncher.Open"/> 的**唯一调用方**，因此"自定义 scheme
/// 绝不触达启动器"这个安全边界在本类里才成立、也才可断言。
/// </summary>
internal sealed class ResultPresenter
{
    private const int ClipboardRetryAttempts = 3;
    private const int ClipboardRetryDelayMs = 50;

    /// <summary>提示条正文的最大长度；超出则截断并加省略号（完整内容仍在剪贴板里）。</summary>
    private const int MaxMessageLength = 120;

    private const string NoCodeMessage = "未识别到二维码";

    private readonly IShellLauncher _launcher;
    private readonly int _dismissMs;

    private ToastWindow? _active;

    public ResultPresenter(IShellLauncher launcher, int dismissMs)
    {
        _launcher = launcher;
        _dismissMs = dismissMs;
    }

    /// <summary>
    /// 单次写剪贴板的实际动作。默认就是真实剪贴板。
    ///
    /// 探针可以替换它来模拟"剪贴板被其他进程锁住"这个**必现**的失败模式
    /// （Office、远程桌面、剪贴板管理器都会锁剪贴板），从而验证重试与失败提示，
    /// 而不必真的去抢剪贴板。
    ///
    /// 与 <see cref="ToastWindow.PointerPositionSource"/> 同类：internal、仅测试用的接缝，
    /// 生产路径下从不会被赋值。
    /// </summary>
    internal static Action<string> ClipboardWrite { get; set; } =
        static text => Clipboard.SetDataObject(text, copy: true);

    /// <summary>
    /// 呈现识别结果：默认复制第一个码，并在内容可打开时提供「打开」。
    /// 结果已由 <c>QrDecoder</c> 按视觉顺序排好（`Position.TopLeft.X` 再 `Y`）。
    /// </summary>
    public void ShowSuccess(IReadOnlyList<QrPayload> payloads, Point atPhysical)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        if (payloads.Count == 0)
        {
            ShowMessage(NoCodeMessage, ToastKind.Info, atPhysical);
            return;
        }

        if (!TrySetClipboardText(payloads[0].Text))
        {
            ShowCopyFailure(payloads, index: 0, atPhysical);
            return;
        }

        ShowPayloadToast(payloads, index: 0, atPhysical);
    }

    /// <summary>一次性提示（"未识别到二维码"、"无法打开：…" 等），不带动作按钮。</summary>
    public void ShowMessage(string text, ToastKind kind, Point atPhysical)
    {
        Replace(new ToastWindow(text, Array.Empty<ToastAction>(), atPhysical, _dismissMs, kind));
    }

    /// <summary>
    /// 写剪贴板。剪贴板被其他进程锁住是**必现**的失败模式，因此重试 3 次 × 50 ms。
    ///
    /// **绝不静默失败** —— 静默失败会让用户以为复制成功了，然后粘贴出上一次的旧内容，
    /// 这是最难排查的一类 bug。调用方必须对 <c>false</c> 做出可见反应。
    /// </summary>
    internal static bool TrySetClipboardText(string text)
    {
        for (int attempt = 0; attempt < ClipboardRetryAttempts; attempt++)
        {
            try
            {
                ClipboardWrite(text);
                return true;
            }
            catch (ExternalException)
            {
                // 剪贴板正被别的进程占用；最后一次不再等待
                if (attempt < ClipboardRetryAttempts - 1)
                    Thread.Sleep(ClipboardRetryDelayMs);
            }
            catch (ThreadStateException)
            {
                // 非 STA 线程属于编程错误：立刻暴露，不要伪装成"剪贴板被占用"
                throw;
            }
        }

        return false;
    }

    /// <summary>
    /// 按规格的按钮矩阵渲染提示条并接管它。
    ///
    /// | 内容类型 | 按钮 |
    /// |---|---|
    /// | 可打开，仅 1 个码 | `[打开] [复制]` |
    /// | 可打开，多个码 | `[打开] [复制] [还有 N 个 ▸]` |
    /// | 纯文本，多个码 | `[复制] [还有 N 个 ▸]` |
    /// | 纯文本，仅 1 个码 | `[复制]` |
    /// </summary>
    private void ShowPayloadToast(IReadOnlyList<QrPayload> payloads, int index, Point atPhysical)
    {
        QrPayload current = payloads[index];
        PayloadKind kind = PayloadClassifier.Classify(current.Text);

        var actions = new List<ToastAction>();

        // 「打开」：仅当分类器放行（http/https/mailto/tel，或确实存在的本地路径）。
        // 纯文本/WiFi 码/vCard/自定义 scheme 一律**不显示**这个按钮 ——
        // 显示一个点了没反应的按钮比不显示更糟。
        if (PayloadClassifier.CanOpen(kind))
            actions.Add(new ToastAction("open", "打开"));

        // 「复制」：重新复制。剪贴板被别的程序覆盖后，或多码切换后想重新确认。
        actions.Add(new ToastAction("copy", "复制"));

        // 多码切换：N 是**除当前之外**还剩几个
        if (payloads.Count > 1)
            actions.Add(new ToastAction("next", $"还有 {payloads.Count - 1} 个 ▸"));

        string message = current.Text.Length <= MaxMessageLength
            ? current.Text
            : current.Text[..MaxMessageLength] + "…";

        var toast = new ToastWindow(message, actions.ToArray(), atPhysical, _dismissMs, ToastKind.Success);
        toast.ActionClicked += action => OnAction(action, payloads, index, atPhysical);
        Replace(toast);
    }

    private void OnAction(ToastAction action, IReadOnlyList<QrPayload> payloads, int index, Point atPhysical)
    {
        switch (action.Id)
        {
            case "open":
                TryOpen(payloads[index].Text, atPhysical);
                break;

            case "copy":
                if (!TrySetClipboardText(payloads[index].Text))
                    ShowCopyFailure(payloads, index, atPhysical);
                break;

            case "next":
                int next = (index + 1) % payloads.Count;

                // 先复制再渲染：复制失败时走失败提示，**不**把「打开」目标悄悄切过去。
                if (!TrySetClipboardText(payloads[next].Text))
                {
                    ShowCopyFailure(payloads, next, atPhysical);
                    break;
                }

                ShowPayloadToast(payloads, next, atPhysical);
                break;
        }
    }

    private void TryOpen(string text, Point atPhysical)
    {
        try
        {
            // 调用前必须已经过 CanOpen（见 ShowPayloadToast）：
            // ShellLauncher 内部也会强制白名单并抛异常，但那时用户看到的是"点了没反应"。
            _launcher.Open(text);
        }
        catch (Exception ex)
        {
            ShowMessage($"无法打开：{ex.Message}", ToastKind.Failure, atPhysical);
        }
    }

    /// <summary>
    /// 红色失败提示条 + 「重试」。这是"绝不静默失败"这条约束的落点。
    ///
    /// 重试成功后**必须回到正常成功态**（<see cref="ShowPayloadToast"/>），而不是只给一句
    /// 不带任何按钮的「已复制」：那个 URL 此后没有任何 UI 能打开它（托盘「最近 10 条」
    /// 按规格只复制、不打开），多码场景还会丢掉 <c>[还有 N 个 ▸]</c>、再也切不回去。
    ///
    /// <paramref name="index"/> 是**失败的那一个**下标，不是永远 0 —— 由「切换」触发的
    /// 复制失败重试后必须回到原来那一个码，而不是退回第 0 个。
    /// </summary>
    private void ShowCopyFailure(IReadOnlyList<QrPayload> payloads, int index, Point atPhysical)
    {
        var toast = new ToastWindow(
            "复制失败（剪贴板被占用）",
            new[] { new ToastAction("retry", "重试") },
            atPhysical,
            _dismissMs,
            ToastKind.Failure);

        toast.ActionClicked += _ =>
        {
            if (TrySetClipboardText(payloads[index].Text))
                ShowPayloadToast(payloads, index, atPhysical);
            else
                ShowCopyFailure(payloads, index, atPhysical);
        };

        Replace(toast);
    }

    /// <summary>
    /// 用新提示条顶掉旧的：先 <c>Show()</c> 新的，再 <c>Close()</c> 旧的。
    ///
    /// 顺序不可交换（先关旧的会有一瞬间屏幕上什么都没有）。
    /// <c>_active</c> 的清空条件必须是"关掉的**就是**当前那个" —— 无条件清空会让
    /// 已经被顶掉的旧窗体在关闭时把新状态一起擦掉。
    /// </summary>
    private void Replace(ToastWindow toast)
    {
        ToastWindow? previous = _active;
        _active = toast;

        toast.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_active, toast))
                _active = null;
        };

        toast.Show();
        previous?.Close();
    }
}
