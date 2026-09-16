using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using QrScan.Core;
using QrScan.Core.Models;
using QrScan.UI;

namespace ResultPresenterProbe;

/// <summary>
/// <see cref="ResultPresenter"/> 的常驻回归探针。
///
/// **为什么需要它**：这个类承担了四条"静默失效也照样跑"的不变量 ——
///
/// 1. **剪贴板重试 3 次 × 50 ms 且失败必须可见**。剪贴板被别的进程锁住是必现状况
///    （Office、远程桌面、剪贴板管理器）。一旦重试被删或失败分支被删，用户会以为
///    复制成功了，然后粘贴出**上一次的旧内容** —— 这是最难排查的一类 bug。
/// 2. **按钮矩阵**。`[打开]` 只在 <see cref="PayloadClassifier.CanOpen"/> 为真时出现。
///    矩阵写错的后果不是崩溃，而是"多一个点了没反应的按钮"或"少一个用户需要的按钮"。
/// 3. **「打开」的安全边界**。二维码内容来自不可信来源；本类是
///    <see cref="IShellLauncher.Open"/> 的**唯一调用方**，所以"自定义 scheme 绝不
///    触达启动器"这条断言只有在这里才成立。这是本探针最有价值的一条。
/// 4. **多码切换**：`(index + 1) % Count` 且切换后剪贴板、打开目标、按钮组三者同步。
///
/// **手法**：csproj 用 <c>&lt;Compile Include&gt;</c> 把仓库里**真实的**
/// `ResultPresenter.cs` 与 `ToastWindow.cs` 编进本程序集（不是副本 —— 副本会漂移），
/// 于是 `internal` 类型在这里可见，且不需要给仓库加 `InternalsVisibleTo`。
///
/// 剪贴板通过 <see cref="ResultPresenter.ClipboardWrite"/> 这个 internal 接缝注入，
/// 因此**不需要真的去抢剪贴板**、也不会污染用户的剪贴板内容；点击按钮走的是真实的
/// `ToastWindow.OnMouseDown` 命中测试路径，不是直接调私有方法。
///
/// **不需要可见桌面**：全程不依赖屏幕可见性，锁屏或无人值守环境下同样可跑。
///
/// 任一项失败时进程退出码为 1，可直接被脚本/CI 当检查用。
/// </summary>
internal static class Program
{
    private const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>
    /// 提示条不应在探针断言期间自动消失；用一个远大于探针时长的值。
    /// （淡出与悬停暂停由 ToastWindowProbe 负责，这里只关心 ResultPresenter 的行为。）
    /// </summary>
    private const int LongDismissMs = 60_000;

    /// <summary>
    /// 断言总数的预期值。**任何一次增删断言都必须同步这个数字。**
    ///
    /// 为什么需要它：退出码只由 `_fail` 决定，而 `_pass` 只被打印 —— 于是
    /// "删掉某一整节的 `Section(...)` 调用"会让那一节的证据**静默消失**，
    /// 剩余项全绿、退出码仍是 0。有了这道闸门，"删节 / 删断言"会直接变成退出码 1。
    /// （任务 11 与任务 12 的探针都因为缺这道闸门被审查者指出过。）
    /// </summary>
    private const int ExpectedAssertions = 88;

    // ── 测试用文本 ────────────────────────────────────────────────────────────
    private const string UrlA = "https://example.com/hello?from=qrscan";
    private const string UrlB = "https://example.org/second-code";
    private const string Plain = "hello world, this is plain text";
    private const string Wifi = "WIFI:T:WPA;S:mynet;P:s3cret;;";

    /// <summary>自定义协议 scheme —— 会被交给系统协议处理器，绝不能被交给 ShellExecute。</summary>
    private const string CustomScheme = "ms-msdt:/id PCWDiagnostic";

    private const string FileScheme = "file:///C:/Windows/System32/calc.exe";

    private static int _pass;
    private static int _fail;

    /// <summary>
    /// 实际执行过的断言总数。单独计数是为了让"断言被删"与"断言失败"可区分 ——
    /// 拿 `_pass` 去比 ExpectedAssertions 会把任何一次真实失败都误报成"总数不符"。
    /// </summary>
    private static int _assertions;

    private static readonly List<(string Title, int Count)> SectionCounts = new();
    private static readonly List<string> Lines = new();
    private static readonly List<string> Notes = new();
    private static readonly List<string> InvokeErrors = new();

    /// <summary>§8 断言那一刻的 <see cref="InvokeErrors"/> 计数，用于捕获它之后新增的异常。</summary>
    private static int _invokeErrorsAtSection8 = -1;

    // ── 剪贴板模拟 ────────────────────────────────────────────────────────────
    private static int _writeAttempts;
    private static string? _lastWritten;
    private static int _failRemaining;
    private static bool _alwaysFail;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("===== ResultPresenterProbe：ResultPresenter 常驻回归探针 =====");
        Console.WriteLine($"虚拟桌面 = {Show(SystemInformation.VirtualScreen)}");
        Console.WriteLine($"提示条 dismissMs = {LongDismissMs}（远大于探针时长，避免断言期间自动消失）");
        Console.WriteLine("剪贴板：已替换为探针模拟实现（不触碰真实剪贴板）");
        Console.WriteLine();

