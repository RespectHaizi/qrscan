using System.Reflection;
using QrScan;
using QrScan.Core;

namespace StartupRepairProbe;

/// <summary>
/// <see cref="StartupPathRepair"/> 的常驻回归探针。
///
/// <b>它守的是什么</b>：这条修复解决的是一个"两个可见信号都是正常的"隐蔽失效 ——
/// 用户把 exe 挪走之后，注册表里的自启路径永久指向旧位置，而托盘菜单里的勾还稳稳打着。
/// 正因为它平时什么都不做，写错的方式也全都"看起来没事"：
///
/// 1. <b>比对写成区分大小写</b>（<c>Ordinal</c> 而不是 <c>OrdinalIgnoreCase</c>）→ 每次启动
///    都白重写一次注册表。功能上没有后果，所以没有任何地方会报错，只会留下无谓的写入。
/// 2. <b>守卫被删掉</b>（未启用时也去写、或读到 null 也去写）→ 把用户**主动关掉**的自启
///    又打开，这是用户会真实察觉到的行为倒退。
/// 3. <b>异常过滤写成 <c>catch (Exception)</c></b> → 看起来更"稳"，实际是把真正的编程
///    错误也一起吞掉；本探针用一条"清单外的异常必须逃逸"来钉死这个区别。
/// 4. <b>返回值与实际副作用脱节</b>（返回 true 却没写、或写了却返回 false）→ 探针不仅断言
///    返回值，还断言 <c>SetEnabled</c> 的**调用次数与实参序列**。
///
/// <b>手法</b>：csproj 用 <c>&lt;Compile Include&gt;</c> 把仓库里**真实的**
/// <c>src/QrScan/StartupPathRepair.cs</c> 编进本程序集，于是 <c>internal</c> 类型在这里可见，
/// 且不需要给仓库加 <c>InternalsVisibleTo</c>。
///
/// <b>完全不碰真实注册表</b>：所有行为都通过一个假的 <see cref="IStartupManager"/> 驱动，
/// 所以本探针运行一万次也不会动到用户的开机自启。真实注册表改写的端到端验证属人工测试。
///
/// <b>不需要可见桌面</b>：全程无 UI，锁屏或无人值守环境下同样可跑。
///
/// 任一项失败时进程退出码为 1，可直接被脚本/CI 当检查用。
/// </summary>
internal static class Program
{
    private const BindingFlags AnyMethod =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// 断言总数的预期值。<b>任何一次增删断言都必须同步这个数字。</b>
    ///
    /// 为什么需要它：退出码只由 <c>_fail</c> 决定，而 <c>_pass</c> 只被打印 —— 于是
    /// "删掉某一整节的 <c>Section(...)</c> 调用"会让那一节的证据<b>静默消失</b>，
    /// 剩余项全绿、退出码仍是 0。有了这道闸门，"删节 / 删断言"会直接变成退出码 1。
    /// （任务 11 与任务 12 的探针都因为缺这道闸门被审查者指出过。）
    /// </summary>
    private const int ExpectedAssertions = 59;

    // ── 测试用路径 ────────────────────────────────────────────────────────────
    /// <summary>当前 exe 路径（探针里只是一个"应当被写进去"的目标值）。</summary>
    private const string CurrentExe = @"C:\Users\dell\Desktop\code\QrScan\src\QrScan\bin\Release\net8.0-windows\win-x64\QrScan.exe";

    /// <summary>用户把它从下载文件夹挪走之前的旧路径。</summary>
    private const string OldExe = @"C:\Users\dell\Downloads\QrScan.exe";

    /// <summary>同一个文件名、不同目录 —— 最容易被"只比文件名"的错误实现漏掉的形态。</summary>
    private const string MovedExe = @"C:\Users\dell\Desktop\QrScan.exe";

    private static int _pass;
    private static int _fail;

    /// <summary>
    /// 实际执行过的断言总数。单独计数是为了让"断言被删"与"断言失败"可区分 ——
    /// 拿 <c>_pass</c> 去比 ExpectedAssertions 会把任何一次真实失败都误报成"总数不符"。
    /// </summary>
    private static int _assertions;

