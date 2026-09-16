using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using QrScan;
using QrScan.Core;
using QrScan.Core.Models;
using QrScan.Native;
using QrScan.UI;
using Timer = System.Windows.Forms.Timer;

namespace AppControllerProbe;

/// <summary>
/// AppController 常驻探针。
///
/// **它为什么存在**：本任务把全程序唯一一处**反向坐标换算**、状态机的两条边界规则、
/// 以及四条退出路径上的资源释放全部集中在 <c>AppController</c> 里。这三样恰好是
/// 最容易悄悄坏掉、又最难用肉眼发现的东西（负原点只在多显示器下暴露；漏释放
/// 只在长跑后暴露）。而当前会话处于锁定状态，"按热键框选一次"根本无法执行。
///
/// **手法**：把仓库里**真实的那几份源文件**编进探针程序集（见 csproj 的 Compile Include），
/// 于是 internal 类型直接可见、也不需要给仓库加 InternalsVisibleTo。
/// 遮罩是真实的 <c>OverlayWindow</c>，解码是真实的 <c>QrDecoder</c>，
/// 只有"屏幕"和"剪贴板"被替换掉（那是环境依赖，必须换成确定性的）。
///
/// **诚实边界**：§1–§4 真的在跑产品代码；§5 是**源码文本检查**，不是行为验证，
/// 在输出里明确标注为 [文本]。
/// </summary>
internal static class Program
{
    // ── 断言账本 ────────────────────────────────────────────────────────────
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>
    /// 断言总数闸门。
    ///
    /// 没有它，"删掉整整一节"这种破坏会让探针**变少但依然全绿**，而退出码 0。
    /// 任务 11 与任务 12 的审查者都独立点出了这个缺陷，这里是第三次出现，所以从
    /// 一开始就带上。改断言数必须同时改这里 —— 那正是我们想要的那一次停顿。
    /// </summary>
    private const int ExpectedAssertions = 51;

    /// <summary>合成虚拟桌面：原点**故意取负**，模拟"副屏位于主屏左上"的真实布局。</summary>
    private static readonly Rectangle VirtualBounds = new(-300, -200, 800, 600);

    private const int QrLeft = 100;
    private const int QrTop = 100;

    [STAThread]
    private static int Main()
    {
        StartWatchdog();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("===== AppControllerProbe =====");
        Console.WriteLine($"虚拟桌面（合成）：X={VirtualBounds.X} Y={VirtualBounds.Y} " +
                          $"W={VirtualBounds.Width} H={VirtualBounds.Height}   ← 原点为负是刻意的");
        Console.WriteLine($"仓库根（§5 的文本检查读它）：{ResolveRepoRoot()}");
        Console.WriteLine();

        using var harness = new Harness();

        Section("1. 反向坐标换算 ScreenToLocal（负原点是全程序第 1 号陷阱）",
            () => SectionCoordinateConversion());

        Section("2. 四条退出路径的资源纪律（冻结位图 29–66 MB，漏一条就是泄漏）",
            () => SectionResourceDiscipline(harness));

        Section("3. 状态机的两条边界规则",
            () => SectionStateMachine(harness));

        Section("4. 端到端：真实解码 → 剪贴板 → 托盘记录 → 提示条",
            () => SectionEndToEnd(harness));

        Section("5. 接线与顺序（★ 源码文本检查，不是行为验证）",
            () => SectionWiringTextChecks());

        return Report();
    }

    // ══════════════════════════════════════════════════════════════════════
    // §1 反向坐标换算
    // ══════════════════════════════════════════════════════════════════════

    private static void SectionCoordinateConversion()
    {
        // 真实布局：主屏 1920x1080 在 (0,0)，副屏 1920x1080 在主屏**左侧** → 原点 (-1920, 0)
        var twoMonitors = new Rectangle(-1920, 0, 3840, 1080);

        Rectangle r1 = AppController.ScreenToLocal(new Rectangle(-800, 300, 420, 420), twoMonitors);
        Check("负 X 原点：屏幕物理像素 → 位图局部坐标",
            r1 == new Rectangle(1120, 300, 420, 420),
            "1120,300,420,420", Describe(r1));

        Rectangle r2 = AppController.ScreenToLocal(new Rectangle(100, 50, 200, 200), twoMonitors);
        Check("负 X 原点下，位于主屏的选区仍然正确",
            r2 == new Rectangle(2020, 50, 200, 200),
            "2020,50,200,200", Describe(r2));

        // 副屏在主屏**上方** → 负 Y
        var above = new Rectangle(0, -1080, 1920, 2160);
        Rectangle r3 = AppController.ScreenToLocal(new Rectangle(10, -1000, 100, 100), above);
        Check("负 Y 原点", r3 == new Rectangle(10, 80, 100, 100), "10,80,100,100", Describe(r3));

        // 原点为零（单屏）—— 这条正是"写死 0 也能过"的情形，所以它**不能**单独构成证据
        var single = new Rectangle(0, 0, 1920, 1080);
        Rectangle r4 = AppController.ScreenToLocal(new Rectangle(5, 6, 7, 8), single);
        Check("零原点（单屏）：结果与入参一致", r4 == new Rectangle(5, 6, 7, 8), "5,6,7,8", Describe(r4));

        // 往返不变量：local + 原点 == physical
        Rectangle back = new Rectangle(
            r1.X + twoMonitors.X, r1.Y + twoMonitors.Y, r1.Width, r1.Height);
        Check("往返不变量：ScreenToLocal(physical) + 原点 == physical",
            back == new Rectangle(-800, 300, 420, 420),
            "-800,300,420,420", Describe(back));

        // 选区恰好等于整个虚拟桌面 → 局部坐标必为 (0,0,W,H)
        Rectangle full = AppController.ScreenToLocal(twoMonitors, twoMonitors);
        Check("选区 == 整个虚拟桌面 → 局部 (0,0,W,H)",
            full == new Rectangle(0, 0, 3840, 1080), "0,0,3840,1080", Describe(full));

        // 两个轴都必须用**自己的**原点分量。X/Y 互换是最容易写错、且在方形选区上
        // 完全看不出来的变异，所以给一组 X/Y 不对称的数据。
        var asymmetric = new Rectangle(-700, -1100, 4000, 2000);
        Rectangle r5 = AppController.ScreenToLocal(new Rectangle(-650, -1050, 30, 40), asymmetric);
        Check("X/Y 原点分量不得互换（用不对称原点与不对称尺寸）",
            r5 == new Rectangle(50, 50, 30, 40), "50,50,30,40", Describe(r5));
    }