        InstallClipboard(failFirst: 0);

        Section("1. 构造与常量", CheckConstructor);
        Section("2. 剪贴板重试 3 次 × 50ms", CheckClipboardRetry);
        Section("3. 复制失败的可见反馈与重试按钮", CheckCopyFailure);
        Section("4. 按钮矩阵（四行组合逐字核对）", CheckButtonMatrix);
        Section("5. 多码切换 0→1→2→0", CheckMultiCodeCycling);
        Section("6. 安全边界：自定义 scheme 绝不触达启动器", CheckSecurityBoundary);
        Section("7. Replace 不泄漏 + ShowMessage + 打开失败", CheckReplaceAndMessages);
        Section("8. 异常即失败", CheckNoExceptions);
        Section("9. 变异灵敏度：错误实现必须被检出", CheckMutationSensitivity);
        Section("10. 报告项", ReportObservations);

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

    // ───────────────────── 1. 构造与常量 ─────────────────────

    private static void CheckConstructor()
    {
        var launcher = new FakeLauncher();
        var presenter = new ResultPresenter(launcher, LongDismissMs);

        Check("初始 _active == null（还没有提示条）",
            ActiveToast(presenter) is null, "null", ActiveToast(presenter) is null ? "null" : "有值");
        Check("_launcher 字段 == 传入的 launcher（未被换掉）",
            ReferenceEquals(ReadField<IShellLauncher?>(presenter, "_launcher"), launcher),
            "同一个实例", ReferenceEquals(ReadField<IShellLauncher?>(presenter, "_launcher"), launcher) ? "同一个实例" : "不是");
        Check("_dismissMs 字段 == 传入值",
            ReadField<int>(presenter, "_dismissMs") == LongDismissMs,
            LongDismissMs.ToString(), ReadField<int>(presenter, "_dismissMs").ToString());

        Check("ClipboardRetryAttempts == 3", ReadConst("ClipboardRetryAttempts") == 3,
            "3", ReadConst("ClipboardRetryAttempts").ToString());
        Check("ClipboardRetryDelayMs == 50", ReadConst("ClipboardRetryDelayMs") == 50,
            "50", ReadConst("ClipboardRetryDelayMs").ToString());
        Check("MaxMessageLength == 120", ReadConst("MaxMessageLength") == 120,
            "120", ReadConst("MaxMessageLength").ToString());
    }

    // ───────────────────── 2. 剪贴板重试 ─────────────────────

    private static void CheckClipboardRetry()
    {
        // (a) 前 2 次抛 ExternalException、第 3 次成功 —— 模拟"剪贴板被占用后松开"
        InstallClipboard(failFirst: 2);
        bool ok = ResultPresenter.TrySetClipboardText(UrlA);

        Check("(a) 前 2 次失败、第 3 次成功 → 返回 true", ok, "True", ok.ToString());
        Check("(a) 尝试次数 == 3（确实重试了，不是一次就放弃）",
            _writeAttempts == 3, "3", _writeAttempts.ToString());
        Check("(a) 最终写入的内容 == 目标文本", _lastWritten == UrlA, UrlA, _lastWritten ?? "(null)");

        // (b) 3 次全失败 —— 同时测**机制**而不只是常量。
        // 若调用点被写成 Thread.Sleep(1) 而常量仍是 50，下面那条时序断言必须变红；
        // 只断言 "ClipboardRetryDelayMs == 50" 是抓不到这种漂移的。
        InstallClipboard(alwaysFail: true);
        var retryWatch = Stopwatch.StartNew();
        bool failed = ResultPresenter.TrySetClipboardText(UrlA);
        retryWatch.Stop();
        long retryElapsedMs = retryWatch.ElapsedMilliseconds;

        Check("(b) 3 次全失败 → 返回 false", !failed, "False", failed.ToString());
        Check("(b) 尝试次数 == 3（不是无限重试）", _writeAttempts == 3, "3", _writeAttempts.ToString());
        Check("(b) 3 次全失败路径的实测耗时 >= 90ms（退避真的走了两次 Thread.Sleep(50)）",
            retryElapsedMs >= 90, ">= 90ms", $"{retryElapsedMs}ms");

        // (c) 一次就成功 —— 不应有多余尝试
        InstallClipboard(failFirst: 0);
        bool first = ResultPresenter.TrySetClipboardText(UrlA);

        Check("(c) 首次即成功 → 返回 true 且尝试次数 == 1",
            first && _writeAttempts == 1, "True 且 1", $"{first} 且 {_writeAttempts}");

        // (g) 非 STA 是编程错误，必须向上抛而不是伪装成"剪贴板被占用"
        ResultPresenter.ClipboardWrite = static _ => throw new ThreadStateException("非 STA（探针模拟）");
        bool threw = false;
        try { ResultPresenter.TrySetClipboardText(UrlA); }
        catch (ThreadStateException) { threw = true; }

        Check("(g) ThreadStateException 向上抛（不伪装成\"剪贴板被占用\"）",
            threw, "抛出 ThreadStateException", threw ? "抛出了" : "被吞掉了");

        InstallClipboard(failFirst: 0);
    }

