using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using QrScan.UI;

namespace OverlayWindowProbe;

/// <summary>
/// <see cref="OverlayWindow"/> 的常驻回归探针。
///
/// **为什么需要它**：全程序只有一处坐标换算（`local + _virtualBounds.Location`），
/// 而"冻结位图在遮罩上逐像素 1:1 绘制"是另一个关键不变量。两者都无法用普通单元测试
/// 覆盖（需要真实桌面/多显示器），而一旦错位：1920px 上 1% 就是约 19px，足以切掉
/// 紧框小码的一个模块 —— 但 <c>SelectedRegionPhysical</c> 仍然完全正确，
/// 所有坐标类断言依旧全绿。所以必须单独、逐像素地验它。
///
/// **手法**：csproj 用 <c>&lt;Compile Include&gt;</c> 把仓库里**真实的那份**
/// `OverlayWindow.cs` 编进本程序集（不是副本 —— 副本会漂移），于是 `internal` 的
/// 窗体在这里可见，且不需要给仓库加 `InternalsVisibleTo`。
/// 再用反射直接驱动 `OnMouseDown/OnMouseMove/OnMouseUp/OnKeyDown/OnPaint/OnShown`
/// 与 `WndProc`，因此**不需要可见桌面**，锁屏或无人值守环境下同样可跑。
///
/// 任一项失败时进程退出码为 1，可直接被脚本/CI 当检查用。
/// </summary>
internal static class Program
{
    private const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    private const int Width = 800;
    private const int Height = 600;

    /// <summary>合成虚拟桌面：原点为负，模拟"副屏在主屏左侧且上方"。</summary>
    private static readonly Rectangle VirtualBounds = new(-100, -50, Width, Height);

    /// <summary>
    /// 逐像素比较区域。做成满幅拖拽后：<c>Region.Exclude</c> 只留下最右 1 列与最下 1 行
    /// 会被变暗，2px 亮框又压在四周，尺寸标签因 <c>y &lt; 0</c> 翻到选区下方而被画布裁掉。
    /// 于是留出 4px 余量后，内部矩形应当就是源图的 1:1 拷贝。
    /// </summary>
    private static readonly Rectangle CompareArea = new(4, 4, Width - 8, Height - 8);

    private static Bitmap _frozen = null!;

    private static int _pass;
    private static int _fail;
    private static readonly List<string> Lines = new();
    private static readonly List<string> Notes = new();
    private static readonly List<string> InvokeErrors = new();
    private static readonly List<string> ScenarioClosures = new();

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("===== OverlayWindowProbe：OverlayWindow 常驻回归探针 =====");
        Console.WriteLine($"合成虚拟桌面矩形 = {Show(VirtualBounds)}（原点为负）");
        Console.WriteLine($"冻结位图 = {Width}×{Height}，图案 = 1px 棋盘格(x<400) + 8px 棋盘格(x>=400) + 2 个异色标记块");
        Console.WriteLine($"逐像素比较区域 = {Show(CompareArea)}");
        Console.WriteLine();

        _frozen = BuildPattern();

        Section("1. 构造期属性", CheckConstructor);
        Section("2. 客户区尺寸 / OnShown / Show()", CheckClientAreaAndShow);
        Section("3. 唯一一处坐标换算（负原点）", CheckCoordinateConversion);
        Section("4. MinimumDragPixels 两侧边界", CheckMinimumDrag);
        Section("5. 取消路径", CheckCancelPaths);
        Section("6. WM_DPICHANGED", CheckDpiChanged);
        Section("7. 所有权与引用", CheckOwnership);
        Section("8. 异常即失败 + 窗体确实关闭", CheckNoExceptionsAndClosed);
        Section("9. 逐像素 1:1 绘制", CheckPixelExact);
        Section("10. 变异灵敏度：错误实现必须被逐像素比对抓住", CheckMutationSensitivity);

        _frozen.Dispose();