    // ══════════════════════════════════════════════════════════════════════
    // §2 资源纪律：四条退出路径
    // ══════════════════════════════════════════════════════════════════════

    private static void SectionResourceDiscipline(Harness h)
    {
        // ── 路径 1：取消（用户按 Esc / 右键）──
        ScanObservation cancel = h.Scan(new ScanOptions
        {
            MakeFrozen = BlankFrozen,
            OnOverlayReady = (overlay, _) => overlay.Close(),
        });

        Check("取消路径：冻结位图被释放", IsDisposed(Single(cancel.Frozen)), "true",
            IsDisposed(Single(cancel.Frozen)).ToString());
        Check("取消路径：遮罩被释放", Single(cancel.Overlays).IsDisposed, "true",
            Single(cancel.Overlays).IsDisposed.ToString());
        Check("取消路径：没有任何解码回调", cancel.CallbackPayloads.Count == 0, "0", cancel.CallbackPayloads.Count.ToString());

        // ── 路径 2：无码 ──
        ScanObservation noCode = h.Scan(new ScanOptions
        {
            MakeFrozen = BlankFrozen,
            OnOverlayReady = (overlay, _) => DragOnOverlay(overlay, new Point(10, 10), new Point(110, 110)),
        });

        Check("无码路径：冻结位图被释放", IsDisposed(Single(noCode.Frozen)), "true",
            IsDisposed(Single(noCode.Frozen)).ToString());
        Check("无码路径：遮罩被释放", Single(noCode.Overlays).IsDisposed, "true",
            Single(noCode.Overlays).IsDisposed.ToString());
        Check("无码路径：没有任何解码回调", noCode.CallbackPayloads.Count == 0, "0", noCode.CallbackPayloads.Count.ToString());

        // ── 路径 3：成功 ──
        ScanObservation success = h.Scan(new ScanOptions
        {
            MakeFrozen = QrFrozen,
            OnOverlayReady = (overlay, _) => DragOnOverlay(overlay, new Point(90, 90), new Point(374, 374)),
        });

        Check("成功路径：冻结位图被释放", IsDisposed(Single(success.Frozen)), "true",
            IsDisposed(Single(success.Frozen)).ToString());
        Check("成功路径：遮罩被释放", Single(success.Overlays).IsDisposed, "true",
            Single(success.Overlays).IsDisposed.ToString());

        // ── 路径 4a：截屏阶段抛 ScreenCaptureFailedException（frozen 从未分配）──
        ScanObservation captureFailed = h.Scan(new ScanOptions
        {
            CaptureThrows = new ScreenCaptureFailedException("截屏失败，请重试。"),
        });
        Check("异常路径（截屏抛 ScreenCaptureFailedException）：气泡标题为「截屏失败」",
            captureFailed.BalloonTitle == "截屏失败", "截屏失败", captureFailed.BalloonTitle ?? "(null)");
        Check("异常路径（截屏抛 ScreenCaptureFailedException）：没有创建遮罩",
            captureFailed.FactoryCalls == 0, "0", captureFailed.FactoryCalls.ToString());

        // ── 路径 4b：截屏阶段抛 InvalidOperationException（更正 5：必须归一到"截屏失败"）──
        ScanObservation invalidOp = h.Scan(new ScanOptions
        {
            CaptureThrows = new InvalidOperationException("无法确定虚拟桌面尺寸。"),
        });
        Check("异常路径（截屏抛 InvalidOperationException）：**必须**呈现为「截屏失败」，不是「扫码失败」",
            invalidOp.BalloonTitle == "截屏失败", "截屏失败", invalidOp.BalloonTitle ?? "(null)");

        // ── 路径 4c：截屏成功之后才抛异常（遮罩工厂抛）→ frozen 已被分配，finally 必须释放它 ──
        ScanObservation afterCapture = h.Scan(new ScanOptions
        {
            MakeFrozen = BlankFrozen,
            FactoryThrows = true,
        });
        Check("异常路径（截屏成功后工厂抛）：冻结位图**仍**被 finally 释放",
            IsDisposed(Single(afterCapture.Frozen)), "true",
            IsDisposed(Single(afterCapture.Frozen)).ToString());
        Check("异常路径（截屏成功后工厂抛）：气泡标题为「扫码失败」（非截屏阶段）",
            afterCapture.BalloonTitle == "扫码失败", "扫码失败", afterCapture.BalloonTitle ?? "(null)");
        Check("异常路径（截屏成功后工厂抛）：没有遮罩被创建", afterCapture.Overlays.Count == 0, "0",
            afterCapture.Overlays.Count.ToString());
    }