    // ───────────────────── 3. 复制失败的可见反馈 ─────────────────────

    private static void CheckCopyFailure()
    {
        var launcher = new FakeLauncher();
        var presenter = new ResultPresenter(launcher, LongDismissMs);

        InstallClipboard(alwaysFail: true);
        presenter.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));

        ToastWindow? toast = ActiveToast(presenter);

        Check("全失败时创建了提示条（绝不静默失败）", toast is not null, "有提示条", toast is null ? "无" : "有");
        Check("提示条 Kind == Failure（红色）", toast?.Kind == ToastKind.Failure,
            "Failure", toast?.Kind.ToString() ?? "(无)");
        Check("正文说明是「复制失败」", toast?.Message.Contains("复制失败") == true,
            "含\"复制失败\"", toast?.Message ?? "(无)");
        Check("正文点明原因是「剪贴板被占用」", toast?.Message.Contains("剪贴板被占用") == true,
            "含\"剪贴板被占用\"", toast?.Message ?? "(无)");
        Check("动作只有 retry 一个（此时还没提交，不该出现「打开」）",
            Ids(toast) == "retry", "retry", Ids(toast));
        Check("retry 的标签 == \"重试\"", Labels(toast) == "重试", "重试", Labels(toast));

        // 剪贴板恢复后点「重试」→ 必须回到**正常成功态**。
        //
        // 只显示一句不带任何按钮的「已复制」是真实的功能损失：那个 URL 此后没有任何 UI
        // 能打开它（托盘「最近 10 条」按规格只复制、不打开）；多码场景还会丢掉
        // [还有 N 个 ▸]、再也切不回去。
        InstallClipboard(failFirst: 0);
        ClickButton(toast, "retry");
        ToastWindow? afterRetry = ActiveToast(presenter);

        Check("点「重试」（此时剪贴板已可用）→ 提示条变为 Success",
            afterRetry?.Kind == ToastKind.Success, "Success", afterRetry?.Kind.ToString() ?? "(无)");
        Check("重试成功后正文 == 原码文本（不是一句「已复制」）",
            afterRetry?.Message == UrlA, UrlA, afterRetry?.Message ?? "(无)");
        Check("重试成功后仍含「打开」（可打开能力没丢）",
            Ids(afterRetry).Contains("open"), "含 open", Ids(afterRetry));
        Check("重试成功后的按钮组 == open,copy（回到正常成功态）",
            Ids(afterRetry) == "open,copy", "open,copy", Ids(afterRetry));
        Check("重试成功后的标签 == \"打开,复制\"",
            Labels(afterRetry) == "打开,复制", "打开,复制", Labels(afterRetry));

        // 渲染侧断言**不能**代替剪贴板断言：若 copy 与 render 各取一个下标
        // （例如 TrySetClipboardText(payloads[0].Text) 而渲染仍用 index），
        // 上面那一整组断言依然全绿。
        Check("重试实际写入剪贴板的码 == 原码（copy 与 render 用的是同一个下标）",
            _lastWritten == UrlA, UrlA, _lastWritten ?? "(null)");

        CloseActive(presenter);

        // 多码场景：重试成功后 [还有 N 个 ▸] 必须回来，否则再也切不回去
        var multiPresenter = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        InstallClipboard(alwaysFail: true);
        multiPresenter.ShowSuccess(new[] { Payload(UrlA), Payload(Plain) }, new Point(200, 200));

        InstallClipboard(failFirst: 0);
        ClickButton(ActiveToast(multiPresenter), "retry");
        ToastWindow? multiRetry = ActiveToast(multiPresenter);

        Check("多码重试成功后仍含「还有 N 个 ▸」",
            Ids(multiRetry).Contains("next"), "含 next", Ids(multiRetry));
        Check("多码重试成功后按钮组 == open,copy,next",
            Ids(multiRetry) == "open,copy,next", "open,copy,next", Ids(multiRetry));
        Check("多码重试成功后 next 标签 == \"还有 1 个 ▸\"",
            LabelOf(multiRetry, "next") == "还有 1 个 ▸", "还有 1 个 ▸", LabelOf(multiRetry, "next") ?? "(无)");
        Check("多码重试实际写入剪贴板的码 == 第 1 个码（index 0）",
            _lastWritten == UrlA, UrlA, _lastWritten ?? "(null)");

        CloseActive(multiPresenter);

        // 由「切换」触发的失败：重试必须保留**那一个** index，而不是退回第 0 个
        var switchPresenter = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        InstallClipboard(failFirst: 0);
        switchPresenter.ShowSuccess(new[] { Payload(UrlA), Payload(Plain) }, new Point(200, 200));

        InstallClipboard(alwaysFail: true);
        ClickButton(ActiveToast(switchPresenter), "next");          // 切到 index 1 时复制失败

        InstallClipboard(failFirst: 0);
        ClickButton(ActiveToast(switchPresenter), "retry");
        ToastWindow? afterSwitchRetry = ActiveToast(switchPresenter);

        Check("切换失败后重试 → 正文 == 第 2 个码（index 被保住，没退回第 0 个）",
            afterSwitchRetry?.Message == Plain, Plain, afterSwitchRetry?.Message ?? "(无)");
        Check("切换失败后重试 → 纯文本无 open，但仍含 next",
            Ids(afterSwitchRetry) == "copy,next", "copy,next", Ids(afterSwitchRetry));
        Check("切换失败后重试实际写入剪贴板的码 == 第 2 个码（index 被保住）",
            _lastWritten == Plain, Plain, _lastWritten ?? "(null)");

        CloseActive(switchPresenter);
    }

    // ───────────────────── 4. 按钮矩阵 ─────────────────────

    private static void CheckButtonMatrix()
    {
        // 行 1：可打开，仅 1 个码
        string row1 = MatrixOf(new[] { UrlA }, out ToastWindow? t1);
        Check("行1（可打开/1 个码）→ open,copy", row1 == "open,copy", "open,copy", row1);
        // 标签必须逐字核对：只断言 Id 的话，把 "打开" 改成 "Open" 全都不会变红，
        // 而"全部面向用户文案使用中文"是本项目的全局约束。
        Check("行1 的标签 == \"打开,复制\"", Labels(t1) == "打开,复制", "打开,复制", Labels(t1));

        // 行 2：可打开，多个码
        string row2 = MatrixOf(new[] { UrlA, UrlB }, out ToastWindow? t2);
        Check("行2（可打开/2 个码）→ open,copy,next", row2 == "open,copy,next", "open,copy,next", row2);
        Check("行2 的标签 == \"打开,复制,还有 1 个 ▸\"",
            Labels(t2) == "打开,复制,还有 1 个 ▸", "打开,复制,还有 1 个 ▸", Labels(t2));
        Check("行2 的 next 标签 == \"还有 1 个 ▸\"",
            LabelOf(t2, "next") == "还有 1 个 ▸", "还有 1 个 ▸", LabelOf(t2, "next") ?? "(无)");

        // 行 3：纯文本，多个码 —— 不得出现「打开」
        string row3 = MatrixOf(new[] { Plain, UrlB }, out ToastWindow? t3);
        Check("行3（纯文本/2 个码）→ copy,next（无 open）", row3 == "copy,next", "copy,next", row3);
        Check("行3 的标签 == \"复制,还有 1 个 ▸\"",
            Labels(t3) == "复制,还有 1 个 ▸", "复制,还有 1 个 ▸", Labels(t3));

        // 行 4：纯文本，仅 1 个码
        string row4 = MatrixOf(new[] { Plain }, out ToastWindow? tSingle);
        Check("行4（纯文本/1 个码）→ copy", row4 == "copy", "copy", row4);
        Check("行4 的标签 == \"复制\"", Labels(tSingle) == "复制", "复制", Labels(tSingle));

        // WiFi 码按纯文本处理（v1 不做一键连网），因此也不该有「打开」
        string rowWifi = MatrixOf(new[] { Wifi }, out _);
        Check("WiFi 码 → copy（无 open）", rowWifi == "copy", "copy", rowWifi);

        // next 的 N 是「除当前之外还剩几个」
        string row4codes = MatrixOf(new[] { UrlA, UrlB, UrlA, UrlB }, out ToastWindow? t4);
        Check("4 个码时 next 标签 == \"还有 3 个 ▸\"",
            LabelOf(t4, "next") == "还有 3 个 ▸", "还有 3 个 ▸", LabelOf(t4, "next") ?? "(无)");

        // 长文本截断：120 不截、121 截
        var atBoundary = new string('a', 120);
        var overBoundary = new string('a', 121);
        string msg120 = MessageOf(new[] { atBoundary });
        string msg121 = MessageOf(new[] { overBoundary });

        Check("正文恰好 120 字符时不截断",
            msg120.Length == 120 && !msg120.EndsWith('…'), "长度 120 且无省略号", $"长度 {msg120.Length}");
        Check("正文 121 字符时截断为 120 + 省略号",
            msg121.Length == 121 && msg121.EndsWith('…'), "长度 121 且以 … 结尾", $"长度 {msg121.Length}");
    }

    // ───────────────────── 5. 多码切换 ─────────────────────

    private static void CheckMultiCodeCycling()
    {
        var launcher = new FakeLauncher();
        var presenter = new ResultPresenter(launcher, LongDismissMs);

        // 顺序由 QrDecoder 保证（Position.TopLeft.X 再 Y）＝ 视觉顺序
        var payloads = new[] { Payload(UrlA), Payload(Plain), Payload(UrlB) };

        InstallClipboard(failFirst: 0);
        presenter.ShowSuccess(payloads, new Point(200, 200));

        Check("初始：剪贴板 == 第 1 个码", _lastWritten == UrlA, UrlA, _lastWritten ?? "(null)");
        Check("初始：index 0 可打开 → 含 open", Ids(ActiveToast(presenter)) == "open,copy,next",
            "open,copy,next", Ids(ActiveToast(presenter)));

        // 切到 index 1（纯文本）
        InstallClipboard(failFirst: 0);
        ClickButton(ActiveToast(presenter), "next");
        Check("切换 1 次：剪贴板 == 第 2 个码（纯文本）", _lastWritten == Plain, Plain, _lastWritten ?? "(null)");
        Check("切换 1 次：纯文本 → 按钮组不含 open", Ids(ActiveToast(presenter)) == "copy,next",
            "copy,next", Ids(ActiveToast(presenter)));
        Check("切换 1 次：正文 == 第 2 个码的文本",
            ActiveToast(presenter)?.Message == Plain, Plain, ActiveToast(presenter)?.Message ?? "(无)");

        // 切到 index 2（可打开）
        InstallClipboard(failFirst: 0);
        ClickButton(ActiveToast(presenter), "next");
        Check("切换 2 次：剪贴板 == 第 3 个码", _lastWritten == UrlB, UrlB, _lastWritten ?? "(null)");
        Check("切换 2 次：可打开 → 按钮组重新含 open", Ids(ActiveToast(presenter)) == "open,copy,next",
            "open,copy,next", Ids(ActiveToast(presenter)));

        // 回绕到 index 0
        InstallClipboard(failFirst: 0);
        ClickButton(ActiveToast(presenter), "next");
        Check("切换 3 次：回绕到 index 0，剪贴板 == 第 1 个码", _lastWritten == UrlA, UrlA, _lastWritten ?? "(null)");
        Check("切换 3 次：正文回到第 1 个码", ActiveToast(presenter)?.Message == UrlA,
            UrlA, ActiveToast(presenter)?.Message ?? "(无)");

        // 每次切换都应恰好写一次剪贴板（不多不少）
        InstallClipboard(failFirst: 0);
        ClickButton(ActiveToast(presenter), "next");
        Check("每次切换恰好写 1 次剪贴板", _writeAttempts == 1, "1", _writeAttempts.ToString());

        // 切换时若剪贴板不可用 → 走失败提示，且**不**把打开目标悄悄切过去
        InstallClipboard(alwaysFail: true);
        ClickButton(ActiveToast(presenter), "next");
        Check("切换时剪贴板失败 → 提示条变为 Failure（不是静默切换）",
            ActiveToast(presenter)?.Kind == ToastKind.Failure,
            "Failure", ActiveToast(presenter)?.Kind.ToString() ?? "(无)");

        CloseActive(presenter);
    }

    // ───────────────────── 6. 安全边界（本探针最有价值的一节） ─────────────────────

    private static void CheckSecurityBoundary()
    {
        // (1) 自定义 scheme：绝不显示「打开」，也绝不调用启动器
        CheckNoOpen(CustomScheme, "ms-msdt: 自定义 scheme");
        CheckNoOpen(FileScheme, "file:/// 文件 scheme");
        CheckNoOpen(Wifi, "WiFi 码");
        CheckNoOpen(Plain, "纯文本");

        // (2) URL：显示「打开」，点击后恰好调用启动器一次、且目标是该 URL
        var launcher = new FakeLauncher();
        var presenter = new ResultPresenter(launcher, LongDismissMs);
        InstallClipboard(failFirst: 0);

        presenter.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        ToastWindow? toast = ActiveToast(presenter);

        Check("URL → 按钮组含 open", Ids(toast) == "open,copy", "open,copy", Ids(toast));

        int callsBeforeClick = launcher.Calls;
        ClickButton(toast, "open");

        Check("点「打开」→ 启动器被调用 1 次", launcher.Calls == callsBeforeClick + 1,
            (callsBeforeClick + 1).ToString(), launcher.Calls.ToString());
        Check("点「打开」→ 目标就是二维码文本", launcher.LastTarget == UrlA,
            UrlA, launcher.LastTarget ?? "(null)");

        // (3) 点「复制」不应触发启动器（否则"打开次数为 0"就没有信息量）
        // 注意：点任一个按钮后提示条会自行 Close()（ToastWindow.OnMouseDown 的既有行为），
        // 因此这里 _active 已经清空 —— 要重新弹一个才能测下一个按钮。
        Check("点按钮后提示条自行关闭、_active 清空", ActiveToast(presenter) is null,
            "null", ActiveToast(presenter) is null ? "null" : "有值");

        presenter.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        ToastWindow? toast2 = ActiveToast(presenter);
        int callsBeforeCopy = launcher.Calls;
        ClickButton(toast2, "copy");

        Check("点「复制」不触发启动器", launcher.Calls == callsBeforeCopy,
            callsBeforeCopy.ToString(), launcher.Calls.ToString());

        CloseActive(presenter);
    }

    /// <summary>对一个 payload 走完整呈现，断言其按钮组里**没有** open，且启动器调用次数为 0。</summary>
    private static void CheckNoOpen(string text, string label)
    {
        var launcher = new FakeLauncher();
        var presenter = new ResultPresenter(launcher, LongDismissMs);
        InstallClipboard(failFirst: 0);

        presenter.ShowSuccess(new[] { Payload(text) }, new Point(200, 200));

        Check($"{label} → 按钮组不含 open", Ids(ActiveToast(presenter)) == "copy",
            "copy", Ids(ActiveToast(presenter)));
        Check($"{label} → 启动器调用次数 == 0", launcher.Calls == 0,
            "0", launcher.Calls.ToString());

        CloseActive(presenter);
    }

    // ───────────────────── 7. Replace / ShowMessage / 打开失败 ─────────────────────

    private static void CheckReplaceAndMessages()
    {
        var launcher = new FakeLauncher();
        var presenter = new ResultPresenter(launcher, LongDismissMs);
        InstallClipboard(failFirst: 0);

        presenter.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        ToastWindow? first = ActiveToast(presenter);

        presenter.ShowSuccess(new[] { Payload(UrlB) }, new Point(200, 200));
        ToastWindow? second = ActiveToast(presenter);

        presenter.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        ToastWindow? third = ActiveToast(presenter);

        Check("第 1 个提示条已被 Close()（不泄漏）", IsGone(first),
            "已关闭", first is null ? "(无)" : (IsGone(first) ? "已关闭" : "仍打开"));
        Check("第 2 个提示条已被 Close()（不泄漏）", IsGone(second),
            "已关闭", second is null ? "(无)" : (IsGone(second) ? "已关闭" : "仍打开"));
        Check("_active 指向第 3 个提示条", ReferenceEquals(ActiveToast(presenter), third),
            "第 3 个", ReferenceEquals(ActiveToast(presenter), third) ? "第 3 个" : "别的");
        Check("第 3 个提示条仍打开", third is not null && !IsGone(third),
            "仍打开", third is null ? "(无)" : (IsGone(third) ? "已关闭" : "仍打开"));

        // 关闭当前那个 → _active 应清空（FormClosed 里的条件判断）
        third?.Close();
        Check("关闭当前提示条后 _active 变回 null",
            ActiveToast(presenter) is null, "null", ActiveToast(presenter) is null ? "null" : "有值");

        // 空结果 → Info 提示
        presenter.ShowSuccess(Array.Empty<QrPayload>(), new Point(200, 200));
        Check("ShowSuccess(空) → 正文为「未识别到二维码」",
            ActiveToast(presenter)?.Message == "未识别到二维码",
            "未识别到二维码", ActiveToast(presenter)?.Message ?? "(无)");
        Check("ShowSuccess(空) → Kind == Info",
            ActiveToast(presenter)?.Kind == ToastKind.Info, "Info", ActiveToast(presenter)?.Kind.ToString() ?? "(无)");
        CloseActive(presenter);

        // ShowMessage(Failure) → 红色
        presenter.ShowMessage("出错了", ToastKind.Failure, new Point(200, 200));
        Check("ShowMessage(Failure) → BackColor 为红 (200,40,40)",
            ActiveToast(presenter)?.BackColor == Color.FromArgb(200, 40, 40),
            Color.FromArgb(200, 40, 40).ToString(), ActiveToast(presenter)?.BackColor.ToString() ?? "(无)");
        Check("ShowMessage → 不带动作按钮", Ids(ActiveToast(presenter)).Length == 0,
            "(空)", Ids(ActiveToast(presenter)).Length == 0 ? "(空)" : Ids(ActiveToast(presenter)));
        CloseActive(presenter);

        // 启动器抛异常 → Failure 提示，正文以「无法打开：」开头
        launcher.Throw = new InvalidOperationException("目标不在白名单内（探针模拟）");
        presenter.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        ClickButton(ActiveToast(presenter), "open");

        Check("启动器抛异常 → Kind == Failure", ActiveToast(presenter)?.Kind == ToastKind.Failure,
            "Failure", ActiveToast(presenter)?.Kind.ToString() ?? "(无)");
        Check("启动器抛异常 → 正文以「无法打开：」开头",
            ActiveToast(presenter)?.Message.StartsWith("无法打开：") == true,
            "以\"无法打开：\"开头", ActiveToast(presenter)?.Message ?? "(无)");

        CloseActive(presenter);
    }

    // ───────────────────── 8. 异常即失败 ─────────────────────

    private static void CheckNoExceptions()
    {
        _invokeErrorsAtSection8 = InvokeErrors.Count;
        Check("前 7 节的反射调用零异常", InvokeErrors.Count == 0,
            "0 个", $"{InvokeErrors.Count} 个：{string.Join(" | ", InvokeErrors)}");
    }

    // ───────────────────── 9. 变异灵敏度 ─────────────────────

    /// <summary>
    /// 本节**不重新编译**，只证明前面那些断言的判据确实有鉴别力：
    /// 若被测行为改成错误的实现，断言的观测值就会不同。
    ///
    /// 真正"重新编译一份错误实现、要求探针变红"的证据在
    /// <c>tools/ResultPresenterProbe/run-mutations.sh</c> 里。
    /// </summary>
    private static void CheckMutationSensitivity()
    {
        // (A) 按钮矩阵：纯文本的序列必须与"总是显示「打开」"的错误实现不同，
        //     否则行3/行4 的断言只是恒真式。
        string plainObserved = MatrixOf(new[] { Plain }, out _);
        string wrongAlwaysOpen = "open,copy";

        Check("(A) 变异：若「打开」判定写成「总是显示」，序列会与实测不同 ⇒ 行4 有鉴别力",
            plainObserved != wrongAlwaysOpen, $"≠ {wrongAlwaysOpen}", plainObserved);

        // (B) 剪贴板：错误实现（不重试）的尝试次数会与实测不同 ⇒ 「尝试次数 == 3」有鉴别力。
        InstallClipboard(failFirst: 2);
        ResultPresenter.TrySetClipboardText(UrlA);
        int observedAttempts = _writeAttempts;
        const int wrongNoRetryAttempts = 1;

        Check("(B) 变异：不重试的实现只尝试 1 次 ⇒ 「尝试次数 == 3」有鉴别力",
            observedAttempts != wrongNoRetryAttempts, $"≠ {wrongNoRetryAttempts}", observedAttempts.ToString());

        // (C) 启动器计数器确实会动 —— 否则"调用次数 == 0"什么都没证明。
        var launcher = new FakeLauncher();
        int before = launcher.Calls;
        launcher.Open(UrlA);
        Check("(C) 启动器计数器会真的增长 ⇒ 「调用次数 == 0」是有信息的",
            launcher.Calls == before + 1, (before + 1).ToString(), launcher.Calls.ToString());

        // (D) 有按钮与无按钮的提示条在"动作数"上必须可区分 ⇒ 「无动作」断言不是恒真。
        var withActions = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        InstallClipboard(failFirst: 0);
        withActions.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        int actionCountWithButtons = CountActions(ActiveToast(withActions));
        CloseActive(withActions);

        var noActions = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        noActions.ShowMessage("仅提示", ToastKind.Info, new Point(200, 200));
        int actionCountWithoutButtons = CountActions(ActiveToast(noActions));
        CloseActive(noActions);

        Check("(D) 变异：无动作提示条的动作数(0) ≠ 带按钮提示条的动作数 ⇒ 「无动作」有鉴别力",
            actionCountWithoutButtons != actionCountWithButtons,
            $"≠ {actionCountWithButtons}", actionCountWithoutButtons.ToString());

        // (E) 「复制失败」路径确实与成功路径产生不同的 Kind ⇒ Kind 断言不是恒真。
        var failing = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        InstallClipboard(alwaysFail: true);
        failing.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        ToastKind failureKind = ActiveToast(failing)?.Kind ?? ToastKind.Info;
        CloseActive(failing);

        var succeeding = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        InstallClipboard(failFirst: 0);
        succeeding.ShowSuccess(new[] { Payload(UrlA) }, new Point(200, 200));
        ToastKind successKind = ActiveToast(succeeding)?.Kind ?? ToastKind.Failure;
        CloseActive(succeeding);

        Check("(E) 变异：全失败与成功产生不同 Kind ⇒ Kind 断言有鉴别力",
            failureKind != successKind, $"≠ {successKind}", failureKind.ToString());

        // (F) next 的标签随码数变化 ⇒ 「标签 == 还有 N 个 ▸」不是常量比对。
        MatrixOf(new[] { UrlA, UrlB }, out ToastWindow? two);
        MatrixOf(new[] { UrlA, UrlB, UrlA }, out ToastWindow? three);
        string label2 = LabelOf(two, "next") ?? "";
        string label3 = LabelOf(three, "next") ?? "";

        Check("(F) 变异：2 个码与 3 个码的 next 标签不同 ⇒ 标签断言不是常量比对",
            label2 != label3, $"≠ {label3}", label2);

        InstallClipboard(failFirst: 0);
    }

    // ───────────────────── 10. 报告项 ─────────────────────

    private static void ReportObservations()
    {
        Notes.Add("生产路径下 ResultPresenter.ClipboardWrite 从未被赋值（默认即真实剪贴板）；"
                + "探针把它整体替换为模拟实现，因此全程未触碰真实剪贴板。");
        Notes.Add($"虚拟桌面 = {Show(SystemInformation.VirtualScreen)}；"
                + "提示条位置与可见性属 ToastWindowProbe 的职责，本节不重复断言。");
        Notes.Add("仍未验证（需真机、非锁屏）：真实窗口下的悬停与点击手感、跨进程前台不被抢占、"
                + "真实剪贴板在 Office/远程桌面占用下的重试时序。");
    }

    // ───────────────────── 探针侧小工具 ─────────────────────

    private sealed class FakeLauncher : IShellLauncher
    {
        public int Calls;
        public string? LastTarget;
        public Exception? Throw;

        public void Open(string target)
        {
            Calls++;
            LastTarget = target;
            if (Throw is not null) throw Throw;
        }
    }

    private static QrPayload Payload(string text) => new()
    {
        Text = text,
        Format = "QR Code",
        IsInverted = false,
        Left = 0,
        Top = 0,
    };

    private static void InstallClipboard(int failFirst = 0, bool alwaysFail = false)
    {
        _writeAttempts = 0;
        _lastWritten = null;
        _failRemaining = failFirst;
        _alwaysFail = alwaysFail;

        ResultPresenter.ClipboardWrite = text =>
        {
            _writeAttempts++;

            if (_alwaysFail || _failRemaining > 0)
            {
                if (!_alwaysFail) _failRemaining--;
                throw new ExternalException("剪贴板被占用（探针模拟）");
            }

            _lastWritten = text;
        };
    }

    /// <summary>对一个 payload 集合走完整呈现，返回按钮 Id 的逗号连接串。</summary>
    private static string MatrixOf(IReadOnlyList<string> texts, out ToastWindow? toast)
    {
        var presenter = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        InstallClipboard(failFirst: 0);
        presenter.ShowSuccess(texts.Select(Payload).ToArray(), new Point(200, 200));

        ToastWindow? active = ActiveToast(presenter);
        toast = active;
        string result = Ids(active);
        CloseActive(presenter);
        return result;
    }

    private static string MessageOf(IReadOnlyList<string> texts)
    {
        var presenter = new ResultPresenter(new FakeLauncher(), LongDismissMs);
        InstallClipboard(failFirst: 0);
        presenter.ShowSuccess(texts.Select(Payload).ToArray(), new Point(200, 200));

        string message = ActiveToast(presenter)?.Message ?? "(无)";
        CloseActive(presenter);
        return message;
    }

    private static ToastWindow? ActiveToast(ResultPresenter presenter) => ReadField<ToastWindow?>(presenter, "_active");

    private static void CloseActive(ResultPresenter presenter) => ActiveToast(presenter)?.Close();

    private static ToastAction[] Actions(ToastWindow? w) => ReadField<ToastAction[]?>(w, "_actions") ?? Array.Empty<ToastAction>();

    private static int CountActions(ToastWindow? w) => Actions(w).Length;

    private static string Ids(ToastWindow? w) => string.Join(",", Actions(w).Select(a => a.Id));

    private static string Labels(ToastWindow? w) => string.Join(",", Actions(w).Select(a => a.Label));

    private static string? LabelOf(ToastWindow? w, string id)
        => Actions(w).FirstOrDefault(a => a.Id == id)?.Label;

    /// <summary>走**真实的** OnMouseDown 命中测试路径点按钮（不是直接调私有方法）。</summary>
    private static void ClickButton(ToastWindow? w, string id)
    {
        if (w is null)
        {
            InvokeErrors.Add($"ClickButton({id})：没有活动提示条");
            return;
        }

        ToastAction[] actions = Actions(w);
        Rectangle[] rects = ReadField<Rectangle[]>(w, "_buttonRects") ?? Array.Empty<Rectangle>();
        int index = Array.FindIndex(actions, a => a.Id == id);

        if (index < 0)
        {
            InvokeErrors.Add($"ClickButton：按钮 {id} 不存在（实际：{Ids(w)}）");
            return;
        }

        if (index >= rects.Length)
        {
            InvokeErrors.Add($"ClickButton：按钮 {id} 没有对应的矩形");
            return;
        }

        Point center = new(rects[index].Left + rects[index].Width / 2,
                           rects[index].Top + rects[index].Height / 2);

        Invoke(w, "OnMouseDown", new[] { typeof(MouseEventArgs) },
               new object[] { new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0) });
    }

    private static bool IsGone(ToastWindow? w) => w is null || w.IsDisposed || !w.IsHandleCreated;

    // ───────────────────── 反射辅助 ─────────────────────

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

    private static T ReadField<T>(object? target, string name)
    {
        if (target is null)
        {
            InvokeErrors.Add($"字段 {name}：目标为 null");
            return default!;
        }

        FieldInfo? fi = target.GetType().GetField(name, NonPublic);
        if (fi is null)
        {
            InvokeErrors.Add($"字段 {name} 未找到");
            return default!;
        }

        object? v = fi.GetValue(target);
        return v is T t ? t : default!;
    }

    /// <summary>读 <c>private const</c>：这些常量本身就是"契约字面值"，必须能被断言。</summary>
    private static int ReadConst(string name)
    {
        FieldInfo? fi = typeof(ResultPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        if (fi is null)
        {
            InvokeErrors.Add($"常量 {name} 未找到");
            return -1;
        }

        return fi.GetRawConstantValue() is int v ? v : -1;
    }

    // ───────────────────── 汇总与清单 ─────────────────────

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
}
