using System.Diagnostics;
using Timer = System.Windows.Forms.Timer;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using QrScan.UI;

namespace ToastWindowProbe;

/// <summary>
/// <see cref="ToastWindow"/> 的常驻回归探针。
///
/// **为什么需要它**：提示条有两条无法用普通单元测试覆盖、又极容易静默失效的不变量 ——
///
/// 1. **绝不抢焦点**（`WS_EX_NOACTIVATE` + `ShowWithoutActivation`）。它一旦失效，
///    用户正在输入的窗口就会丢焦点；而这条路径的失败**没有任何可见症状**。
/// 2. **悬停暂停淡出**（`if (_hovering) return;`）。它一旦失效，用户正要点的
///    「打开」按钮会在指尖下消失，而"点不到"很容易被误当成手慢。
///
/// 还有一条更基础的：**淡出 Timer 到底有没有在跑**。`System.Windows.Forms.Timer`
/// 依赖消息泵，而在探针里手动驱动窗体时消息泵正是最容易漏掉的东西 —— 若 Timer 从不
/// tick，那么"悬停时没被关闭"就**恒为真**，测试会全绿却什么都没验证。所以本探针把
/// 「Timer 确实 tick 过」单独断言出来（§4），并配一组变异检查（§9）自证有鉴别力。
///
/// **手法**：csproj 用 <c>&lt;Compile Include&gt;</c> 把仓库里**真实的那份**
/// `ToastWindow.cs` 编进本程序集（不是副本 —— 副本会漂移），于是 `internal` 的窗体
/// 在这里可见，且不需要给仓库加 `InternalsVisibleTo`。
/// 再用反射直接驱动 `OnMouseEnter/OnMouseLeave/OnMouseMove/OnMouseDown/OnPaint/OnShown`，
/// 用 <see cref="Application.DoEvents"/> 驱动**真实的消息泵与真实的 Timer**，
/// 因此**不需要可见桌面**，锁屏或无人值守环境下同样可跑。
///
/// 任一项失败时进程退出码为 1，可直接被脚本/CI 当检查用。
/// </summary>
internal static class Program
{
    private const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>远小于泵消息时长，使"应当被关闭"在几百毫秒内可观察到。</summary>
    private const int ShortDismissMs = 250;

    /// <summary>
    /// 断言总数的预期值。**任何一次增删断言都必须同步这个数字。**
    ///
    /// 为什么需要它：退出码只由 `_fail` 决定，而 `_pass` 只被打印 —— 于是
    /// "删掉某一整节的 `Section(...)` 调用"会让那一节的证据**静默消失**，
    /// 剩余项全绿、退出码仍是 0。有了这道闸门，"删节 / 删断言"会直接变成退出码 1。
    /// </summary>
    private const int ExpectedAssertions = 85;

    /// <summary>每次泵消息的时长：约为 dismissMs 的 4 倍，足够让 Timer 跑出多次 tick。</summary>
    private const int PumpMs = 1000;

    /// <summary>判定"亮像素"（即白色文字）的通道阈值。背景/边框/按钮的混合值实测最高约 154。</summary>
    private const int LightThreshold = 220;

    private static int _pass;
    private static int _fail;

    /// <summary>
    /// 实际执行过的断言总数。行 1 个计数目的：让"断言被删"与"断言失败"可区分 ——
    /// 拿 `_pass` 去比 ExpectedAssertions 会把任何一次真实失败都误报成"总数不符"。
    /// </summary>
    private static int _assertions;

    /// <summary>逐节的实际断言产出，汇总时打印。</summary>
    private static readonly List<(string Title, int Count)> SectionCounts = new();

    /// <summary>§8 断言那一刻的 <see cref="InvokeErrors"/> 计数，用于捕获它之后新增的异常。</summary>
    private static int _invokeErrorsAtSection8 = -1;
    private static readonly List<string> Lines = new();
    private static readonly List<string> Notes = new();
    private static readonly List<string> InvokeErrors = new();

    private static ToastAction? _lastAction;
    private static int _actionCount;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("===== ToastWindowProbe：ToastWindow 常驻回归探针 =====");
        Console.WriteLine($"虚拟桌面 = {Show(SystemInformation.VirtualScreen)}");
        Console.WriteLine($"淡出参数：dismissMs = {ShortDismissMs}，每次泵消息 = {PumpMs}ms");
        Console.WriteLine("消息泵：Application.DoEvents()（真实 Timer、真实 WM_TIMER）");
        Console.WriteLine();

        Section("1. 构造期属性", CheckConstructor);
        Section("2. 不抢焦点的两道保险", CheckNoActivate);
        Section("3. 位置 clamp 与指针避让（含屏幕外构造点）", CheckClamp);
        Section("4. 悬停暂停淡出（含消息泵有效性证明）", CheckHoverPause);
        Section("5. 按钮命中测试", CheckButtonHitTesting);
        Section("6. 文字确实被绘制", CheckTextPainted);
        Section("7. 关闭与资源释放", CheckCloseAndResources);
        Section("8. 异常即失败", CheckNoExceptions);
        Section("9. 变异灵敏度：错误实现必须被检出", CheckMutationSensitivity);
        Section("10. 资源与焦点观察（报告项，不断言）", ObserveResourcesAndFocus);

        // ── 闸门一：断言总数。不计入 _pass（否则它会把自己算进去、变成自指）。
        // "删掉一整节"是本探针最危险的自欺形式：它的表现是完全全绿。
        // 比的是 _assertions 而不是 _pass —— 后者会把任何一次真实失败都误报成"总数不符"。
        if (_assertions != ExpectedAssertions)
        {
            _fail++;
            Lines.Add("  [FAIL] 断言总数 == ExpectedAssertions（防「整节被删」而仍然全绿）");
            Lines.Add($"         期望 = {ExpectedAssertions}");
            Lines.Add($"         实测 = {_assertions}（通过 {_pass} / 失败 {_fail}）");
        }

        // ── 闸门二：§8 之后新增的反射异常。§9 / §10 也会发起反射调用，
        // 它们的异常必须同样让退出码变红 —— 否则会逃过断言、且不会被打印出来。
        if (InvokeErrors.Count > _invokeErrorsAtSection8)
        {
            _fail++;
            Lines.Add("  [FAIL] §8 之后新增的反射异常（§9 / §10）");
            Lines.Add("         期望 = 0 个异常");
            Lines.Add($"         实测 = {InvokeErrors.Count} 个：{string.Join(" | ", InvokeErrors)}");
        }

        if (Notes.Count > 0)
        {
            Console.WriteLine("----- 报告项（不断言，仅供参考）-----");
            foreach (string n in Notes) Console.WriteLine("  " + n);
            Console.WriteLine();
        }