    // ══════════════════════════════════════════════════════════════════════
    // §3 状态机
    // ══════════════════════════════════════════════════════════════════════

    private static void SectionStateMachine(Harness h)
    {
        // 规则 1：Capturing 期间再按热键 = 取消（**不是**叠加第二层遮罩）
        ScanObservation second = h.Scan(new ScanOptions
        {
            MakeFrozen = BlankFrozen,
            // 遮罩一就绪，就在模态消息泵里再触发一次热键 —— 走真实的 OnHotkey 分支
            OnOverlayReady = (_, controller) => InvokePrivate(controller, "OnHotkey"),
        });

        Check("Capturing 中再按热键：工厂只被调用 1 次（没有叠加第二层遮罩）",
            second.FactoryCalls == 1, "1", second.FactoryCalls.ToString());
        Check("Capturing 中再按热键：第一个遮罩被关闭并释放",
            Single(second.Overlays).IsDisposed, "true", Single(second.Overlays).IsDisposed.ToString());
        Check("Capturing 中再按热键：按「取消」处理（没有选中区域 → 没有任何解码回调）",
            second.CallbackPayloads.Count == 0, "0", second.CallbackPayloads.Count.ToString());
        Check("Capturing 中再按热键：冻结位图仍被释放",
            IsDisposed(Single(second.Frozen)), "true", IsDisposed(Single(second.Frozen)).ToString());

        // 规则 2：Decoding / Presenting 期间再按热键 = 忽略（禁止重入原生调用）
        // 说明：这两个状态在同步解码下**观察不到**（UI 线程正忙）。这里用反射把状态
        // 直接置过去再驱动真实的 OnHotkey —— 验的是那两条分支本身，不是它们的可达性。
        foreach (AppState state in new[] { AppState.Decoding, AppState.Presenting })
        {
            // 同时给上素材与一个会关掉遮罩的回调：若某个变异把这两条分支改成「又开始
            // 一次框选」，探针会真的建出遮罩并把它关掉 —— 得到一条清晰的「FactoryCalls=1」
            // 而不是卡死在嵌套的 ShowDialog 里（那会变成「无法判定」，不是证据）。
            ScanObservation ignored = h.Scan(new ScanOptions
            {
                ForceState = state,
                MakeFrozen = BlankFrozen,
                OnOverlayReady = (overlay, _) => overlay.Close(),
            });

            Check($"{state} 中再按热键：被忽略（工厂零调用、无解码回调、无异常）",
                ignored.FactoryCalls == 0 && ignored.Unhandled is null && ignored.Frozen.Count == 0,
                "FactoryCalls=0、Frozen=0、无异常",
                $"FactoryCalls={ignored.FactoryCalls}, Frozen={ignored.Frozen.Count}, "
                + $"Unhandled={Describe(ignored.Unhandled)}");
        }

        // 起点：Idle + 热键 = 开始一次框选
        ScanObservation fromIdle = h.Scan(new ScanOptions
        {
            MakeFrozen = BlankFrozen,
            OnOverlayReady = (overlay, _) => overlay.Close(),
        });
        Check("Idle + 热键：确实开始了框选（工厂被调用 1 次）",
            fromIdle.FactoryCalls == 1, "1", fromIdle.FactoryCalls.ToString());
    }

    // ══════════════════════════════════════════════════════════════════════
    // §4 端到端
    // ══════════════════════════════════════════════════════════════════════