    private static readonly List<(string Title, int Count)> SectionCounts = new();
    private static readonly List<string> Lines = new();
    private static readonly List<string> Notes = new();
    private static readonly List<string> InternalErrors = new();

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("===== StartupRepairProbe：StartupPathRepair 常驻回归探针 =====");
        Console.WriteLine("全程使用假的 IStartupManager —— 本探针不读、不写真实注册表。");
        Console.WriteLine($"对比目标（CurrentExe） = {CurrentExe}");
        Console.WriteLine($"旧位置（OldExe）       = {OldExe}");
        Console.WriteLine();

        Section("1. 接缝的形状与签名", CheckShape);
        Section("2. 五条行为（返回值 + SetEnabled 调用次数与实参）", CheckFiveBehaviours);
        Section("3. 路径比较语义", CheckPathSemantics);
        Section("4. 幂等：修好之后不再重复写", CheckIdempotence);
        Section("5. 变异灵敏度：判据确实有鉴别力", CheckMutationSensitivity);
        Section("6. 异常吞掉：只吞清单里的四类", CheckSwallowSemantics);
        Section("7. 生产入口 RepairUsingCurrentExecutable", CheckProductionEntry);
        Section("8. 调用点与「首次落盘失败必须可见」", CheckCallSite);
        Section("9. 异常即失败", CheckNoInternalErrors);