        if (Notes.Count > 0)
        {
            Console.WriteLine("----- 报告项（不断言，仅供参考）-----");
            foreach (string n in Notes) Console.WriteLine("  " + n);
            Console.WriteLine();
        }

        Console.WriteLine($"===== 结果：{_pass} 通过 / {_fail} 失败 =====");
        foreach (string l in Lines) Console.WriteLine(l);
        Console.WriteLine(_fail == 0 ? "退出码 0（全绿）" : $"退出码 1（{_fail} 项失败）");
        return _fail == 0 ? 0 : 1;
    }

    // ───────────────────────── 1. 构造期属性 ─────────────────────────

    private static void CheckConstructor()
    {
        using var w = new OverlayWindow(_frozen, VirtualBounds);

        Check("AutoScaleMode == None（绕开 WinForms DPI 缩放）",
            w.AutoScaleMode == AutoScaleMode.None, "None", w.AutoScaleMode.ToString());
        Check("FormBorderStyle == None（无边框 = 无客户区偏移）",
            w.FormBorderStyle == FormBorderStyle.None, "None", w.FormBorderStyle.ToString());
        Check("TopMost == true", w.TopMost, "True", w.TopMost.ToString());
        Check("ShowInTaskbar == false", !w.ShowInTaskbar, "False", w.ShowInTaskbar.ToString());
        Check("KeyPreview == true（Esc 才收得到）", w.KeyPreview, "True", w.KeyPreview.ToString());
        Check("Bounds == 物理像素矩形（负原点未被改写）",
            w.Bounds == VirtualBounds, Show(VirtualBounds), Show(w.Bounds));
        Check("SelectedRegionPhysical 初始为 null",
            w.SelectedRegionPhysical is null, "null", Show(w.SelectedRegionPhysical));

        _ = w.Handle;
        Check("创建句柄后 Bounds 仍未被缩放",
            w.Bounds == VirtualBounds, Show(VirtualBounds), Show(w.Bounds));
    }

    // ───────────────── 2. 客户区尺寸 / OnShown / Show() ─────────────────

    private static void CheckClientAreaAndShow()
    {
        // 客户区尺寸是整条换算链唯一的前提：客户区 (0,0) == 虚拟桌面左上角。
        // 若存在非客户区偏移，所有选区会整体平移，而坐标类断言一项都发现不了。
        using (var w = new OverlayWindow(_frozen, VirtualBounds))
        {
            _ = w.Handle;
            Check("ClientSize == 虚拟桌面尺寸（未 Show，仅建句柄）",
                w.ClientSize == VirtualBounds.Size, Show(VirtualBounds.Size), Show(w.ClientSize));
        }

        // OnShown 里会重新断言 Bounds 并 Activate()/Focus()。
        using (var w = new OverlayWindow(_frozen, VirtualBounds))
        {
            _ = w.Handle;
            w.Bounds = new Rectangle(999, 999, 10, 10);

            Exception? err = TryInvoke(w, "OnShown", typeof(EventArgs), EventArgs.Empty);
            Check("OnShown 反射调用零异常", err is null, "无异常", err is null ? "无异常" : $"{err.GetType().Name}: {err.Message}");
            Check("OnShown 重新断言 Bounds 为物理像素矩形",
                w.Bounds == VirtualBounds, Show(VirtualBounds), Show(w.Bounds));
        }

        // Show() 会真的走一遍窗口创建/放置。Windows 对窗口位置有钳制逻辑，
        // 这里验证它没有把我们的物理像素矩形挪走。
        // 注意：此检查会短暂显示一个无边框窗口（锁屏/无人值守下不可见）。
        var sw = new OverlayWindow(_frozen, VirtualBounds);
        try
        {
            sw.Show();
            Application.DoEvents();

            Check("Show() 后 Bounds 未被系统钳制",
                sw.Bounds == VirtualBounds, Show(VirtualBounds), Show(sw.Bounds));
            Check("Show() 后 ClientSize == 虚拟桌面尺寸",
                sw.ClientSize == VirtualBounds.Size, Show(VirtualBounds.Size), Show(sw.ClientSize));
        }
        finally
        {
            if (!sw.IsDisposed) sw.Dispose();
        }
    }

    // ─────────────────── 3. 唯一一处坐标换算（负原点） ───────────────────

    private static void CheckCoordinateConversion()
    {
        Scenario s1 = Run(new Point(10, 20), new Point(110, 120));
        Check("正向拖拽 (10,20)->(110,120)，原点(-100,-50) → (-90,-30,100,100)",
            s1.Selection == new Rectangle(-90, -30, 100, 100),
            Show(new Rectangle(-90, -30, 100, 100)), Show(s1.Selection));

        Scenario s2 = Run(new Point(200, 300), new Point(120, 250));
        Check("反向拖拽 (200,300)->(120,250) → Normalize + 原点 → (20,200,80,50)",
            s2.Selection == new Rectangle(20, 200, 80, 50),
            Show(new Rectangle(20, 200, 80, 50)), Show(s2.Selection));

        Scenario s3 = Run(new Point(0, 0), new Point(Width - 1, Height - 1));
        Check("满幅拖拽 → 客户区原点映射到虚拟桌面左上角 (-100,-50,799,599)",
            s3.Selection == new Rectangle(-100, -50, 799, 599),
            Show(new Rectangle(-100, -50, 799, 599)), Show(s3.Selection));
    }

    // ──────────────────── 4. MinimumDragPixels 两侧边界 ────────────────────

    private static void CheckMinimumDrag()
    {
        Scenario three = Run(new Point(10, 10), new Point(13, 13));
        Check("恰好 3px 拖拽 → 不取消，得 (-90,-40,3,3)",
            three.Selection == new Rectangle(-90, -40, 3, 3),
            Show(new Rectangle(-90, -40, 3, 3)), Show(three.Selection));

        Scenario two = Run(new Point(10, 10), new Point(12, 12));
        Check("2px 拖拽 → 视为取消 → null", two.Selection is null, "null", Show(two.Selection));

        Scenario one = Run(new Point(10, 10), new Point(11, 11));
        Check("1px 拖拽 → 视为取消 → null", one.Selection is null, "null", Show(one.Selection));
    }

    // ────────────────────────── 5. 取消路径 ──────────────────────────

    private static void CheckCancelPaths()
    {
        Scenario esc = Run(new Point(10, 20), new Point(110, 120), escape: true);
        Check("Esc → 取消 → null", esc.Selection is null, "null", Show(esc.Selection));

        Scenario zero = Run(new Point(10, 10), new Point(10, 10));
        Check("零面积拖拽（原地）→ 取消 → null", zero.Selection is null, "null", Show(zero.Selection));

        Scenario right = Run(new Point(50, 50), new Point(50, 50), button: MouseButtons.Right, move: false);
        Check("右键按下 → 取消 → null", right.Selection is null, "null", Show(right.Selection));
    }

    // ───────────────────────── 6. WM_DPICHANGED ─────────────────────────

    private static void CheckDpiChanged()
    {
        using var w = new OverlayWindow(_frozen, VirtualBounds);
        _ = w.Handle;

        w.Bounds = new Rectangle(999, 999, 10, 10);
        Rectangle before = w.Bounds;

        MethodInfo? wndProc = typeof(OverlayWindow).GetMethod(
            "WndProc", NonPublic, null, new[] { typeof(Message).MakeByRefType() }, null);
        if (wndProc is null)
        {
            Check("WndProc(WM_DPICHANGED) 重新断言 Bounds", false, "找到 WndProc", "未找到 WndProc");
            return;
        }

        var msg = Message.Create(w.Handle, 0x02E0, IntPtr.Zero, IntPtr.Zero);
        Exception? err = null;
        try
        {
            wndProc.Invoke(w, new object[] { msg });
        }
        catch (TargetInvocationException ex)
        {
            err = ex.InnerException ?? ex;
        }

        if (err is not null) InvokeErrors.Add($"WndProc(WM_DPICHANGED): {err}");

        Check("WndProc(WM_DPICHANGED) 重新断言 Bounds 为物理像素矩形",
            err is null && w.Bounds == VirtualBounds,
            $"{Show(VirtualBounds)}（处理前 {Show(before)}）",
            err is null ? Show(w.Bounds) : $"{err.GetType().Name}: {err.Message}");
    }

    // ──────────────────────── 7. 所有权与引用 ────────────────────────

    private static void CheckOwnership()
    {
        // 窗体绝不该释放调用方传入的冻结位图（所有权归 AppController）。
        var owner = new OverlayWindow(_frozen, VirtualBounds);
        _ = owner.Handle;
        _ = RunProbeOn(owner, new Point(10, 20), new Point(110, 120), escape: false, button: MouseButtons.Left, move: true);
        owner.Dispose();

        string detail;
        bool usable;
        try
        {
            usable = _frozen.Width == Width && _frozen.Height == Height;
            _frozen.GetPixel(0, 0);
            detail = $"{_frozen.Width}x{_frozen.Height}，仍可读像素";
        }
        catch (Exception ex)
        {
            usable = false;
            detail = $"{ex.GetType().Name}: {ex.Message}";
        }

        Check("窗体 Dispose 后，传入的冻结位图仍可用（所有权归 AppController）",
            usable, $"{Width}x{Height} 仍可读", detail);

        using var w = new OverlayWindow(_frozen, VirtualBounds);
        FieldInfo? field = typeof(OverlayWindow).GetField("_frozen", NonPublic);
        object? actual = field?.GetValue(w);
        Check("_frozen 持有的就是传入的那张位图（未偷偷拷贝）",
            ReferenceEquals(actual, _frozen), "同一引用",
            field is null ? "找不到 _frozen 字段" : (ReferenceEquals(actual, _frozen) ? "同一引用" : "不是同一引用"));
    }

    // ──────────────── 8. 异常即失败 + 窗体确实关闭 ────────────────

    private static void CheckNoExceptionsAndClosed()
    {
        // 旧探针把 TargetInvocationException 记成 PASS，于是取消路径的 PASS
        // 无法区分"正常取消"与"Close() 内部抛异常" —— 真机上后者意味着遮罩关不掉。
        Check("全部反射调用零异常", InvokeErrors.Count == 0, "0 个异常",
            InvokeErrors.Count == 0 ? "0 个异常" : string.Join(" | ", InvokeErrors));

        bool allClosed = ScenarioClosures.All(c => c.StartsWith("已释放", StringComparison.Ordinal));
        Check("全部场景窗体在 Close() 后确实已释放（IsDisposed / 句柄已销毁）",
            allClosed, "全部已释放", string.Join("; ", ScenarioClosures));
    }

    // ──────────────────── 9. 逐像素 1:1 绘制 ────────────────────

    private static void CheckPixelExact()
    {
        using var w = Dragging();

        (Bitmap at96, float dpi96) = RenderRealOnPaint(w, 96f);
        using (at96)
        {
            (int diff, string sample) = Compare(_frozen, at96, CompareArea);
            Check($"真实 OnPaint 在 g.DpiX={dpi96} 下逐像素 1:1", diff == 0, "0 差异像素",
                $"{diff} 差异像素{(diff == 0 ? "" : "   例:" + sample)}");
        }

        (Bitmap at144, float dpi144) = RenderRealOnPaint(w, 144f);
        using (at144)
        {
            Check("强制 SetResolution(144,144) 后 Graphics 的 DPI 实测生效",
                Math.Abs(dpi144 - 144f) < 0.5f, "g.DpiX == 144", $"g.DpiX == {dpi144}");

            (int diff, string sample) = Compare(_frozen, at144, CompareArea);
            Check($"真实 OnPaint 在 g.DpiX={dpi144} 下逐像素 1:1（与高 DPI 无关）",
                diff == 0, "0 差异像素", $"{diff} 差异像素{(diff == 0 ? "" : "   例:" + sample)}");
        }

        // 上面两项都是**反射直调** OnPaint，只能证明“绘制代码是 1:1”。
        // 这一项走 Control.DrawToBitmap（即 WM_PRINTCLIENT），是**真实的窗口绘制管线**，
        // 因此它才能证明 OnPaint 确实会被管线调到。
        using (var w2 = Dragging())
        {
            var target = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (target)
            {
                try
                {
                    w2.DrawToBitmap(target, new Rectangle(0, 0, Width, Height));
                }
                catch (Exception ex)
                {
                    InvokeErrors.Add($"DrawToBitmap: {ex.GetType().Name}: {ex.Message}");
                }

                (int diff, string sample) = Compare(_frozen, target, CompareArea);
                Check("真实绘制管线（DrawToBitmap → WM_PRINTCLIENT）下逐像素 1:1",
                    diff == 0, "0 差异像素", $"{diff} 差异像素{(diff == 0 ? "" : "   例:" + sample)}");
            }
        }
    }

    // ───────────── 10. 变异灵敏度：错误实现必须被抓住 ─────────────

    private static void CheckMutationSensitivity()
    {
        // 这一节是判定第 9 节"逐像素比对是否真的有效"的唯一方法。
        // 三个"错误实现"都**内联在本文件里**（不是被验证源文件的副本），
        // 因此探针编译进哪份 OverlayWindow.cs 都不影响它们的独立性。
        foreach (float dpi in new[] { 96f, 144f })
        {
            (int scaled, _) = CompareAgainst(PaintWrongScaled, dpi);
            Check($"@{dpi} DPI：尺寸放大 1px 的错误实现被检出",
                scaled > 0, "> 0 差异像素", $"{scaled} 差异像素");

            (int shifted, _) = CompareAgainst(PaintWrongShifted, dpi);
            Check($"@{dpi} DPI：偏移 1px 的错误实现被检出",
                shifted > 0, "> 0 差异像素", $"{shifted} 差异像素");

            (int unscaled, _) = CompareAgainst(PaintWrongUnscaled, dpi);

            if (Math.Abs(dpi - 144f) < 0.5f)
            {
                // 144 DPI 下 DrawImageUnscaled 会按 Graphics 的 DPI 放大 → 必须被检出。
                Check("@144 DPI：DrawImageUnscaled 的错误实现被检出（它受 Graphics.DpiX 影响）",
                    unscaled > 0, "> 0 差异像素", $"{unscaled} 差异像素");
            }
            else
            {
                // 96 DPI 下它与正确实现**无法区分** —— 这是实测事实，不是缺陷。
                // 它正是"必须同时跑 144 DPI"这条要求的理由，故只报告、不断言：
                // 一旦将来 GDI+ 改变该行为，这行输出会变，而 144 DPI 的断言仍守着底线。
                Notes.Add($"[@96 DPI] DrawImageUnscaled 的差异像素 = {unscaled} —— "
                          + (unscaled == 0
                              ? "与正确实现无法区分（故 96 DPI 单跑对该风险零灵敏度，144 DPI 那条断言才是底线）"
                              : "已被检出"));
            }
        }
    }

    // ──────────────────────────── 基础设施 ────────────────────────────

    private sealed record Scenario(Rectangle? Selection, bool Closed, Exception? Error);

    private static Scenario Run(
        Point down,
        Point up,
        MouseButtons button = MouseButtons.Left,
        bool escape = false,
        bool move = true)
    {
        var w = new OverlayWindow(_frozen, VirtualBounds);
        _ = w.Handle;

        Exception? err = RunProbeOn(w, down, up, escape, button, move);

        // 必须真的关掉了：区分"正常取消"与"Close() 内部抛异常"。
        bool closed = w.IsDisposed || !w.IsHandleCreated;
        Rectangle? selection = w.SelectedRegionPhysical;

        ScenarioClosures.Add($"{(closed ? "已释放" : "未释放 ⚠")}[{Show(down)}->{Show(up)}"
                             + $"{(escape ? " Esc" : "")}{(button == MouseButtons.Right ? " 右键" : "")}]");

        if (!w.IsDisposed) w.Dispose();
        return new Scenario(selection, closed, err);
    }

    private static Exception? RunProbeOn(
        OverlayWindow w, Point down, Point up, bool escape, MouseButtons button, bool move)
    {
        Exception? err = TryInvoke(w, "OnMouseDown", typeof(MouseEventArgs),
            new MouseEventArgs(button, 1, down.X, down.Y, 0));

        if (button == MouseButtons.Left)
        {
            if (move)
            {
                err ??= TryInvoke(w, "OnMouseMove", typeof(MouseEventArgs),
                    new MouseEventArgs(MouseButtons.Left, 0, up.X, up.Y, 0));
            }

            if (escape)
            {
                err ??= TryInvoke(w, "OnKeyDown", typeof(KeyEventArgs), new KeyEventArgs(Keys.Escape));
            }
            else
            {
                err ??= TryInvoke(w, "OnMouseUp", typeof(MouseEventArgs),
                    new MouseEventArgs(MouseButtons.Left, 1, up.X, up.Y, 0));
            }
        }

        if (err is not null) InvokeErrors.Add($"{Show(down)}->{Show(up)}{(escape ? " Esc" : "")}: {err}");
        return err;
    }

    /// <summary>
    /// 造一个处于"满幅拖拽中"状态的窗体，供 OnPaint 渲染。
    /// **故意不发 OnMouseUp** —— 那会把 <c>_dragging</c> 置回 false 并 Close()，
    /// 于是 OnPaint 里 selection 为空、整幅被变暗，逐像素比对就会得到
    /// 与图案无关的恒定 234688 个差异（如 [255,255,255]->[145,145,145]，
    /// 正是 alpha=110 的黑盖在白色上的结果）。探针第一版就踩了这个坑，
    /// 而那恰好也证明了逐像素比对确实有灵敏度。
    /// </summary>
    private static OverlayWindow Dragging()
    {
        var w = new OverlayWindow(_frozen, VirtualBounds);
        _ = w.Handle;

        Exception? err = TryInvoke(w, "OnMouseDown", typeof(MouseEventArgs),
            new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
        err ??= TryInvoke(w, "OnMouseMove", typeof(MouseEventArgs),
            new MouseEventArgs(MouseButtons.Left, 0, Width - 1, Height - 1, 0));

        if (err is not null) InvokeErrors.Add($"Dragging() 准备满幅拖拽状态: {err}");
        return w;
    }

    private static Exception? TryInvoke(object target, string method, Type parameterType, EventArgs args)
    {
        MethodInfo? mi = target.GetType().GetMethod(method, NonPublic, null, new[] { parameterType }, null);
        if (mi is null) return new MissingMethodException($"{method}({parameterType.Name}) 未找到");

        try
        {
            mi.Invoke(target, new object[] { args });
            return null;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException ?? ex;
        }
    }

    /// <summary>用真实的 <c>OnPaint</c> 把窗体渲染进一张指定 DPI 的位图。</summary>
    private static (Bitmap Bitmap, float ActualDpi) RenderRealOnPaint(OverlayWindow w, float requestedDpi)
    {
        Bitmap target = NewTarget(requestedDpi);
        using var g = Graphics.FromImage(target);
        float actualDpi = g.DpiX;

        MethodInfo? onPaint = typeof(OverlayWindow).GetMethod(
            "OnPaint", NonPublic, null, new[] { typeof(PaintEventArgs) }, null);

        if (onPaint is null)
        {
            InvokeErrors.Add("找不到 OnPaint(PaintEventArgs)");
            return (target, actualDpi);
        }

        try
        {
            onPaint.Invoke(w, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, Width, Height)) });
        }
        catch (TargetInvocationException ex)
        {
            InvokeErrors.Add($"OnPaint: {ex.InnerException ?? ex}");
        }

        return (target, actualDpi);
    }

    private static (int Diff, string Sample) CompareAgainst(Action<Graphics, Bitmap> wrongPaint, float dpi)
    {
        Bitmap target = NewTarget(dpi);
        using (target)
        {
            using (var g = Graphics.FromImage(target)) wrongPaint(g, _frozen);
            return Compare(_frozen, target, CompareArea);
        }
    }

    private static Bitmap NewTarget(float dpi)
    {
        var target = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        if (Math.Abs(dpi - 96f) > 0.5f) target.SetResolution(dpi, dpi);
        return target;
    }

    // ── 故意写错的实现（独立内联，不是被验证源文件的副本）──

    /// <summary>计划明令"不要用"的写法：其行为受 <c>Graphics.DpiX/DpiY</c> 影响。</summary>
    private static void PaintWrongUnscaled(Graphics g, Bitmap source)
    {
        g.PageUnit = GraphicsUnit.Pixel;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImageUnscaled(source, 0, 0);
    }

    private static void PaintWrongScaled(Graphics g, Bitmap source)
    {
        g.PageUnit = GraphicsUnit.Pixel;
        g.DrawImage(source, new Rectangle(0, 0, source.Width + 1, source.Height + 1));
    }

    private static void PaintWrongShifted(Graphics g, Bitmap source)
    {
        g.PageUnit = GraphicsUnit.Pixel;
        g.DrawImage(source, new Rectangle(1, 0, source.Width, source.Height));
    }

    /// <summary>高频图案：均匀色发现不了缩放，必须有 1px 棋盘格与异色标记块。</summary>
    private static Bitmap BuildPattern()
    {
        var b = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int block = x < Width / 2 ? 1 : 8;
                bool even = ((x / block) + (y / block)) % 2 == 0;
                b.SetPixel(x, y, even ? Color.Black : Color.White);
            }
        }

        using (var g = Graphics.FromImage(b))
        {
            g.FillRectangle(Brushes.Red, 20, 20, 16, 16);
            g.FillRectangle(Brushes.Lime, Width - 40, Height - 40, 16, 16);
        }

        return b;
    }

    /// <summary>逐像素比对（用 LockBits 比字节数组，不用 GetPixel 逐点）。</summary>
    private static (int Diff, string Sample) Compare(Bitmap expected, Bitmap actual, Rectangle area)
    {
        byte[] e = ToRgba(expected);
        byte[] a = ToRgba(actual);

        int diff = 0;
        var samples = new List<string>();

        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                int i = (y * expected.Width + x) * 4;
                if (e[i] != a[i] || e[i + 1] != a[i + 1] || e[i + 2] != a[i + 2])
                {
                    diff++;
                    if (samples.Count < 3)
                        samples.Add($"({x},{y})[{e[i + 2]},{e[i + 1]},{e[i]}]->[{a[i + 2]},{a[i + 1]},{a[i]}]");
                }
            }
        }

        return (diff, string.Join(" ", samples));
    }

    private static byte[] ToRgba(Bitmap src)
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

    private static void Section(string title, Action body)
    {
        Console.WriteLine($"----- {title} -----");
        body();
        Console.WriteLine();
    }

    private static void Check(string name, bool ok, string expected, string actual)
    {
        if (ok) _pass++; else _fail++;
        Lines.Add($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        Lines.Add($"         期望 = {expected}");
        Lines.Add($"         实测 = {actual}");
    }

    private static string Show(Rectangle? r) =>
        r is { } v ? $"{{X={v.X},Y={v.Y},W={v.Width},H={v.Height}}}" : "null";

    private static string Show(Size s) => $"{s.Width}x{s.Height}";

    private static string Show(Point p) => $"({p.X},{p.Y})";
}