    private static void SectionEndToEnd(Harness h)
    {
        // 这一条同时是**正向换算是反向换算的逆运算**的端到端证明：
        // 拖动发生在遮罩客户区坐标 (90,90)→(374,374)，
        // OverlayWindow 加上负原点得到屏幕物理坐标，
        // AppController 再减回负原点得到位图局部坐标，
        // 而二维码**恰好**画在位图局部 (100,100) 处 ——
        // 任何一处原点算错，解码就会落空。
        ScanObservation success = h.Scan(new ScanOptions
        {
            MakeFrozen = QrFrozen,
            OnOverlayReady = (overlay, _) => DragOnOverlay(overlay, new Point(90, 90), new Point(374, 374)),
        });

        Check("端到端：负原点下坐标往返正确 → 解码结果（经回调观察）恰好 1 个码",
            success.CallbackPayloads.Count == 1, "1", success.CallbackPayloads.Count.ToString());
        Check("端到端：回调到的文本正确（真实 QrDecoder，非桩）",
            success.CallbackPayloads.Count == 1 && success.CallbackPayloads[0].Text == Harness.ExpectedUrl,
            Harness.ExpectedUrl,
            success.CallbackPayloads.Count == 1 ? success.CallbackPayloads[0].Text : "(无)");
        Check("端到端：剪贴板收到了同一个 URL",
            success.Clipboard.Count == 1 && success.Clipboard[0] == Harness.ExpectedUrl,
            Harness.ExpectedUrl,
            success.Clipboard.Count == 1 ? success.Clipboard[0] : $"({success.Clipboard.Count} 次写入)");
        Check("端到端：提示条正文含该 URL",
            success.ToastMessage?.Contains(Harness.ExpectedUrl, StringComparison.Ordinal) == true,
            "含 URL", success.ToastMessage ?? "(无提示条)");

        // 无码路径的可见反馈
        ScanObservation noCode = h.Scan(new ScanOptions
        {
            MakeFrozen = BlankFrozen,
            OnOverlayReady = (overlay, _) => DragOnOverlay(overlay, new Point(10, 10), new Point(110, 110)),
        });
        Check("无码：提示条正文为「未识别到二维码…」（不静默）",
            noCode.ToastMessage?.StartsWith("未识别到二维码", StringComparison.Ordinal) == true,
            "以「未识别到二维码」开头", noCode.ToastMessage ?? "(无提示条)");
        Check("无码：没有写剪贴板（不误复制）", noCode.Clipboard.Count == 0, "0",
            noCode.Clipboard.Count.ToString());

        // 取消路径不得碰剪贴板
        ScanObservation cancelled = h.Scan(new ScanOptions
        {
            MakeFrozen = BlankFrozen,
            OnOverlayReady = (overlay, _) => overlay.Close(),
        });
        Check("取消：没有写剪贴板", cancelled.Clipboard.Count == 0, "0", cancelled.Clipboard.Count.ToString());
        Check("取消：没有弹提示条", cancelled.ToastMessage is null, "(无)", cancelled.ToastMessage ?? "(无)");

        // 托盘「最近 N 条」的**输入**：`AppController` 对每个解出的码回调一次。
        // 注意：`TrayApplicationContext.AddRecent` 的插入置顶与超限截断**不在本探针覆盖范围内**
        // （它需要真的构造 TrayApplicationContext，而那个构造函数会读配置、写配置、
        //  抢真实热键、创建真托盘图标 —— 副作用太重，不适合放进探针）。
        // 所以这里只断言"回调次数正确"，不断言列表状态。
        ScanObservation once = h.Scan(new ScanOptions
        {
            MakeFrozen = QrFrozen,
            OnOverlayReady = (overlay, _) => DragOnOverlay(overlay, new Point(90, 90), new Point(374, 374)),
        });
        Check("成功识别：每个解出的码恰好回调一次 _onPayloadDecoded（托盘「最近 N 条」的输入）",
            once.CallbackPayloads.Count == 1, "1", once.CallbackPayloads.Count.ToString());
    }

    // ══════════════════════════════════════════════════════════════════════
    // §5 接线与顺序 —— 源码文本检查
    // ══════════════════════════════════════════════════════════════════════