        // ── 闸门：断言总数。不计入 _pass（否则它会把自己算进去、变成自指）。
        // "删掉一整节"是本探针最危险的自欺形式：它的表现是完全全绿。
        // 比的是 _assertions 而不是 _pass —— 后者会把任何一次真实失败都误报成"总数不符"。
        // **必须在汇总行之前结算**，否则机器可读的那一行看不到它（任务 14 的实测教训）。
        if (_assertions != ExpectedAssertions)
        {
            _fail++;
            Lines.Add("  [FAIL] 断言总数 == ExpectedAssertions（防「整节被删」而仍然全绿）");
            Lines.Add($"         期望 = {ExpectedAssertions}");
            Lines.Add($"         实测 = {_assertions}（通过 {_pass} / 失败 {_fail}）");
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

    // ───────────────────── 1. 接缝的形状与签名 ─────────────────────

    private static void CheckShape()
    {
        Type? t = typeof(StartupPathRepair);

        Check("StartupPathRepair 的接线：静态、密封、不可见（工具类，不该进公共 API）",
            t.IsAbstract && t.IsSealed && !t.IsPublic,
            "static 且 internal",
            $"IsAbstract={t.IsAbstract}, IsSealed={t.IsSealed}, IsPublic={t.IsPublic}");

        MethodInfo? core = t.GetMethod("RepairStartupPathIfNeeded", AnyMethod);
        Check("RepairStartupPathIfNeeded(IStartupManager, string) → bool 存在且静态",
            core is not null && core.IsStatic && core.ReturnType == typeof(bool)
                && core.GetParameters() is [{ ParameterType: var p0 }, { ParameterType: var p1 }]
                && p0 == typeof(IStartupManager) && p1 == typeof(string),
            "static bool (IStartupManager, string)",
            core is null ? "未找到" : $"{Describe(core)}");

        MethodInfo? entry = t.GetMethod("RepairUsingCurrentExecutable", AnyMethod);
        Check("RepairUsingCurrentExecutable(IStartupManager) → bool 存在且静态",
            entry is not null && entry.IsStatic && entry.ReturnType == typeof(bool)
                && entry.GetParameters() is [{ ParameterType: var q0 }] && q0 == typeof(IStartupManager),
            "static bool (IStartupManager)",
            entry is null ? "未找到" : $"{Describe(entry)}");
    }

    // ───────────────────── 2. 五条行为 ─────────────────────

    private static void CheckFiveBehaviours()
    {
        Scenario("A 未启用（Path 为 null）", new FakeStartup { Enabled = false, Path = null },
            expectResult: false, expectArgs: Array.Empty<bool>());

        // 这个组合在真实的 StartupManager 里不可达（IsEnabled 的定义就是"RegisteredPath 非 null"），
        // 但守卫**不该依赖那个不变式**：一个假实现、或者将来接口语义变了，只要漏掉守卫，
        // 就会把用户**主动关掉**的自启又打开 —— 那是用户会真实察觉到的行为倒退。
        // （没有这一条时，"删掉未启用守卫"这个变异会**逃逸**：场景 A 的 Path 恰好是 null，
        //   于是即使守卫没了，它也会被下面的 null 守卫拦住、看起来一切正常。）
        Scenario("A2 未启用但仍能读到路径（守卫必须优先于路径比较）",
            new FakeStartup { Enabled = false, Path = OldExe },
            expectResult: false, expectArgs: Array.Empty<bool>());

        Scenario("B 已启用但读不到路径（RegisteredPath 为 null）",
            new FakeStartup { Enabled = true, Path = null },
            expectResult: false, expectArgs: Array.Empty<bool>());

        Scenario("C 已启用且路径完全一致", new FakeStartup { Enabled = true, Path = CurrentExe },
            expectResult: false, expectArgs: Array.Empty<bool>());

        Scenario("D 已启用且路径指向别的目录", new FakeStartup { Enabled = true, Path = OldExe },
            expectResult: true, expectArgs: new[] { true });

        Scenario("E 已启用且是同一文件名、旧目录", new FakeStartup { Enabled = true, Path = MovedExe },
            expectResult: true, expectArgs: new[] { true });
    }

    // ───────────────────── 3. 路径比较语义 ─────────────────────

    private static void CheckPathSemantics()
    {
        Scenario("F 仅大小写不同（Windows 上同一个文件）",
            new FakeStartup { Enabled = true, Path = CurrentExe.ToLowerInvariant() },
            expectResult: false, expectArgs: Array.Empty<bool>());

        // 以下两条记录的是**本方法的契约**：它做的是精确（忽略大小写的）比较，
        // **不做**路径归一化 —— 分隔符与首尾空白的归一属于 StartupManager.RegisteredPath
        // 的职责。所以这两种形态会各触发一次无害的重写，随后注册表里就是规范形态。
        Scenario("G 正斜杠 vs 反斜杠（本方法不归一分隔符 → 视为不同）",
            new FakeStartup { Enabled = true, Path = CurrentExe.Replace('\\', '/') },
            expectResult: true, expectArgs: new[] { true });

        Scenario("H 尾随空格（本方法不 trim → 视为不同）",
            new FakeStartup { Enabled = true, Path = CurrentExe + " " },
            expectResult: true, expectArgs: new[] { true });
    }

    // ───────────────────── 4. 幂等 ─────────────────────

    private static void CheckIdempotence()
    {
        // 假实现模仿真实 StartupManager：SetEnabled(true) 之后注册表里就是"当前路径"。
        var fake = new FakeStartup { Enabled = true, Path = OldExe, PathAfterEnable = CurrentExe };

        Scenario("I 幂等·第一次（路径不一致 → 修复）", fake,
            expectResult: true, expectArgs: new[] { true });

        Scenario("J 幂等·第二次（已一致 → 不再写）", fake,
            expectResult: false, expectArgs: new[] { true });
    }

    // ───────────────────── 5. 变异灵敏度 ─────────────────────

    /// <summary>
    /// 本节不重新编译，只证明场景 F 的判据确实有鉴别力。
    /// 真正"重新编译一份错误实现、要求探针变红"的证据在
    /// <c>tools/StartupRepairProbe/run-mutations.sh</c> 里。
    /// </summary>
    private static void CheckMutationSensitivity()
    {
        // (K) 场景 F 用的是 OrdinalIgnoreCase 语义。若实现写成 Ordinal，
        //     本场景就会返回 true（误判为"不一致"→ 每次启动白重写一次）。
        //     这里直接算出两种比较的结论差异，证明 F 不是恒真式。
        string lower = CurrentExe.ToLowerInvariant();
        bool ordinalSaysEqual = string.Equals(lower, CurrentExe, StringComparison.Ordinal);
        bool ignoreCaseSaysEqual = string.Equals(lower, CurrentExe, StringComparison.OrdinalIgnoreCase);

        Check("(K) 变异灵敏度：Ordinal 在本场景判为不同、OrdinalIgnoreCase 判为相同 ⇒ 场景 F 有鉴别力",
            !ordinalSaysEqual && ignoreCaseSaysEqual,
            "Ordinal=不同, OrdinalIgnoreCase=相同",
            $"Ordinal={(ordinalSaysEqual ? "相同" : "不同")}, OrdinalIgnoreCase={(ignoreCaseSaysEqual ? "相同" : "不同")}");
    }

    // ───────────────────── 6. 异常吞掉 ─────────────────────

    private static void CheckSwallowSemantics()
    {
        Swallow("IsEnabled 抛 IOException",
            new FakeStartup { EnableOnReadThrows = new IOException("probe") }, 0);
        Swallow("IsEnabled 抛 UnauthorizedAccessException",
            new FakeStartup { EnableOnReadThrows = new UnauthorizedAccessException("probe") }, 0);
        Swallow("IsEnabled 抛 SecurityException",
            new FakeStartup { EnableOnReadThrows = new System.Security.SecurityException("probe") }, 0);
        Swallow("IsEnabled 抛 InvalidOperationException",
            new FakeStartup { EnableOnReadThrows = new InvalidOperationException("probe") }, 0);

        Swallow("RegisteredPath 抛 IOException",
            new FakeStartup { Enabled = true, PathOnReadThrows = new IOException("probe") }, 0);
        Swallow("RegisteredPath 抛 UnauthorizedAccessException",
            new FakeStartup { Enabled = true, PathOnReadThrows = new UnauthorizedAccessException("probe") }, 0);

        // SetEnabled 抛 —— 这两条额外断言"真的调用到了写入"，
        // 否则"返回 false"可能只是因为它在更早的地方就提前返回了。
        Swallow("SetEnabled 抛 IOException（且确实调用到了写入）",
            new FakeStartup { Enabled = true, Path = OldExe, WriteThrows = new IOException("probe") }, 1);
        Swallow("SetEnabled 抛 UnauthorizedAccessException（且确实调用到了写入）",
            new FakeStartup { Enabled = true, Path = OldExe, WriteThrows = new UnauthorizedAccessException("probe") }, 1);

        // (W) 清单外的异常类型**必须逃逸**。
        //     如果实现图省事写成 catch (Exception)，这一条会红 ——
        //     而那种写法会把真正的编程错误（例如 NullReferenceException、
        //     或未来某个接口成员新增的异常类型）一起吞掉，让 Bug 无从暴露。
        var unlisted = new FakeStartup { Enabled = true, EnableOnReadThrows = new ArgumentOutOfRangeException("probe") };
        (bool threw, bool result, string detail) = Call(unlisted, CurrentExe);

        Check("(W) 清单外的异常类型必须向调用方抛出（证明 catch 不是 catch (Exception)）",
            threw, "抛出 ArgumentOutOfRangeException", threw ? detail : $"被吞掉，返回 {result}");
    }

    // ───────────────────── 7. 生产入口 ─────────────────────

    private static void CheckProductionEntry()
    {
        // 入口的返回语义是"这件事现在是不是好的"，而不是"本次有没有写入"：
        //   true  = 已与当前路径一致（含"本来就无需修"与"刚修好了"）
        //   false = 需要修、但没能修成
        // 调用方直接拿它决定要不要弹"无法修复开机自启"，所以这条边界必须被钉死 ——
        // 若沿用"有没有写"的旧含义，**每一次自启本已正确的正常启动**都会被报成失败。

        var idle = new FakeStartup { Enabled = false, Path = null };
        bool idleResult = StartupPathRepair.RepairUsingCurrentExecutable(idle);
        Check("X 未启用时 RepairUsingCurrentExecutable 返回 true（无需修复 ≠ 失败）且不写",
            idleResult && idle.SetEnabledCalls == 0,
            "true / 0 次写入",
            $"{idleResult} / {idle.SetEnabledCalls} 次写入");

        // 本节的断言必须用**真正的**当前 exe 路径：RepairUsingCurrentExecutable 不接路径参数，
        // 它自己调 StartupManager.CurrentExecutablePath()。若拿上面那个常量 CurrentExe 当"当前路径"，
        // 就与真实值对不上 —— 断言会因错误的理由失败。
        string realCurrent = StartupManager.CurrentExecutablePath();

        // 最常见的正常启动形态：自启已开、且路径就是当前 exe。
        // 这里**必须**是 true，否则每次启动都会弹一个假的"无法修复"。
        var consistent = new FakeStartup { Enabled = true, Path = realCurrent };
        bool consistentResult = StartupPathRepair.RepairUsingCurrentExecutable(consistent);
        Check("X2 已启用且路径已一致时返回 true（路径一致 ≠ 失败）且不写",
            consistentResult && consistent.SetEnabledCalls == 0,
            "true / 0 次写入",
            $"{consistentResult} / {consistent.SetEnabledCalls} 次写入");

        // 写入后把路径改成**真实当前路径**，这样"修好之后是一致的"才成立。
        var stale = new FakeStartup { Enabled = true, Path = OldExe, PathAfterEnable = realCurrent };
        bool staleResult = StartupPathRepair.RepairUsingCurrentExecutable(stale);
        Check("X3 已启用且路径陈旧时返回 true 且写入 1 次（证明它取到了真实当前路径）",
            staleResult && stale.SetEnabledCalls == 1 && stale.SetEnabledArgs is [true],
            "true / 1 次写入且实参为 true",
            $"{staleResult} / {stale.SetEnabledCalls} 次写入，实参 [{string.Join(", ", stale.SetEnabledArgs)}]");

        // 真正的失败：需要修、但写入抛了被吞掉的异常。
        // 这正是调用方要弹那个气泡的唯一情形。
        var writeFails = new FakeStartup
        {
            Enabled = true,
            Path = OldExe,
            WriteThrows = new IOException("probe: 注册表不可写"),
        };
        bool writeFailsResult = StartupPathRepair.RepairUsingCurrentExecutable(writeFails);
        Check("X4 需要修但写入失败时返回 false（这是调用方唯一应当弹提示的情形）",
            !writeFailsResult && writeFails.SetEnabledCalls == 1,
            "false / 尝试写入 1 次",
            $"{writeFailsResult} / {writeFails.SetEnabledCalls} 次写入");

        Notes.Add("RepairUsingCurrentExecutable 里那条 catch (InvalidOperationException) 无法被"
                  + "本探针触发 —— 它守的是 Environment.ProcessPath 为 null 的异常宿主，"
                  + "正常进程里造不出来（它返回 true，即不报成失败，因为那种宿主下自启本就无意义）。"
                  + "属已知覆盖缺口。");
    }

    // ───────────────────── 8. 调用点与第二件事 ─────────────────────

    /// <summary>
    /// 本节全部是**对源码文本**的检查，不是行为验证。
    ///
    /// 原因：<c>TrayApplicationContext</c> 的构造函数会读配置、写配置、抢真实热键、
    /// 建真实托盘图标，不适合在探针里构造（它只能在真实的 WinForms 消息循环里跑）。
    /// 所以"接线长成什么样"只能用文本钉住，并在输出里明确标注为 [文本]。
    /// </summary>
    private static void CheckCallSite()
    {
        Console.WriteLine("  （以下全部是**对源码文本**的检查，不是行为验证；标注为 [文本]）");

        string ctx = ReadSource("src/QrScan/TrayApplicationContext.cs");

        int repairIdx = ctx.IndexOf("RepairUsingCurrentExecutable", StringComparison.Ordinal);
        int trayHostIdx = ctx.IndexOf("new TrayHost(", StringComparison.Ordinal);
        int saveIdx = ctx.IndexOf("configStore.Save(", StringComparison.Ordinal);
        int hotkeyIdx = ctx.IndexOf("new HotkeyManager(", StringComparison.Ordinal);

        Check("[文本] 调用了 StartupPathRepair.RepairUsingCurrentExecutable(startup)",
            repairIdx >= 0 && ctx.Contains("RepairUsingCurrentExecutable(startup)", StringComparison.Ordinal),
            "存在 RepairUsingCurrentExecutable(startup)",
            repairIdx >= 0 ? $"第 {repairIdx} 字符处" : "未找到");

        Check("[文本] 自启修复出现在 new TrayHost( 之后（失败时要能弹气泡）且早于 new HotkeyManager(",
            repairIdx >= 0 && trayHostIdx >= 0 && trayHostIdx < repairIdx
                && hotkeyIdx >= 0 && hotkeyIdx > repairIdx,
            "TrayHost 索引 < repair 索引 < HotkeyManager 索引",
            $"repair={repairIdx}, TrayHost={trayHostIdx}, HotkeyManager={hotkeyIdx}");

        Check("[文本] 首次落盘仍在 new HotkeyManager( 之前（更正 6 不得被破坏）",
            saveIdx >= 0 && hotkeyIdx > saveIdx,
            "Save 索引 < HotkeyManager 索引",
            $"Save={saveIdx}, HotkeyManager={hotkeyIdx}");

        // 首次落盘失败必须可见 —— 本任务的第二件事。
        // 只切出 CreatedDefault 那一小段来查，避免把别处的 ShowBalloon 误当成本处的证据。
        int blockStart = ctx.IndexOf("ConfigLoadStatus.CreatedDefault", StringComparison.Ordinal);
        int blockEnd = ctx.IndexOf("_decoder = new QrDecoder()", StringComparison.Ordinal);
        string saveBlock = blockStart >= 0 && blockEnd > blockStart
            ? ctx[blockStart..blockEnd]
            : string.Empty;

        Check("[文本] 首次落盘失败的 catch 里有 _tray.ShowBalloon(...)（绝不静默失败）",
            saveBlock.Contains("ShowBalloon(", StringComparison.Ordinal),
            "CreatedDefault 区间内含 ShowBalloon(",
            saveBlock.Length == 0 ? "未切出该区间" : $"区间长度 {saveBlock.Length}，"
                + $"含 ShowBalloon( = {saveBlock.Contains("ShowBalloon(", StringComparison.Ordinal)}");

        Check("[文本] 落盘失败的消息里含 configStore.FilePath（告诉用户写的是哪个文件）",
            saveBlock.Contains("configStore.FilePath", StringComparison.Ordinal),
            "含 configStore.FilePath",
            saveBlock.Contains("configStore.FilePath", StringComparison.Ordinal) ? "含" : "不含");

        Check("[文本] 自启修复不再内联在 TrayApplicationContext 里（避免两处实现）",
            !ctx.Contains("private void TryRepairStartupPath", StringComparison.Ordinal),
            "不存在 private void TryRepairStartupPath",
            ctx.Contains("private void TryRepairStartupPath", StringComparison.Ordinal) ? "仍存在" : "不存在");
    }

    // ───────────────────── 9. 异常即失败 ─────────────────────

    private static void CheckNoInternalErrors()
    {
        Check("前 8 节的反射 / 文件读取零内部异常",
            InternalErrors.Count == 0,
            "0 个",
            InternalErrors.Count == 0 ? "0 个" : $"{InternalErrors.Count} 个：{string.Join(" | ", InternalErrors)}");
    }

    // ───────────────────── 辅助 ─────────────────────

    /// <summary>
    /// 驱动一个场景，并同时钉住三件事：返回值、<c>SetEnabled</c> 的调用次数、
    /// 以及它的**实参序列**（修复必须是 <c>true</c>；传 <c>false</c> 会把用户的自启删掉）。
    /// </summary>
    private static void Scenario(string name, FakeStartup fake, bool expectResult, bool[] expectArgs)
    {
        (bool threw, bool result, string detail) = Call(fake, CurrentExe);

        Check($"{name} → 返回值",
            !threw && result == expectResult,
            threw ? "不抛出" : $"返回 {expectResult}",
            threw ? detail : $"返回 {result}");

        Check($"{name} → SetEnabled 调用次数",
            fake.SetEnabledCalls == expectArgs.Length,
            $"{expectArgs.Length} 次",
            $"{fake.SetEnabledCalls} 次");

        Check($"{name} → SetEnabled 实参序列（只允许 true：修复，绝不 false：删除）",
            fake.SetEnabledArgs.SequenceEqual(expectArgs),
            $"[{string.Join(", ", expectArgs)}]",
            $"[{string.Join(", ", fake.SetEnabledArgs)}]");
    }

    private static void Swallow(string name, FakeStartup fake, int expectCalls)
    {
        (bool threw, bool result, string detail) = Call(fake, CurrentExe);

        Check($"{name} → 吞掉且返回 false（不向调用方抛出）",
            !threw && result == false,
            "不抛且返回 false",
            threw ? detail : $"返回 {result}");

        // 对 SetEnabled 抛的情形额外断言"确实调用到了写入" ——
        // 否则"返回 false"可能只是因为它在更早的守卫处就返回了，什么都没验证到。
        if (expectCalls > 0)
        {
            Check($"{name} → 确实调用到了 SetEnabled（证明异常来自写入而非提前返回）",
                fake.SetEnabledCalls == expectCalls,
                $"{expectCalls} 次",
                $"{fake.SetEnabledCalls} 次");
        }
    }

    private static (bool Threw, bool Result, string Detail) Call(FakeStartup fake, string currentExe)
    {
        try
        {
            bool result = StartupPathRepair.RepairStartupPathIfNeeded(fake, currentExe);
            return (false, result, "未抛出");
        }
        catch (Exception ex)
        {
            return (true, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Describe(MethodInfo m) =>
        $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}("
        + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";

    private static void Section(string title, Action body)
    {
        Console.WriteLine($"----- {title} -----");

        // 记录本节实际产出了多少项断言 —— 汇总时逐节列出，
        // 这样"某一节是不是悄悄变空了"能直接看出来，而不必去数源码。
        int before = _assertions;
        try
        {
            body();
        }
        catch (Exception ex)
        {
            InternalErrors.Add($"{title}: {ex.GetType().Name}: {ex.Message}");
        }
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

    private static string ReadSource(string relativePath)
    {
        string root = ResolveRepoRoot();

        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// 定位仓库根。
    ///
    /// 优先读环境变量 <c>QRSCAN_REPO_ROOT</c>：本探针会被 run-mutations.sh 在**临时目录**里
    /// 用变异副本重新构建并运行，那时从程序目录向上是找不到 <c>QrScan.sln</c> 的。
    /// 该变量也让"调用点文本变异"成为可能 —— 脚本可以把一份**变异过的**
    /// <c>TrayApplicationContext.cs</c> 放进一个假的仓库根，让 §8 的文本断言去读它。
    /// 直接从仓库里运行时，向上找 <c>QrScan.sln</c> 仍然是通的那条路。
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
            throw new InvalidOperationException(
                "找不到仓库根：既没有 QRSCAN_REPO_ROOT 环境变量，也无法从程序目录向上找到 QrScan.sln。");

        return dir.FullName;
    }

    /// <summary>
    /// 假的 <see cref="IStartupManager"/>：把"读到什么、写到哪去了"全部记下来，
    /// 且可以让指定成员抛指定异常。**它不碰真实注册表。**
    /// </summary>
    private sealed class FakeStartup : IStartupManager
    {
        public bool Enabled { get; set; }
        public string? Path { get; set; }

        /// <summary>读 <see cref="IsEnabled"/> 时抛这个异常。</summary>
        public Exception? EnableOnReadThrows { get; set; }

        /// <summary>读 <see cref="RegisteredPath"/> 时抛这个异常。</summary>
        public Exception? PathOnReadThrows { get; set; }

        /// <summary>写 <see cref="SetEnabled"/> 时抛这个异常。</summary>
        public Exception? WriteThrows { get; set; }

        /// <summary>
        /// <c>SetEnabled(true)</c> 之后把 <see cref="Path"/> 更新为这个值 ——
        /// 模仿真实 <c>StartupManager</c> 的状态变更，使幂等性可被观察。
        /// </summary>
        public string? PathAfterEnable { get; set; }

        public int IsEnabledReads { get; private set; }
        public int RegisteredPathReads { get; private set; }
        public int SetEnabledCalls { get; private set; }
        public List<bool> SetEnabledArgs { get; } = new();

        public bool IsEnabled
        {
            get
            {
                IsEnabledReads++;
                if (EnableOnReadThrows is not null) throw EnableOnReadThrows;
                return Enabled;
            }
        }

        public string? RegisteredPath
        {
            get
            {
                RegisteredPathReads++;
                if (PathOnReadThrows is not null) throw PathOnReadThrows;
                return Path;
            }
        }

        public void SetEnabled(bool enabled)
        {
            SetEnabledCalls++;
            SetEnabledArgs.Add(enabled);

            if (WriteThrows is not null) throw WriteThrows;

            Enabled = enabled;
            if (enabled)
            {
                if (PathAfterEnable is not null) Path = PathAfterEnable;
            }
            else
            {
                Path = null;
            }
        }
    }
}