        Console.WriteLine($"===== 结果：{_pass} 通过 / {_fail} 失败（共 {_assertions} 项断言）=====");
        Console.WriteLine("  分节断言数（合计应与上面的总数相等）：");
        foreach ((string title, int count) in SectionCounts)
            Console.WriteLine($"    {count,3}  {title}");
        Console.WriteLine();
        foreach (string l in Lines) Console.WriteLine(l);
        Console.WriteLine(_fail == 0 ? "退出码 0（全绿）" : $"退出码 1（{_fail} 项失败）");
        return _fail == 0 ? 0 : 1;
    }

    // ───────────────────────── 1. 构造期属性 ─────────────────────────

    private static void CheckConstructor()
    {
        using (var w = NewToast(ShortDismissMs, TwoActions()))
        {
            Check("FormBorderStyle == None", w.FormBorderStyle == FormBorderStyle.None, "None", w.FormBorderStyle.ToString());
            Check("TopMost == true", w.TopMost, "True", w.TopMost.ToString());
            Check("ShowInTaskbar == false", !w.ShowInTaskbar, "False", w.ShowInTaskbar.ToString());
            Check("AutoScaleMode == None", w.AutoScaleMode == AutoScaleMode.None, "None", w.AutoScaleMode.ToString());
            Check("DoubleBuffered == true", ReadProtectedBool(w, "DoubleBuffered"), "True", ReadProtectedBool(w, "DoubleBuffered").ToString());
            Check("StartPosition == Manual", w.StartPosition == FormStartPosition.Manual, "Manual", w.StartPosition.ToString());
            Check("Message 属性回显构造参数", w.Message == MessageText, MessageText, w.Message);
            Check("Kind 属性回显构造参数", w.Kind == ToastKind.Success, "Success", w.Kind.ToString());
            Check("_actions 数量 == 传入动作数", (ReadActions(w)?.Length ?? -1) == 2, "2", (ReadActions(w)?.Length ?? -1).ToString());
            Check("_buttonRects 数量 == 动作数（构造期已按实际版面算好）",
                ReadButtonRects(w).Length == 2, "2", ReadButtonRects(w).Length.ToString());
            Check("Size 为正", w.Width > 0 && w.Height > 0, "> 0", $"{w.Width}×{w.Height}");
            Check("_fadeTimer.Interval == 50", ReadTimer(w).Interval == 50, "50", ReadTimer(w).Interval.ToString());
            Check("初始 _hoveredButton == -1", ReadField<int>(w, "_hoveredButton") == -1, "-1", ReadField<int>(w, "_hoveredButton").ToString());
            Check("初始 _hovering == false", !ReadField<bool>(w, "_hovering"), "False", ReadField<bool>(w, "_hovering").ToString());
        }

        // 无按钮时不应留出按钮区高度
        using (var withButtons = NewToast(ShortDismissMs, TwoActions()))
        using (var noButtons = NewToast(ShortDismissMs))
        {
            Check("带按钮的提示条比不带按钮的高",
                withButtons.Height > noButtons.Height,
                $"> {noButtons.Height}", $"{withButtons.Height}");
            Check("不带按钮时 _buttonRects 为空", ReadButtonRects(noButtons).Length == 0, "0", ReadButtonRects(noButtons).Length.ToString());
        }

        // 三态背景色
        CheckBackColor(ToastKind.Success, Color.FromArgb(32, 32, 32));
        CheckBackColor(ToastKind.Failure, Color.FromArgb(200, 40, 40));
        CheckBackColor(ToastKind.Info, Color.FromArgb(48, 48, 48));
    }

    private static void CheckBackColor(ToastKind kind, Color expected)
    {
        using var w = NewToast(ShortDismissMs, kind: kind);
        Check($"ToastKind.{kind} → BackColor == {expected}", w.BackColor == expected, expected.ToString(), w.BackColor.ToString());
    }

    // ───────────────────── 2. 不抢焦点的两道保险 ─────────────────────

    private static void CheckNoActivate()
    {
        using var w = NewToast(ShortDismissMs);

        // 第一道：ShowWithoutActivation（反射取 protected 覆写属性）
        PropertyInfo? swa = FindNonPublicProperty(typeof(ToastWindow), "ShowWithoutActivation");
        bool swaValue = false;
        try { swaValue = swa is not null && (bool)(swa.GetValue(w) ?? false); }
        catch (TargetInvocationException ex) { InvokeErrors.Add("ShowWithoutActivation: " + (ex.InnerException ?? ex).Message); }

        Check("ShowWithoutActivation == true（第一道保险）", swaValue, "True", swaValue.ToString());

        // 第二道：CreateParams.ExStyle
        int exStyle = ReadExStyle(w);

        Check("CreateParams.ExStyle 含 WS_EX_NOACTIVATE (0x08000000)",
            (exStyle & WS_EX_NOACTIVATE) != 0, "非 0", $"0x{exStyle:X8}");
        Check("CreateParams.ExStyle 含 WS_EX_TOOLWINDOW (0x00000080) → 不进 Alt+Tab",
            (exStyle & WS_EX_TOOLWINDOW) != 0, "非 0", $"0x{exStyle:X8}");

        // 句柄真的建出来之后，ExStyle 应当仍是我们的值（而不是被 base 覆盖回去）
        _ = w.Handle;
        Check("创建句柄后 ExStyle 仍含两位（未被 base 覆盖）",
            (ReadExStyle(w) & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) == (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW),
            $"含 0x{WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW:X8}", $"0x{ReadExStyle(w):X8}");

        // CreateParams 只能证明"我们要了什么"，不能证明"OS 真的拿到了什么"。
        // 直接查已创建窗口的原生 ExStyle，才能封死"CreateParams 算了但没生效"这个口子。
        int nativeEx = GetWindowLongPtr64(w.Handle, GWL_EXSTYLE).ToInt32();
        Check("已创建窗口的原生 ExStyle 真的含 WS_EX_NOACTIVATE",
            (nativeEx & WS_EX_NOACTIVATE) != 0, "非 0", $"0x{nativeEx:X8}");
        Check("已创建窗口的原生 ExStyle 真的含 WS_EX_TOOLWINDOW",
            (nativeEx & WS_EX_TOOLWINDOW) != 0, "非 0", $"0x{nativeEx:X8}");

        // 原生值**不**等于 CreateParams：框架会为 TopMost 这类顶层属性额外补位。
        // 判据是子集关系（我们的位一个不能少），不是相等。
        //
        // 注："多出的位恰好只有 WS_EX_TOPMOST" **不做断言** —— 实测该位由 WinForms 在
        // 建句柄后经 SetWindowPos 补上，紧跟 `_ = w.Handle` 去读会偶发读不到（本来就是
        // 非需求项，不能让它把探针弄成间歇性红）。改为报告项。
        int extraBits = nativeEx & ~exStyle;
        Check("原生 ExStyle ⊇ CreateParams（我们的位一个不少）",
            (nativeEx & exStyle) == exStyle, $"⊇ 0x{exStyle:X8}", $"0x{nativeEx:X8}");

        Notes.Add($"原生 ExStyle = 0x{nativeEx:X8}，CreateParams = 0x{exStyle:X8}，"
                + $"多出 0x{extraBits:X8}"
                + (extraBits == WS_EX_TOPMOST ? "（= WS_EX_TOPMOST，来自 TopMost = true；由框架建句柄后补上）" : ""));
    }

    // ─────────────────── 3. 位置 clamp（含屏幕外构造点） ───────────────────

    private static void CheckClamp()
    {
        Rectangle vs = SystemInformation.VirtualScreen;

        using (var w = NewToast(ShortDismissMs, at: new Point(99999, 99999)))
        {
            w.Show();
            Rectangle b = NativeBounds(w);
            Check("构造点 (99999,99999)：Show() 后完全落在虚拟桌面内",
                vs.Contains(b), $"⊂ {Show(vs)}", Show(b));
            Check("  └ 右下边界未越界",
                b.Right <= vs.Right && b.Bottom <= vs.Bottom,
                $"Right<={vs.Right} 且 Bottom<={vs.Bottom}", $"Right={b.Right} Bottom={b.Bottom}");
            w.Close();
        }

        using (var w = NewToast(ShortDismissMs, at: new Point(-99999, -99999)))
        {
            w.Show();
            Rectangle b = NativeBounds(w);
            Check("构造点 (-99999,-99999)：Show() 后完全落在虚拟桌面内",
                vs.Contains(b), $"⊂ {Show(vs)}", Show(b));
            Check("  └ 左上边界未越界",
                b.Left >= vs.Left && b.Top >= vs.Top,
                $"Left>={vs.Left} 且 Top>={vs.Top}", $"Left={b.Left} Top={b.Top}");
            w.Close();
        }

        // 这一块断言的是“锚点被**原样**保留”，而 PlaceAwayFromPointer 会在提示条矩形
        // 含指针时把它挪开 —— 用真实 Cursor.Position 的话，只要跑探针时鼠标恰好落在
        // 那个位置（右上角 10,10 附近），这里就会**假红**（而产品行为是正确的）。
        // 所以临时把指针接缝钉到“远离锚点”的点上；run-mutations.sh 里没有覆盖它，
        // 因此不能依赖 CheckPointerAvoidance 后面才注入接缝。
        Func<Point> savedPointer = ToastWindow.PointerPositionSource;
        try
        {
            ToastWindow.PointerPositionSource = () => new Point(vs.Right - 1, vs.Bottom - 1);

            var anchor = new Point(vs.Left + 10, vs.Top + 10);
            using var w = NewToast(ShortDismissMs, at: anchor);
            w.Show();
            Check("合法锚点被原样保留（未被无谓挪动）",
                NativeBounds(w).Location == anchor, Show(anchor), Show(NativeBounds(w).Location));
            w.Close();
        }
        finally
        {
            ToastWindow.PointerPositionSource = savedPointer;
        }

        CheckClampAgainstUnderestimatedSize(vs);
        CheckPointerAvoidance(vs);
    }

    /// <summary>
    /// 构造期的 clamp 只用 <c>(360, 120)</c> 这个**尺寸上界估计**，而实际尺寸只有在正文
    /// 很长时才会超过它。若不去构造那种情况，<c>OnShown</c> 里的二次 clamp 就是**死代码** ——
    /// 把它删掉也测不出来（实测：短文本的提示条只有 126×43 与 158×77，远小于估计值）。
    /// 所以这里必须把实际尺寸顶过估计值，才能真的盖到那条路径。
    /// </summary>
    private static void CheckClampAgainstUnderestimatedSize(Rectangle vs)
    {
        // 两种“超过估计值”的形态都要盖：
        //  宽：一个 300 字符的不可断长词把宽度顶到正文宽度上限（340）→ 368 > 360；
        //  高：大量可换行文字把高度顶过 120（高度根本没有上界）。
        var scenarios = new (string Name, string Message)[]
        {
            ("宽", "长文本：" + new string('M', 300)),
            ("高", string.Join(" ", Enumerable.Repeat("这是一个会换行的长句子用来把提示条撑得很高", 10))),
        };

        foreach ((string name, string message) in scenarios)
        {
            using var w = new ToastWindow(message, TwoActions(),
                                          new Point(99999, 99999), ShortDismissMs, ToastKind.Success);

            Check($"长文本提示条（{name}）的实际尺寸确实超过构造期用的 (360,120)（否则 clamp 测不到）",
                w.Width > 360 || w.Height > 120,
                "W > 360 或 H > 120",
                $"{w.Width}×{w.Height}");

            w.Show();

            // 关键：读**原生窗口矩形**，不读 Control.Bounds。
            // 实测 Show() 返回后 Control.Location 可能是陈旧缓存，拿它判定会得到假结果。
            Rectangle native = NativeBounds(w);
            Rectangle managed = w.Bounds;

            Notes.Add($"clamp（{name} {w.Width}×{w.Height}）：Show() 后原生矩形 = {Show(native)}，"
                    + $"Control.Bounds = {Show(managed)}"
                    + (native != managed ? "（Control.Bounds 是陈旧缓存；判定以原生矩形为准）" : ""));

            Check($"长文本（{name}）+ 屏幕外构造点：原生矩形完全落在虚拟桌面内",
                vs.Contains(native), $"⊂ {Show(vs)}", Show(native));
            Check($"  └ 右边界未越界（{name}）", native.Right <= vs.Right, $"<= {vs.Right}", native.Right.ToString());
            Check($"  └ 下边界未越界（{name}）", native.Bottom <= vs.Bottom, $"<= {vs.Bottom}", native.Bottom.ToString());

            w.Close();
        }
    }

    /// <summary>
    /// 提示条**绝不落在指针底下**。
    ///
    /// 为什么这条重要：调用方（`ResultPresenter`）把锚点设为选区的右下角，而那里正是用户
    /// 松开鼠标的位置（向右下拖拽时）。若提示条左上角恰好落在指针之下，`OnMouseEnter` 会
    /// 立刻触发、`_hovering` 变 true，于是淡出被**无限暂停** —— 提示条永远不消失，与
    /// 「2 秒后自动淡出」的设计相悖。而且它**没有任何报错**：看起来只是「提示条赖着不走」。
    ///
    /// 探针无法真的移动鼠标（<see cref="Cursor.Position"/> 只读），所以通过
    /// <see cref="ToastWindow.PointerPositionSource"/> 这个 internal 接缝注入固定指针位置。
    /// </summary>
    private static void CheckPointerAvoidance(Rectangle vs)
    {
        // 三个场景分别打中三条避让路径：
        //   屏幕中部 → 原位含指针，靠「指针上方」解决；
        //   贴上边缘 → 上方放不下（会被 clamp 回顶部），靠「指针左侧」解决；
        //   贴左上角 → 上方与左侧都不行，靠「指针右侧」解决。
        var scenarios = new (string Name, Point Pointer)[]
        {
            ("屏幕中部", new Point(vs.Left + 600, vs.Top + 400)),
            ("贴上边缘", new Point(vs.Left + 600, vs.Top + 5)),
            ("贴左上角", new Point(vs.Left + 5, vs.Top + 5)),
        };

        Func<Point> saved = ToastWindow.PointerPositionSource;

        try
        {
            foreach ((string name, Point pointer) in scenarios)
            {
                ToastWindow.PointerPositionSource = () => pointer;

                // 锚点就设在指针处 —— 这正是调用方传选区右下角时的几何关系。
                using var w = new ToastWindow(MessageText, TwoActions(), pointer, ShortDismissMs, ToastKind.Success);
                w.Show();
                Rectangle b = NativeBounds(w);

                Check($"指针避让（{name}）：提示条不覆盖指针",
                    !b.Contains(pointer),
                    $"不含 {Show(pointer)}",
                    b.Contains(pointer) ? $"覆盖了指针（矩形 {Show(b)}）" : $"不含（矩形 {Show(b)}）");

                Check($"指针避让（{name}）：避让后仍完整落在虚拟桌面内",
                    vs.Contains(b), $"⊂ {Show(vs)}", Show(b));

                w.Close();
            }

            // 回归保护：避让不能退化成「总是移位」—— 指针远离时锚点必须原样保留。
            var farPointer = new Point(vs.Left + 1500, vs.Top + 900);
            var anchor = new Point(vs.Left + 20, vs.Top + 20);
            ToastWindow.PointerPositionSource = () => farPointer;

            using (var w = new ToastWindow(MessageText, TwoActions(), anchor, ShortDismissMs, ToastKind.Success))
            {
                w.Show();
                Check("指针避让：指针远离锚点时，锚点被原样保留（不无谓挪动）",
                    NativeBounds(w).Location == anchor, Show(anchor), Show(NativeBounds(w).Location));
                w.Close();
            }
        }
        finally
        {
            ToastWindow.PointerPositionSource = saved;
        }
    }

    // ─────────────── 4. 悬停暂停淡出（含消息泵有效性证明） ───────────────

    /// <summary>
    /// 这一节是整个探针的重点。分三层证据：
    /// (a) 控制组：不悬停 → 必须被关闭，**且 Timer 确实 tick 过** ⇒ 消息泵与 Timer 真的在工作；
    /// (b) 悬停组：同一个时间窗内**未**被关闭，**且 Timer 同样 tick 过** ⇒ "没关"是被 _hovering 挡住的；
    /// (c) 直接调用私有的 OnFadeTick（绕开消息泵）在悬停中仍不关闭 ⇒ 直击守卫本身。
    /// 缺了 (a) 的 tick 计数，(b) 的"没关"就恒为真、毫无鉴别力。
    /// </summary>
    private static void CheckHoverPause()
    {
        int ticksNoHover;
        int ticksHover;

        // (a) 控制组
        using (var w = NewToast(ShortDismissMs))
        {
            Func<int> tickCount = AttachTickCounter(w);
            w.Show();
            PumpFor(PumpMs);
            ticksNoHover = tickCount();

            Check("(a) 控制组（不悬停）：超过 dismissMs 后确实被自动关闭",
                IsGone(w), "已关闭", w.IsDisposed ? "已关闭" : "仍打开");
            Check("(a) 控制组：淡出 Timer 确实 tick 过 → 消息泵 + Timer + Close 路径真的通",
                ticksNoHover > 0, "> 0", $"{ticksNoHover} 次");
        }

        // (b) 悬停组
        using (var w = NewToast(ShortDismissMs))
        {
            Func<int> tickCount = AttachTickCounter(w);
            w.Show();

            Invoke(w, "OnMouseEnter", new[] { typeof(EventArgs) }, new object[] { EventArgs.Empty });
            Check("(b) OnMouseEnter 后 _hovering == true",
                ReadField<bool>(w, "_hovering"), "True", ReadField<bool>(w, "_hovering").ToString());

            PumpFor(PumpMs);
            ticksHover = tickCount();

            Check("(b) 悬停组：同一时间窗内仍未被关闭",
                !w.IsDisposed, "仍打开", w.IsDisposed ? "已关闭" : "仍打开");
            Check("(b) 悬停组：淡出 Timer 同样 tick 过 → \"没关\"是被 _hovering 挡住的，不是因为计时器没跑",
                ticksHover > 0, "> 0", $"{ticksHover} 次");
            Check("(b) 悬停组的 tick 次数不少于控制组（同一时间窗，说明它一直在跑）",
                ticksHover >= ticksNoHover, $">= {ticksNoHover}", $"{ticksHover} 次");

            // (c) 直击守卫本身：绕开消息泵，直接调被验证源文件里的私有 OnFadeTick
            Invoke(w, "OnFadeTick", new[] { typeof(object), typeof(EventArgs) }, new object?[] { w, EventArgs.Empty });
            Check("(c) 悬停中直接调用 OnFadeTick（绕开消息泵）仍不关闭 → 守卫本身有效",
                !w.IsDisposed, "仍打开", w.IsDisposed ? "已关闭" : "仍打开");

            // 移开 → 应当恢复淡出
            Invoke(w, "OnMouseLeave", new[] { typeof(EventArgs) }, new object[] { EventArgs.Empty });
            Check("(d) OnMouseLeave 后 _hovering == false",
                !ReadField<bool>(w, "_hovering"), "False", ReadField<bool>(w, "_hovering").ToString());

            PumpFor(PumpMs);
            Check("(d) 移开鼠标后应当被自动关闭", IsGone(w), "已关闭", w.IsDisposed ? "已关闭" : "仍打开");
        }
    }

    // ───────────────────── 5. 按钮命中测试 ─────────────────────

    private static void CheckButtonHitTesting()
    {
        ToastAction[] actions = TwoActions();

        using var w = NewToast(ShortDismissMs, actions);
        Rectangle[] rects = ReadButtonRects(w);

        Check("两个按钮矩形互不重叠", rects.Length == 2 && !rects[0].IntersectsWith(rects[1]),
            "不重叠", rects.Length == 2 ? (rects[0].IntersectsWith(rects[1]) ? "重叠" : "不重叠") : $"只有 {rects.Length} 个");
        Check("按钮矩形都在客户区内",
            rects.All(r => r.Left >= 0 && r.Top >= 0 && r.Right <= w.Width && r.Bottom <= w.Height),
            $"⊂ (0,0,{w.Width},{w.Height})", string.Join(" ", rects.Select(Show)));

        w.Show();

        // 悬停到按钮 0
        Invoke(w, "OnMouseMove", new[] { typeof(MouseEventArgs) }, new object[] { MouseArgs(Center(rects[0])) });
        Check("移到按钮 0 中心 → _hoveredButton == 0",
            ReadField<int>(w, "_hoveredButton") == 0, "0", ReadField<int>(w, "_hoveredButton").ToString());
        Check("移到按钮上 → 光标变 Hand", w.Cursor == Cursors.Hand, "Hand", w.Cursor.ToString());

        // 悬停到按钮 1
        Invoke(w, "OnMouseMove", new[] { typeof(MouseEventArgs) }, new object[] { MouseArgs(Center(rects[1])) });
        Check("移到按钮 1 中心 → _hoveredButton == 1",
            ReadField<int>(w, "_hoveredButton") == 1, "1", ReadField<int>(w, "_hoveredButton").ToString());

        // 移到按钮外
        var outside = new Point(w.Width - 3, 3);
        Invoke(w, "OnMouseMove", new[] { typeof(MouseEventArgs) }, new object[] { MouseArgs(outside) });
        Check("移到按钮外 → _hoveredButton == -1",
            ReadField<int>(w, "_hoveredButton") == -1, "-1", ReadField<int>(w, "_hoveredButton").ToString());
        Check("移到按钮外 → 光标恢复 Default", w.Cursor == Cursors.Default, "Default", w.Cursor.ToString());

        // 点在按钮外：既不触发动作，也不关闭
        _lastAction = null;
        _actionCount = 0;
        Invoke(w, "OnMouseDown", new[] { typeof(MouseEventArgs) }, new object[] { MouseArgs(outside) });
        Check("点在按钮外 → 不触发 ActionClicked", _lastAction is null, "null", _lastAction?.Id ?? "null");
        Check("点在按钮外 → 窗体不关闭", !w.IsDisposed, "仍打开", w.IsDisposed ? "已关闭" : "仍打开");

        // 点在按钮 1：触发正确动作并关闭
        Invoke(w, "OnMouseDown", new[] { typeof(MouseEventArgs) }, new object[] { MouseArgs(Center(rects[1])) });
        Check("点在按钮 1 → ActionClicked 收到 Id == \"copy\"", _lastAction?.Id == "copy", "copy", _lastAction?.Id ?? "null");
        Check("点在按钮 1 → 收到的是按钮 1 的 Label", _lastAction?.Label == actions[1].Label, actions[1].Label, _lastAction?.Label ?? "null");
        Check("点在按钮 1 → 只触发一次（无重复派发）", _actionCount == 1, "1", _actionCount.ToString());
        Check("点在按钮 1 → 窗体随后关闭", IsGone(w), "已关闭", w.IsDisposed ? "已关闭" : "仍打开");

        // 右键点在按钮上：不得触发动作、也不得关闭。
        // 若 OnMouseDown 不区分鼠标键，右键会意外执行「打开」—— 而右键在提示条上的
        // 直觉语义是「取消/关闭」，一个误触「打开」比没反应更糟。
        using (var right = NewToast(ShortDismissMs, actions))
        {
            Rectangle[] rightRects = ReadButtonRects(right);
            right.Show();

            _lastAction = null;
            _actionCount = 0;
            Invoke(right, "OnMouseDown", new[] { typeof(MouseEventArgs) },
                   new object[] { MouseArgs(Center(rightRects[0]), MouseButtons.Right) });

            Check("右键点在按钮上 → 不触发 ActionClicked",
                _lastAction is null, "null", _lastAction?.Id ?? "null");
            Check("右键点在按钮上 → 窗体不关闭",
                !right.IsDisposed, "仍打开", right.IsDisposed ? "已关闭" : "仍打开");
        }
    }

    // ───────────────────── 6. 文字确实被绘制 ─────────────────────

    /// <summary>
    /// 本节必须能区分"正文被画了"与"只有按钮标签被画了"。
    /// 第一版的写法（整幅扫亮像素）**抓不到 "删掉正文" 这个变异** —— 按钮标签自己
    /// 就贡献了 262 个亮像素。所以现在分带扫描 + 用无按钮的提示条单独验证正文。
    /// </summary>
    private static void CheckTextPainted()
    {
        // (6a) 无按钮的提示条：亮像素只可能来自 Message 正文
        using (var w = NewToast(ShortDismissMs))
        {
            _ = w.Handle;
            using Bitmap real = Render(w, null);
            int light = CountLightPixels(real, null);

            Check("(6a) 无按钮的提示条渲染出亮像素 ⇒ 正文 Message 确实被绘制",
                light > 0, "> 0", $"{light} 个亮像素");
            Notes.Add($"(6a) 无按钮提示条 {w.Width}×{w.Height}：亮像素 = {light}");
        }

        using (var w = NewToast(ShortDismissMs, TwoActions()))
        {
            _ = w.Handle;
            Rectangle[] rects = ReadButtonRects(w);
            var messageBand = new Rectangle(0, 0, w.Width, rects.Length > 0 ? rects[0].Top : w.Height);
            var buttonBand = new Rectangle(0, rects[0].Top, w.Width, w.Height - rects[0].Top);

            using Bitmap real = Render(w, null);
            int messageLight = CountLightPixels(real, messageBand);
            int labelLight = CountLightPixels(real, buttonBand);

            // (6b) 有按钮时，只扫正文所在横带（按钮行以上）→ 排除按钮标签的干扰
            Check("(6b) 有按钮时，正文横带（按钮行以上）仍有亮像素 ⇒ 正文未被按钮标签顶替",
                messageLight > 0, "> 0", $"{messageLight} 个亮像素（扫描区 {Show(messageBand)}）");
            Check("(6d) 按钮行内有亮像素 ⇒ 按钮标签确实被绘制",
                labelLight > 0, "> 0", $"{labelLight} 个亮像素（扫描区 {Show(buttonBand)}）");

            Notes.Add($"(6b/6d) 有按钮提示条 {w.Width}×{w.Height}：正文带亮像素 = {messageLight}，按钮带 = {labelLight}");

            // (6c) 变异：不画正文但照画按钮标签 → 正文横带必须为 0
            using Bitmap mutated = Render(w, PaintWithoutMessageText);
            int mutatedLight = CountLightPixels(mutated, messageBand);
            Check("(6c) 变异（照画边框/按钮、不画正文）⇒ 正文横带亮像素 == 0 ⇒ (6a)(6b) 有灵敏度",
                mutatedLight == 0, "0", $"{mutatedLight} 个亮像素");
        }

        // (6e)(6f) NoPrefix：`&` 不得被当成助记符前缀吃掉。
        // 单个 "&" 是最锋利的探针：不带 NoPrefix 时它是助记符前缀、后面又没有字符可加
        // 下划线，于是**什么都画不出来**；带 NoPrefix 时它是一个正常字形。
        // 真实影响：正文里经常是 URL 与路径，`https://x.com/a?b=1&c=2` 会显示成 `a?b=1c=2`。
        using (var w = NewToast(ShortDismissMs, message: "&"))
        {
            _ = w.Handle;
            using Bitmap bmp = Render(w, null);
            int light = CountLightPixels(bmp, null);
            Check("(6e) NoPrefix：正文里的 & 被原样绘制（未被当成助记符吞掉）",
                light > 0, "> 0", $"{light} 个亮像素");
        }

        using (var w = NewToast(ShortDismissMs, new[] { new ToastAction("only", "&") }))
        {
            _ = w.Handle;
            Rectangle[] rects = ReadButtonRects(w);
            using Bitmap bmp = Render(w, null);
            int light = rects.Length > 0 ? CountLightPixels(bmp, rects[0]) : -1;
            Check("(6f) NoPrefix：按钮标签里的 & 被原样绘制（未被当成助记符吞掉）",
                light > 0, "> 0", $"{light} 个亮像素（扫描区 {(rects.Length > 0 ? Show(rects[0]) : "无按钮")}）");
        }
    }

    // ───────────────────── 7. 关闭与资源释放 ─────────────────────

    private static void CheckCloseAndResources()
    {
        // 正常路径：Show() 之后 Close()
        using (var w = NewToast(ShortDismissMs))
        {
            w.Show();
            w.Close();
            Check("Show() 后 Close()：窗体已释放", w.IsDisposed, "IsDisposed = True", w.IsDisposed.ToString());
            Check("Show() 后 Close()：淡出 Timer 已停止（Enabled == false）",
                !ReadTimer(w).Enabled, "False", ReadTimer(w).Enabled.ToString());
        }

        // 边界：从未 Show() 就 Close()（句柄从未建立，OnFormClosed 可能不触发）
        var neverShown = NewToast(ShortDismissMs);

        // 断言参数必须真的依赖行为：原先写的是 `Check(..., true, ...)`，那个字面量恒真，
        // 真正起作用的只是"抛异常会让进程崩掉"。改成显式捕获并断言"没有捕获到"。
        Exception? closeError = null;
        try { neverShown.Close(); }
        catch (Exception ex) { closeError = ex; }

        Timer timer = ReadTimer(neverShown);
        Notes.Add($"从未 Show() 就 Close()：IsDisposed = {neverShown.IsDisposed}，"
                + $"IsHandleCreated = {neverShown.IsHandleCreated}，Timer.Enabled = {timer.Enabled}，Timer 非 null = {timer is not null}。"
                + "从外部无法区分「Timer 已被释放」与「它从未被 Start」（两者都表现为 Enabled == False）；"
                + "实际影响为零：该分支下 _fadeTimer 从未 Start，故不存在 OS 定时器。");
        if (closeError is not null)
            InvokeErrors.Add("从未 Show() 就 Close(): " + closeError.GetType().Name + ": " + closeError.Message);

        Check("从未 Show() 就 Close() 不抛异常",
            closeError is null, "不抛异常",
            closeError is null ? "不抛异常" : $"{closeError.GetType().Name}: {closeError.Message}");
        neverShown.Dispose();

        // 报告项：窗体释放后显式赋给 Font 的那个 Font 归谁释放
        using (var w = NewToast(ShortDismissMs))
        {
            w.Show();
            Font f = w.Font;
            w.Close();
            string state;
            try { _ = f.Height; state = "仍可访问（提示窗体未释放它）"; }
            catch (Exception ex) { state = $"已失效（{ex.GetType().Name}）"; }
            Notes.Add($"窗体释放后，构造期 new 的 Font 的状态：{state}。"
                    + "仅凭这一观察不能定论是否泄漏（需看 §10 的 GDI 对象计数）。");
        }
    }

    // ───────── 10. 资源与焦点观察（报告项，不断言） ─────────

    /// <summary>
    /// 两条无法在当前（锁定）会话下定论、但很值得量化的观察：
    /// 1. `Font = new Font(...)` 在每个提示条上新建且从未显式释放 —— 它到底会不会积成 GDI 句柄泄漏？
    /// 2. 提示条弹出时是否真的没有抢走前台窗口？
    /// 两者都只记录不断言：它们是简报代码的既有行为，不是本任务的实现偏差。
    /// </summary>
    private static void ObserveResourcesAndFocus()
    {
        // ── 1. GDI 对象计数 ──
        for (int i = 0; i < 20; i++)   // 预热：排除首次创建的静态开销
        {
            var warm = NewToast(50);
            warm.Show();
            warm.Close();
            warm.Dispose();
        }

        CollectNow();
        uint before = GdiObjectCount();

        const int Rounds = 200;
        for (int i = 0; i < Rounds; i++)
        {
            var w = NewToast(50);
            w.Show();
            w.Close();
            w.Dispose();
        }

        CollectNow();
        uint after = GdiObjectCount();
        long delta = (long)after - before;

        Notes.Add($"GDI 对象数：{Rounds} 个提示条前后 {before} → {after}（差 {delta}）"
                + (delta > 50
                    ? $" ← 回收后仍增长 {delta}，疑似每提示条泄漏一个 Font/GDI 对象"
                    : " ← 全量 GC 后未见随提示条数量的线性增长（Font 的终结器能回收它）"));

        // ── 2. 焦点行为 ──
        using var host = new Form
        {
            Text = "ToastProbeFocusHost",
            Width = 300,
            Height = 200,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(20, 20),
        };

        host.Show();
        host.Activate();
        PumpFor(150);
        IntPtr beforeFore = GetForegroundWindow();
        bool hostWasForeground = beforeFore == host.Handle;
        bool hostHadFocus = host.ContainsFocus;

        using var toast = NewToast(ShortDismissMs);
        toast.Show();
        PumpFor(150);
        IntPtr afterFore = GetForegroundWindow();
        bool sceneIntact = afterFore == host.Handle;
        bool focusMoved = toast.ContainsFocus && !host.ContainsFocus;

        Notes.Add($"焦点行为：弹提示条前前台窗口 = 0x{beforeFore.ToInt64():X}（是探测宿主：{hostWasForeground}）、"
                + $"宿主 ContainsFocus = {hostHadFocus}；弹出后前台 = 0x{afterFore.ToInt64():X}（仍是宿主：{sceneIntact}、"
                + $"相对弹出前是否变化：{afterFore != beforeFore}）、"
                + $"宿主 ContainsFocus = {host.ContainsFocus}、toast.ContainsFocus = {toast.ContainsFocus}");

        // 真正要守的不变量是**跳进程前台不被抢**，而它的前提是“宿主本来就在前台”。
        // 会话锁定时 SetForegroundWindow 无效，这个前提根本立不起来 —— 那就必须如实
        // 报成"不可结论"，而不是报成"保阶未生效"（后者会误导控制者去改本就正确的代码）。
        if (!hostWasForeground)
        {
            Notes.Add("  → 本会话下探测宿主始终未成为前台窗口（锁定会话下 Activate()/SetForegroundWindow 受限），"
                    + "故**跳进程前台未被抢**这个主不变量**不可结论**，必须 T16 在解锁会话下用真机复核。");
            Notes.Add($"  → 仅供 T16 参考的必要观察：本进程内部焦点确实从宿主移到了 toast"
                    + $"（宿主 {hostHadFocus} → {host.ContainsFocus}，toast = {toast.ContainsFocus}）。"
                    + "这不矛盾：WS_EX_NOACTIVATE 阻止的是**跳进程前台抢占**（实测前台窗口在弹出前后未变），"
                    + "而非本线程内部的焦点归属。真机上只要验证“在记事本里持续打字时弹提示条不丢字”即可。");
        }
        else if (!sceneIntact)
        {
            Notes.Add("  → ⚠️ 宿主原本在前台，而弹出提示条后前台变了 —— 这是真正的失败，须立即修。");
        }
        else if (focusMoved)
        {
            Notes.Add("  → 前台未被抢（主不变量成立）；仅本进程内部焦点移到 toast，对本产品无害（托盘程序无其他可聚焦窗口）。");
        }
        else
        {
            Notes.Add("  → 前台与焦点均未变 ✔");
        }

        toast.Close();
        host.Close();
    }

    private static void CollectNow()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Application.DoEvents();
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// 窗口的**原生**物理像素矩形。
    /// 为什么用原生矩形而不是 <c>Control.Bounds</c>：判定"窗口到底在屏幕的哪里"应该问 OS，
    /// WinForms 的托管缓存只是它对同一事实的副本，多一层间接就多一个可能不自洽的地方。
    ///
    /// 实测记录（修复轮由控制者直接复核，4 次测量）：在当前实现上两者**一致** ——
    /// 尺寸估计版读到 <c>{1560,960,…}</c>、已修复版读到 <c>{1552,988,…}</c>，
    /// `Control.Bounds` 与 `GetWindowRect` 读数相同。也就是说：越界是**真实**的
    /// （1560+368=1928 &gt; 1920），而"托管缓存会陈旧到给出假结果"这一点
    /// **本轮没有复现**，不作为结论。保留原生读数是因为它更直接。
    /// </summary>
    private static Rectangle NativeBounds(ToastWindow w)
    {
        if (!GetWindowRect(w.Handle, out RECT r))
        {
            InvokeErrors.Add("GetWindowRect 失败");
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    private static uint GdiObjectCount() => GetGuiResources(Process.GetCurrentProcess().Handle, 0);

    // ───────────────────── 8. 异常即失败 ─────────────────────

    private static void CheckNoExceptions()
    {
        _invokeErrorsAtSection8 = InvokeErrors.Count;

        Check("全部反射调用未抛异常", InvokeErrors.Count == 0, "0 个异常",
            InvokeErrors.Count == 0 ? "0 个异常" : $"{InvokeErrors.Count} 个：" + string.Join(" | ", InvokeErrors));
    }

    // ───────────────── 9. 变异灵敏度：错误实现必须被检出 ─────────────────

    private static void CheckMutationSensitivity()
    {
        // (A) 去掉 _hovering 守卫的"错误 OnFadeTick"在悬停中必须能关闭窗体。
        //     这证明 §4(b) 的"没关"是被守卫挡住的，而不是因为别的原因关不掉。
        using (var w = NewToast(ShortDismissMs))
        {
            w.Show();
            Invoke(w, "OnMouseEnter", new[] { typeof(EventArgs) }, new object[] { EventArgs.Empty });
            PumpFor(PumpMs);

            Check("(A) 前置：悬停 + 已过 dismissMs，真实实现仍未关闭",
                !w.IsDisposed, "仍打开", w.IsDisposed ? "已关闭" : "仍打开");

            WrongFadeTick(w);   // 故意少了 `if (_hovering) return;`

            Check("(A) 变异：忽略 _hovering 的错误实现会关闭窗体 ⇒ 守卫是承重的、§4(b) 有鉴别力",
                w.IsDisposed, "已关闭", w.IsDisposed ? "已关闭" : "仍打开");
        }

        // (B) 本节能做的只有"确认真实值含该位"。"抹掉该位后 §2 会变红"这个证据**不能**
        //     在这里制造 —— 它需要重新编译一份没有该常量的实现，也就是
        //     tools/ToastWindowProbe/run-mutations.sh 里的 noNoActivate 变异。
        //
        //     曾经这里写过 `Check(..., (wrongEx & WS_EX_NOACTIVATE) == 0, ...)`，
        //     而 `wrongEx = realEx & ~WS_EX_NOACTIVATE` —— 该式对**任意**输入恒真
        //     （x & ~b & b 恒为 0），它不是证据，只是看起来像证据。
        using (var w = NewToast(ShortDismissMs))
        {
            int realEx = ReadExStyle(w);
            Check("(B) 真实 ExStyle 含 WS_EX_NOACTIVATE（其反例由 run-mutations.sh 的 noNoActivate 变异覆盖）",
                (realEx & WS_EX_NOACTIVATE) != 0, "非 0", $"0x{realEx:X8}");
        }

        // (C) §6 的亮像素检测对"纯背景"必须返回 0（无假阳性）。
        using (var bmp = new Bitmap(160, 48, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(32, 32, 32));
            int light = CountLightPixels(bmp);
            Check("(C) 变异：纯背景位图的亮像素 == 0 ⇒ §6 的检测无假阳性",
                light == 0, "0", light.ToString());
        }

        // (D) §4 的"关闭"判据对"一直打开的窗体"必须为假（无假阳性）。
        using (var w = NewToast(dismissMs: 60000))
        {
            w.Show();
            PumpFor(200);
            Check("(D) 变异：dismissMs 很大时窗体会保持打开 ⇒ §4 的\"已关闭\"判据有鉴别力",
                !IsGone(w), "仍打开", w.IsDisposed ? "已关闭" : "仍打开");
            w.Close();
        }
    }

    /// <summary>故意写错的淡出判定：少了 <c>if (_hovering) return;</c>，其余与真实实现一致。</summary>
    private static void WrongFadeTick(ToastWindow w)
    {
        DateTime shownAt = ReadField<DateTime>(w, "_shownAt");
        int dismissMs = ReadField<int>(w, "_dismissMs");

        if ((DateTime.UtcNow - shownAt).TotalMilliseconds >= dismissMs)
            w.Close();
    }

    // ───────────────────────── 消息泵 ─────────────────────────

    /// <summary>
    /// 驱动**真实的消息泵**。`System.Windows.Forms.Timer` 靠 WM_TIMER 触发，
    /// 而 WM_TIMER 只有在消息循环分派时才到达 —— <see cref="Application.DoEvents"/>
    /// 正好分派调用线程队列里的消息，因此这里跑的是真实的 Timer tick 路径，
    /// 而不是"反射调一下 OnFadeTick 假装它响了"。
    /// </summary>
    private static void PumpFor(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>挂一个 tick 计数器到真实的 _fadeTimer 上，用来证明它确实在跑。</summary>
    private static Func<int> AttachTickCounter(ToastWindow w)
    {
        int ticks = 0;
        ReadTimer(w).Tick += (_, _) => ticks++;
        return () => ticks;
    }

    private static Timer ReadTimer(ToastWindow w)
    {
        FieldInfo? fi = typeof(ToastWindow).GetField("_fadeTimer", NonPublic);
        if (fi is null)
        {
            InvokeErrors.Add("字段 _fadeTimer 未找到");
            return new Timer();
        }

        return (Timer)fi.GetValue(w)!;
    }

    // ───────────────────────── 反射辅助 ─────────────────────────

    private static Exception? Invoke(object target, string method, Type[] parameterTypes, object?[] args)
    {
        MethodInfo? mi = target.GetType().GetMethod(method, NonPublic, null, parameterTypes, null);
        if (mi is null)
        {
            string sig = $"{method}({string.Join(", ", parameterTypes.Select(t => t.Name))})";
            InvokeErrors.Add($"方法 {sig} 未找到");
            return new MissingMethodException(sig);
        }

        try
        {
            mi.Invoke(target, args);
            return null;
        }
        catch (TargetInvocationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            InvokeErrors.Add($"{method}: {inner.GetType().Name}: {inner.Message}");
            return inner;
        }
    }

    private static T ReadField<T>(object target, string name)
    {
        FieldInfo? fi = target.GetType().GetField(name, NonPublic);
        if (fi is null)
        {
            InvokeErrors.Add($"字段 {name} 未找到");
            return default!;
        }

        object? v = fi.GetValue(target);
        return v is T t ? t : default!;
    }

    private static ToastAction[]? ReadActions(ToastWindow w) => ReadField<ToastAction[]?>(w, "_actions");

    /// <summary>读 <c>Control</c> 上 <c>protected</c> 的布尔属性（如 <c>DoubleBuffered</c>）。</summary>
    private static bool ReadProtectedBool(ToastWindow w, string propertyName)
    {
        PropertyInfo? pi = FindNonPublicProperty(typeof(ToastWindow), propertyName);
        if (pi is null)
        {
            InvokeErrors.Add($"属性 {propertyName} 未找到");
            return false;
        }

        try
        {
            return pi.GetValue(w) is bool b && b;
        }
        catch (TargetInvocationException ex)
        {
            InvokeErrors.Add($"{propertyName}: {(ex.InnerException ?? ex).Message}");
            return false;
        }
    }

    private static Rectangle[] ReadButtonRects(ToastWindow w) => ReadField<Rectangle[]>(w, "_buttonRects") ?? Array.Empty<Rectangle>();

    private static int ReadExStyle(ToastWindow w)
    {
        PropertyInfo? pi = FindNonPublicProperty(typeof(ToastWindow), "CreateParams");
        if (pi is null)
        {
            InvokeErrors.Add("属性 CreateParams 未找到");
            return 0;
        }

        try
        {
            object? cp = pi.GetValue(w);
            if (cp is null) return 0;

            PropertyInfo? ex = cp.GetType().GetProperty("ExStyle");
            return ex?.GetValue(cp) is int v ? v : 0;
        }
        catch (TargetInvocationException ex)
        {
            InvokeErrors.Add("CreateParams: " + (ex.InnerException ?? ex).Message);
            return 0;
        }
    }

    private static PropertyInfo? FindNonPublicProperty(Type type, string name)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            PropertyInfo? pi = t.GetProperty(name, NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (pi is not null) return pi;
        }

        return type.GetProperty(name, NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
    }

    // ───────────────────────── 渲染与像素比较 ─────────────────────────

    /// <summary>
    /// 把窗体渲染进位图。<paramref name="overridePaint"/> 为 null 时驱动**真实的 OnPaint**。
    /// 先 <c>g.Clear(BackColor)</c>：真实绘制路径上背景由 WM_ERASEBKGND 填好，
    /// 手动驱动 OnPaint 时少了这一步，背景会是透明的，比较就失真了。
    /// </summary>
    private static Bitmap Render(ToastWindow w, Action<Graphics, ToastWindow>? overridePaint)
    {
        var bmp = new Bitmap(w.Width, w.Height, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(bmp);
        try
        {
            g.Clear(w.BackColor);

            if (overridePaint is null)
            {
                using var pe = new PaintEventArgs(g, new Rectangle(0, 0, w.Width, w.Height));
                Invoke(w, "OnPaint", new[] { typeof(PaintEventArgs) }, new object[] { pe });
            }
            else
            {
                overridePaint(g, w);
            }
        }
        finally
        {
            g.Dispose();
        }

        return bmp;
    }

    /// <summary>变异实现：画边框与按钮（含标签），但**不画正文 Message**。</summary>
    private static void PaintWithoutMessageText(Graphics g, ToastWindow w)
    {
        using var border = new Pen(Color.FromArgb(90, 255, 255, 255));
        g.DrawRectangle(border, 0, 0, w.Width - 1, w.Height - 1);

        ToastAction[] actions = ReadActions(w) ?? Array.Empty<ToastAction>();
        Rectangle[] rects = ReadButtonRects(w);

        for (int i = 0; i < rects.Length; i++)
        {
            using var fill = new SolidBrush(Color.FromArgb(35, 255, 255, 255));
            g.FillRectangle(fill, rects[i]);

            using var pen = new Pen(Color.FromArgb(140, 255, 255, 255));
            g.DrawRectangle(pen, rects[i]);

            if (i < actions.Length)
            {
                TextRenderer.DrawText(g, actions[i].Label, w.Font, rects[i], Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>统计指定区域（null = 整幅）内的"亮像素"（RGB 三通道都超过阈值），用来判定白色文字是否真的被画出来。</summary>
    private static int CountLightPixels(Bitmap bmp, Rectangle? area = null)
    {
        Rectangle r = area ?? new Rectangle(0, 0, bmp.Width, bmp.Height);
        r = Rectangle.Intersect(r, new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (r.Width <= 0 || r.Height <= 0) return 0;

        byte[] px = ToBgra(bmp);
        int n = 0;

        for (int y = r.Top; y < r.Bottom; y++)
        {
            for (int x = r.Left; x < r.Right; x++)
            {
                int i = (y * bmp.Width + x) * 4;
                if (px[i] > LightThreshold && px[i + 1] > LightThreshold && px[i + 2] > LightThreshold)
                    n++;
            }
        }

        return n;
    }

    private static byte[] ToBgra(Bitmap src)
    {
        var buffer = new byte[src.Width * src.Height * 4];
        BitmapData data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                                       ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
        }
        finally
        {
            src.UnlockBits(data);
        }

        return buffer;
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private const string MessageText = "已复制到剪贴板";

    private static ToastAction[] TwoActions() => new[]
    {
        new ToastAction("open", "打开"),
        new ToastAction("copy", "复制"),
    };

    private static ToastWindow NewToast(int dismissMs, ToastAction[]? actions = null,
                                        Point? at = null, ToastKind kind = ToastKind.Success,
                                        string message = MessageText)
    {
        var w = new ToastWindow(message, actions ?? Array.Empty<ToastAction>(),
                                at ?? new Point(120, 120), dismissMs, kind);
        w.ActionClicked += a => { _lastAction = a; _actionCount++; };
        return w;
    }

    private static MouseEventArgs MouseArgs(Point p, MouseButtons button = MouseButtons.Left)
        => new(button, 1, p.X, p.Y, 0);

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    /// <summary>窗体的"已关闭"判据。</summary>
    private static bool IsGone(ToastWindow w) => w.IsDisposed || !w.IsHandleCreated;

    private static void Section(string title, Action body)
    {
        Console.WriteLine($"----- {title} -----");

        // 记录本节实际产出了多少项断言 —— 汇总时逐节列出，
        // 这样"某一节是不是悄悄变空了"能直接看出来，而不必去数源码。
        int before = _assertions;
        body();
        SectionCounts.Add((title, _assertions - before));

        Console.WriteLine();
    }

    private static void Check(string name, bool ok, string expected, string actual)
    {
        _assertions++;
        if (ok) _pass++; else _fail++;
        Lines.Add($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        Lines.Add($"         期望 = {expected}");
        Lines.Add($"         实测 = {actual}");
    }

    private static string Show(Rectangle r) => $"{{X={r.X},Y={r.Y},W={r.Width},H={r.Height}}}";

    private static string Show(Size s) => $"{s.Width}×{s.Height}";

    private static string Show(Point p) => $"({p.X},{p.Y})";
}