    private static void SectionWiringTextChecks()
    {
        Console.WriteLine("  （以下全部是**对源码文本**的检查，不是行为验证；标注为 [文本]）");

        string app = ReadSource("src/QrScan/AppController.cs");
        string ctx = ReadSource("src/QrScan/TrayApplicationContext.cs");
        string tray = ReadSource("src/QrScan/UI/TrayHost.cs");

        // ★ 先去掉注释再检查。
        //   不去注释的话，「代码里不再有 X」这类断言会被**自己写的解释性注释**误伤
        //   —— 本探针第一版就栽在这里：注释里为了说明「旧写法已被替换」而提到
        //   Clipboard.SetDataObject，断言就变红了。注释不能执行，所以正确的语义
        //   就是在剥掉注释的源码上匹配。
        string appCode = StripComments(app);
        string ctxCode = StripComments(ctx);
        string trayCode = StripComments(tray);

        // 更正 2：临时调试脚手架必须彻底消失
        Check("[文本] src/ 下不再有 DebugCapture / RunCaptureProbe（更正 2）",
            !appCode.Contains("DebugCapture", StringComparison.Ordinal)
            && !ctxCode.Contains("DebugCapture", StringComparison.Ordinal)
            && !trayCode.Contains("DebugCapture", StringComparison.Ordinal)
            && !ctxCode.Contains("RunCaptureProbe", StringComparison.Ordinal)
            && !trayCode.Contains("RunCaptureProbe", StringComparison.Ordinal),
            "零命中", "有命中");

        Check("[文本] TrayHost 不再包含「调试：」菜单项",
            !trayCode.Contains("调试：", StringComparison.Ordinal), "零命中", "有命中");

        // 更正 1：零参数 Action? 的订阅写法
        Check("[文本] QuitRequested 用 () => 订阅（更正 1：零参数 Action?）",
            ctxCode.Contains("_tray.QuitRequested += () => ExitThread();", StringComparison.Ordinal),
            "_tray.QuitRequested += () => ExitThread();", "未按预期出现");
        Check("[文本] 不存在 (_, _) => 订阅零参数事件的写法（更正 1）",
            !ctxCode.Contains("QuitRequested += (_, _)", StringComparison.Ordinal)
            && !ctxCode.Contains("ScanRequested += (_, _)", StringComparison.Ordinal)
            && !appCode.Contains("Pressed += (_, _)", StringComparison.Ordinal)
            && !appCode.Contains("ScanRequested += (_, _)", StringComparison.Ordinal),
            "零命中", "有命中");

        // 更正 3：托盘最近记录改走带重试的写入
        Check("[文本] RecentItemChosen 接到 ResultPresenter.TrySetClipboardText（更正 3）",
            ctxCode.Contains("ResultPresenter.TrySetClipboardText(text)", StringComparison.Ordinal),
            "ResultPresenter.TrySetClipboardText(text)", "未按预期出现");
        Check("[文本] 不再有裸 Clipboard.SetDataObject（更正 3：无重试会走到未处理异常）",
            !ctxCode.Contains("Clipboard.SetDataObject", StringComparison.Ordinal), "零命中", "有命中");
        Check("[文本] 托盘复制失败有可见反馈（全局约束：绝不静默失败）",
            ctxCode.Contains("_tray.ShowBalloon(\"复制失败\"", StringComparison.Ordinal),
            "ShowBalloon(\"复制失败\"…)", "未按预期出现");

        // 更正 4：热键失败除气泡外还有自绘提示条
        Check("[文本] 热键失败除气泡外还走自绘 ToastWindow（更正 4）",
            ctxCode.Contains("presenter.ShowMessage(reason, ToastKind.Failure", StringComparison.Ordinal),
            "presenter.ShowMessage(reason, ToastKind.Failure, …)", "未按预期出现");
        Check("[文本] 热键失败的提示条锚点不取自指针位置",
            ctxCode.Contains("StartupToastAnchor()", StringComparison.Ordinal)
            && !ctxCode.Contains("Cursor.Position", StringComparison.Ordinal),
            "使用 StartupToastAnchor() 且不读 Cursor.Position", "未按预期出现");

        // 更正 5：截屏阶段的两类异常都归一为「截屏失败」
        Check("[文本] 更正的实现：把截屏的 InvalidOperationException 归一为 ScreenCaptureFailedException（更正 5）",
            appCode.Contains("catch (InvalidOperationException ex)", StringComparison.Ordinal)
            && appCode.Contains("throw new ScreenCaptureFailedException(", StringComparison.Ordinal),
            "catch (InvalidOperationException) → throw ScreenCaptureFailedException", "未按预期出现");

        // 更正 6：首次落盘必须在创建 HotkeyManager 之前
        int saveAt = ctxCode.IndexOf("configStore.Save(loaded.Config)", StringComparison.Ordinal);
        int hotkeyAt = ctxCode.IndexOf("new HotkeyManager(", StringComparison.Ordinal);
        Check("[文本] 首次落盘在创建 HotkeyManager **之前**（更正 6：失败消息里的路径须已存在）",
            saveAt >= 0 && hotkeyAt >= 0 && saveAt < hotkeyAt,
            "Save 的下标 < HotkeyManager 的下标",
            $"saveAt={saveAt}, hotkeyAt={hotkeyAt}");

        // 顺序不可交换：截屏必须在遮罩 Show 之前
        int captureAt = appCode.IndexOf("frozen = CaptureOrThrow();", StringComparison.Ordinal);
        int showAt = appCode.IndexOf("overlay.ShowDialog();", StringComparison.Ordinal);
        int factoryAt = appCode.IndexOf("_overlayFactory(frozen, bounds);", StringComparison.Ordinal);
        Check("[文本] 顺序：截屏 → 造遮罩 → ShowDialog（不可交换，否则遮罩会把自己截进去）",
            captureAt >= 0 && factoryAt >= 0 && showAt >= 0 && captureAt < factoryAt && factoryAt < showAt,
            "capture < factory < ShowDialog",
            $"captureAt={captureAt}, factoryAt={factoryAt}, showAt={showAt}");

        // 唯一的反向换算必须存在，且唯一换算点不得出现裸 0 原点
        Check("[文本] 只存在一处 ScreenToLocal 定义，且用的是原点分量而不是裸 0",
            CountOccurrences(appCode, "internal static Rectangle ScreenToLocal") == 1
            && appCode.Contains("physical.X - virtualBounds.X", StringComparison.Ordinal)
            && appCode.Contains("physical.Y - virtualBounds.Y", StringComparison.Ordinal),
            "1 处定义 + 两个原点分量都在",
            $"定义 {CountOccurrences(appCode, "internal static Rectangle ScreenToLocal")} 处");

        // 四条路径共用一个 finally
        Check("[文本] 释放写在 finally 里（四条路径共用，而不是「成功路径顺手释放」）",
            appCode.Contains("overlay?.Dispose();", StringComparison.Ordinal)
            && appCode.Contains("frozen?.Dispose();", StringComparison.Ordinal)
            && appCode.Contains("finally", StringComparison.Ordinal),
            "finally 内同时释放 overlay 与 frozen", "未按预期出现");
    }

    // ══════════════════════════════════════════════════════════════════════
    // 场景执行器
    // ══════════════════════════════════════════════════════════════════════

    private sealed record ScanOptions
    {
        public Rectangle Bounds { get; init; } = VirtualBounds;
        public Func<Bitmap>? MakeFrozen { get; init; }
        public Exception? CaptureThrows { get; init; }
        public bool FactoryThrows { get; init; }
        public Action<OverlayWindow, AppController>? OnOverlayReady { get; init; }
        public AppState? ForceState { get; init; }
    }

    private sealed record ScanObservation(
        List<Bitmap> Frozen,
        List<OverlayWindow> Overlays,
        // 解码结果**唯一**的观察窗口：AppController 对每个解出的码回调一次，
        // 探针把回调收集起来。命名刻意点明它是回调产物 —— 否则
        // 「解出恰好 1 个码」这类断言在被 noTrayCallback 变异打红时会被
        // 误读成「解码坏了」。
        List<QrPayload> CallbackPayloads,
        int FactoryCalls,
        List<string> Clipboard,
        string? ToastMessage,
        string? BalloonTitle,
        Exception? Unhandled);

    private sealed class Harness : IDisposable
    {
        internal const string ExpectedUrl = "https://example.com/hello?from=qrscan";

        private readonly TrayHost _tray;
        private readonly List<string> _clipboard = new();
        private readonly Action<string> _originalClipboardWrite;
        private readonly Func<Point> _originalPointerSource;

        public Harness()
        {
            // 剪贴板与指针位置是**环境依赖**，必须换成确定性的，否则探针会随
            // "用户当前有没有锁剪贴板 / 鼠标在哪儿"而飘。
            _originalClipboardWrite = ResultPresenter.ClipboardWrite;
            _originalPointerSource = ToastWindow.PointerPositionSource;

            ResultPresenter.ClipboardWrite = text => _clipboard.Add(text);
            ToastWindow.PointerPositionSource = () => new Point(-10000, -10000);   // 钉在屏幕外

            // HotkeyManager 必须能构造但不抢真实热键：空 spec 会让解析失败 →
            // IsRegistered=false、不调用 RegisterHotKey。Pressed 永不触发，
            // 场景里由反射直接驱动 AppController.OnHotkey。
            _tray = new TrayHost(new StartupManager(), "Ctrl+Shift+Q");
        }

        public ScanObservation Scan(ScanOptions options)
        {
            var frozen = new List<Bitmap>();
            var overlays = new List<OverlayWindow>();
            var decoded = new List<QrPayload>();
            int factoryCalls = 0;
            Exception? unhandled = null;

            _clipboard.Clear();
            SetBalloon("(未设置)", "(未设置)");

            var capture = new FakeCapture(options.Bounds, () =>
            {
                Bitmap bitmap = options.MakeFrozen!();
                frozen.Add(bitmap);
                return bitmap;
            }, options.CaptureThrows);

            ResultPresenter presenter = new(new FakeLauncher(), dismissMs: 60_000);
            using HotkeyManager hotkey = new(string.Empty);

            AppController? controller = null;

            Func<Bitmap, Rectangle, OverlayWindow> factory = (bitmap, bounds) =>
            {
                factoryCalls++;
                if (options.FactoryThrows)
                    throw new InvalidOperationException("(probe) 遮罩工厂故意抛异常");

                var overlay = new OverlayWindow(bitmap, bounds);
                overlays.Add(overlay);

                if (options.OnOverlayReady is { } act)
                {
                    // WinForms 的 Timer 把 WM_TIMER 投给本线程消息队列，而 ShowDialog
                    // 正在泵那个队列 —— 于是这个回调会在**模态循环内部**执行，
                    // 那正是"用户在框选期间做出动作"的位置。
                    var timer = new Timer { Interval = 30 };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        try
                        {
                            act(overlay, controller!);
                        }
                        catch (Exception ex)
                        {
                            unhandled = ex;
                            if (!overlay.IsDisposed) overlay.Close();
                        }
                    };
                    timer.Start();
                }

                return overlay;
            };

            controller = new AppController(
                capture: capture,
                decoder: new QrDecoder(),
                presenter: presenter,
                hotkey: hotkey,
                tray: _tray,
                overlayFactory: factory,
                onPayloadDecoded: decoded.Add);

            try
            {
                if (options.ForceState is { } state)
                    SetState(controller, state);

                InvokePrivate(controller, "OnHotkey");
            }
            catch (TargetInvocationException ex)
            {
                // 把反射层的包装异常剥掉：包装异常是探针的伪影，内层才是产品异常。
                // （任务 11 的探针最初把 TargetInvocationException 记成 PASS，
                //   于是"取消路径通过"里混着"Close() 抛了异常"。）
                unhandled ??= ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                unhandled ??= ex;
            }

            string? toastMessage = ActiveToastMessage(presenter);
            string? balloonTitle = LastBalloonTitle();

            // 关掉还开着的提示条，免得污染下一个场景
            CloseActiveToast(presenter);
            controller.Dispose();

            return new ScanObservation(
                frozen,
                overlays,
                decoded,
                factoryCalls,
                new List<string>(_clipboard),
                toastMessage,
                balloonTitle,
                unhandled);
        }

        public void Dispose()
        {
            ResultPresenter.ClipboardWrite = _originalClipboardWrite;
            ToastWindow.PointerPositionSource = _originalPointerSource;
            _tray.Dispose();
        }

        private void SetBalloon(string title, string text)
        {
            NotifyIcon icon = IconOf(_tray);
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = text;
        }

        private string? LastBalloonTitle() => IconOf(_tray).BalloonTipTitle;

        private static NotifyIcon IconOf(TrayHost tray) =>
            (NotifyIcon)typeof(TrayHost)
                .GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(tray)!;

        private static string? ActiveToastMessage(ResultPresenter presenter)
        {
            var active = (ToastWindow?)typeof(ResultPresenter)
                .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(presenter);

            return active?.Message;
        }

        private static void CloseActiveToast(ResultPresenter presenter)
        {
            var active = (ToastWindow?)typeof(ResultPresenter)
                .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(presenter);

            if (active is { IsDisposed: false })
                active.Close();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 替身（只替换环境依赖）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 位图是否已被释放。
    ///
    /// 为什么不用"派生一个能在 Dispose(bool) 上留痕的子类"：<c>Bitmap</c> 是 **sealed**
    /// （实测 CS0509），派生不了。改为在事后探测 —— 已释放的 <c>Image</c> 访问
    /// <c>Width</c> 会抛 <see cref="ArgumentException"/>，而未释放的会正常返回宽度
    /// （两种情形都已实测，不是推测）。
    ///
    /// 这正是"资源有没有被释放"这件事在**真实调用点**上的可观察后果，
    /// 而不是探针自己插的钩子。
    /// </summary>
    private static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.Width;
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class FakeCapture : IScreenCapture
    {
        private readonly Rectangle _bounds;
        private readonly Func<Bitmap> _make;
        private readonly Exception? _throws;

        public FakeCapture(Rectangle bounds, Func<Bitmap> make, Exception? throws)
        {
            _bounds = bounds;
            _make = make;
            _throws = throws;
        }

        public Rectangle VirtualScreenPhysical() => _bounds;

        public Bitmap CaptureVirtualScreen() =>
            _throws is not null ? throw _throws : _make();
    }

    private sealed class FakeLauncher : IShellLauncher
    {
        public List<string> Opens { get; } = new();

        public void Open(string target) => Opens.Add(target);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 场景素材
    // ══════════════════════════════════════════════════════════════════════

    private static Bitmap BlankFrozen()
    {
        var bitmap = new Bitmap(800, 600, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        return bitmap;
    }

    private static Bitmap QrFrozen()
    {
        var bitmap = new Bitmap(800, 600, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);

        // 用仓库里真实的测试样张（T1 生成、UnitTests 已断言过内容），
        // 于是"解出来的是什么"这件事不依赖本探针自己造码的能力。
        using Bitmap qr = LoadAsset("standard.png");
        g.DrawImage(qr, new Rectangle(QrLeft, QrTop, qr.Width, qr.Height));

        return bitmap;
    }

    private static Bitmap LoadAsset(string name)
    {
        var assembly = typeof(Program).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        // 复制一份：流一关，GDI+ 对 stream-backed 位图的后续操作会抛 OutOfMemoryException
        // （任务 1 踩过的坑），而这里之后还要在多个场景里反复 DrawImage 它。
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 驱动辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 反射调用 <see cref="OverlayWindow"/> 的三个 protected 鼠标处理器，模拟一次真实的框选。
    ///
    /// 这是本探针唯一"绕过 UI"的手法：会话锁定、<c>Cursor.Position</c> 只读，
    /// 无法真的拖鼠标。驱动的**是产品代码本体**（Normalize → 加负原点 → 赋 SelectedRegionPhysical
    /// → Close），只是把"鼠标事件"这一层的来源换掉了。
    /// </summary>
    private static void DragOnOverlay(OverlayWindow overlay, Point from, Point to)
    {
        InvokeMouse(overlay, "OnMouseDown", from);
        InvokeMouse(overlay, "OnMouseMove", to);
        InvokeMouse(overlay, "OnMouseUp", to);
    }

    private static void InvokeMouse(OverlayWindow overlay, string method, Point at)
    {
        MethodInfo mi = typeof(OverlayWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        mi.Invoke(overlay, new object[] { new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0) });
    }

    private static void InvokePrivate(object target, string method)
    {
        MethodInfo mi = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        mi.Invoke(target, null);
    }

    private static void SetState(AppController controller, AppState state) =>
        typeof(AppController)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, state);

    // ══════════════════════════════════════════════════════════════════════
    // 断言与汇总
    // ══════════════════════════════════════════════════════════════════════

    private static void Section(string title, Action body)
    {
        Console.WriteLine(title);

        try
        {
            body();
        }
        catch (TargetInvocationException ex)
        {
            Check($"{title} —— 整节未抛异常", false, "无异常", Describe(ex.InnerException ?? ex));
        }
        catch (Exception ex)
        {
            Check($"{title} —— 整节未抛异常", false, "无异常", Describe(ex));
        }

        Console.WriteLine();
    }

    private static void Check(string name, bool ok, string expected, string actual)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Failures.Add($"{name}（期望 = {expected}，实测 = {actual}）");
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"         期望 = {expected}");
            Console.WriteLine($"         实测 = {actual}");
        }
    }

    private static int Report()
    {
        int executed = _passed + _failed;

        // ★ 闸门必须在打印汇总行**之前**结算，并且它的失败要计入那一行。
        //   本探针第一版把闸门放在汇总行之后、另打一行「重算：…」，于是
        //   run-mutations.sh 解析 `^===== 结果：` 的 tail -1 拿到的仍是「0 失败」——
        //   闸门确实响了，但判定没能传到机器可读的那一行上。变异复核当场把这条
        //   自欺链抓了出来（deletedSection 逃逸），这就是它存在的意义。
        if (executed != ExpectedAssertions)
        {
            _failed++;
            Failures.Add($"断言总数 == ExpectedAssertions（期望 {ExpectedAssertions}，实测 {executed}）");
            Console.WriteLine("  [FAIL] 断言总数 == ExpectedAssertions（防「整节被删」而仍然全绿）");
            Console.WriteLine($"         期望 = {ExpectedAssertions}");
            Console.WriteLine($"         实测 = {executed}");
        }

        int total = _passed + _failed;

        Console.WriteLine("========================================");
        Console.WriteLine($"===== 结果：{_passed} 通过 / {_failed} 失败（共 {total} 项断言）=====");

        if (_failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (string failure in Failures)
                Console.WriteLine($"  - {failure}");

            Console.WriteLine("退出码 1（有断言失败）");
            return 1;
        }

        Console.WriteLine("退出码 0（全绿）");
        return 0;
    }

    private static void StartWatchdog()
    {
        // 探针会在 ShowDialog 的模态循环里等一个 Timer 回调。若那个回调因为任何原因
        // 没到达，探针会永远挂住，于是"跑不动"看起来就像"还在跑"。给它一个硬上限。
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(90));
            Console.Error.WriteLine("!!! 探针超过 90 秒未结束 —— 疑似卡在某个 ShowDialog 的模态循环里");
            Environment.Exit(3);
        })
        { IsBackground = true };

        watchdog.Start();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 小工具
    // ══════════════════════════════════════════════════════════════════════

    private static T Single<T>(IReadOnlyList<T> items) =>
        items.Count == 1
            ? items[0]
            : throw new InvalidOperationException($"期望恰好 1 个元素，实际 {items.Count} 个");

    private static string Describe(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    private static string Describe(Exception? ex) =>
        ex is null ? "(无异常)" : $"{ex.GetType().Name}: {ex.Message}";

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private enum ScanMode
    {
        Code,
        String,
        Verbatim,
        Char,
    }

    /// <summary>
    /// 剥掉 C# 注释，但**保留字符串字面量**（否则 <c>"https://…"</c> 会被当成行注释切开）。
    ///
    /// §5 全部是「代码里是否还有 X」这类断言，而不去注释的匹配会被**解释性注释**误伤 ——
    /// 本探针第一版就栽在这里：注释里说明「旧写法已被替换」时提到了旧 API 名，断言就红了。
    /// 注释不可能执行，所以正确的语义是在剥掉注释的源码上匹配。
    /// </summary>
    private static string StripComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        ScanMode mode = ScanMode.Code;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            switch (mode)
            {
                case ScanMode.Code:
                    if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        output.Append("@\"");
                        i++;
                        mode = ScanMode.Verbatim;
                        continue;
                    }

                    if (c == '"') { output.Append(c); mode = ScanMode.String; continue; }
                    if (c == '\'') { output.Append(c); mode = ScanMode.Char; continue; }

                    if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                    {
                        while (i < source.Length && source[i] != '\n') i++;
                        output.Append('\n');
                        continue;
                    }

                    if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                    {
                        i += 2;
                        while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                        if (i + 1 < source.Length) i++;
                        continue;
                    }

                    output.Append(c);
                    break;

                case ScanMode.String:
                    output.Append(c);
                    if (c == '\\' && i + 1 < source.Length) output.Append(source[++i]);
                    else if (c == '"') mode = ScanMode.Code;
                    break;

                case ScanMode.Verbatim:
                    output.Append(c);
                    if (c == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') output.Append(source[++i]);
                        else mode = ScanMode.Code;
                    }

                    break;

                case ScanMode.Char:
                    output.Append(c);
                    if (c == '\\' && i + 1 < source.Length) output.Append(source[++i]);
                    else if (c == '\'') mode = ScanMode.Code;
                    break;
            }
        }

        return output.ToString();
    }

    private static string ReadSource(string relativePath)
    {
        string? root = ResolveRepoRoot();

        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// 定位仓库根。
    ///
    /// 优先读环境变量 <c>QRSCAN_REPO_ROOT</c>：本探针会被 run-mutations.sh 在**临时目录**里
    /// 用一份变异副本重新构建并运行，而那时从程序目录向上是找不到 <c>QrScan.sln</c> 的
    /// （§5 当时直接抛异常 → 对照组变红，整个变异复核失去前提）。
    /// 直接从仓库里运行时，向上找 QrScan.sln 仍然是通的那条路。
    /// </summary>
    private static string ResolveRepoRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("QRSCAN_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot) && Directory.Exists(overrideRoot))
            return overrideRoot;

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QrScan.sln")))
            dir = dir.Parent;

        if (dir is null)
        {
            throw new InvalidOperationException(
                "找不到仓库根：既没有 QRSCAN_REPO_ROOT 环境变量，"
                + "也无法从程序目录向上找到 QrScan.sln。");
        }

        return dir.FullName;
    }
}
