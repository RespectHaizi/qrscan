using QrScan.Core;

namespace QrScan;

/// <summary>
/// 启动期对"开机自启指向了一个已经不在那儿的路径"的自动修复。
///
/// <para>
/// <b>它修的是什么</b>：用户把 <c>QrScan.exe</c> 从下载文件夹挪到桌面后，注册表
/// <c>HKCU\...\Run</c> 里的路径会<b>永久指向旧位置</b> —— 而托盘菜单里的勾还稳稳地打着。
/// 两个可见信号都"正常"，开机却根本不会启动。这类失效极难被发现，因为没有任何一处会报错。
/// </para>
///
/// <para>
/// 单独成文件而不是内联在 <see cref="TrayApplicationContext"/> 的构造函数里，是为了让它
/// 能被探针直接驱动：路径<b>通过参数传入</b>，本类不读注册表、不读环境，于是一个假的
/// <see cref="IStartupManager"/> 就能把它每条行为逐条钉死。
/// </para>
/// </summary>
internal static class StartupPathRepair
{
    /// <summary>
    /// 是否存在"自启指向的路径与当前进程的路径不一致、需要重写"的情况。
    ///
    /// <para>
    /// <b>只判断，不写入</b> —— 副作用全部留在 <see cref="RepairStartupPathIfNeeded"/>。
    /// 它是"需不需要修"的<b>唯一</b>判据：写入路径与调用方汇报失败的那条路径都取自这里，
    /// 两处若各写一份比较逻辑，就可能对"是否已一致"给出不同答案，
    /// 而那种分歧的表现恰好是"该报的不报、不该报的乱报"。
    /// </para>
    /// </summary>
    internal static bool NeedsRepair(IStartupManager startup, string currentExePath)
    {
        if (!startup.IsEnabled)
            return false;

        string? registered = startup.RegisteredPath;

        // 读取失败（IsEnabled 为真却拿不到路径）时不猜、不写。
        if (registered is null)
            return false;

        // Windows 路径大小写不敏感：C:\a\Q.exe 与 c:\A\q.exe 是同一个文件。
        // 用 Ordinal 会把它们判成不同，于是每次启动都白白重写一次。
        if (string.Equals(registered, currentExePath, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// 若自启已启用、但注册表里记录的路径与 <paramref name="currentExePath"/> 不一致，
    /// 就用当前路径重写一次。
    ///
    /// <para>
    /// <paramref name="currentExePath"/> 必须传 <see cref="StartupManager.CurrentExecutablePath"/>：
    /// 本方法只负责<b>判断</b>是否不一致，真正的写入由 <see cref="IStartupManager.SetEnabled"/>
    /// 完成，而后者写的始终是"当前进程的可执行文件路径"。两者取同一个值，
    /// "比对"与"重写"才指向同一个目标。
    /// </para>
    /// </summary>
    /// <returns>确实做了修复时为 true；其余一切情况为 false。</returns>
    internal static bool RepairStartupPathIfNeeded(IStartupManager startup, string currentExePath)
    {
        try
        {
            if (!NeedsRepair(startup, currentExePath))
                return false;

            startup.SetEnabled(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException
                                      or InvalidOperationException)
        {
            // 修复失败不影响本次运行：这一项本次不修，下次启动还会再试一次。
            return false;
        }
    }

    /// <summary>
    /// 生产入口：自己取当前 exe 路径，并把"取不到路径"这种启动期异常也一并吞掉。
    ///
    /// <para>
    /// 之所以需要额外这一层：<see cref="StartupManager.CurrentExecutablePath"/> 在宿主异常
    /// （<c>Environment.ProcessPath</c> 为 null）时会抛 <see cref="InvalidOperationException"/>，
    /// 而那是<b>参数求值</b>，发生在 <see cref="RepairStartupPathIfNeeded"/> 的 try 之外 ——
    /// 落在构造函数里就变成"整个托盘程序起不来"，为了修一个隐蔽的小失效而制造一个显眼的大故障。
    /// </para>
    ///
    /// <para>
    /// <b>返回值的含义是"这件事现在是不是好的"</b>，而不是"本次有没有写入"：
    /// <list type="bullet">
    ///   <item><c>true</c> —— 已与当前路径一致（含"本来就无需修"与"刚修好了"）。</item>
    ///   <item><c>false</c> —— 需要修、但没能修成。</item>
    /// </list>
    /// 调用方据此决定要不要提示用户。若沿用"有没有写"的含义，那么
    /// <b>每一次自启本已正确的正常启动</b>都会被报成"修复失败"。
    /// </para>
    /// </summary>
    internal static bool RepairUsingCurrentExecutable(IStartupManager startup)
    {
        string currentExePath;

        try
        {
            currentExePath = StartupManager.CurrentExecutablePath();
        }
        catch (InvalidOperationException)
        {
            // 取不到自身路径（Environment.ProcessPath 为 null 的异常宿主）→ 无法判断。
            // 这里**不**报成"修复失败"：那种宿主下自启本来就没有意义，
            // 而误报会让每一次启动都弹一个用户无从处理的提示。
            return true;
        }

        // 尝试修复。失败会被它自己吞掉 —— 那正是它的契约（构造函数不该因此崩掉）。
        RepairStartupPathIfNeeded(startup, currentExePath);

        // 修完之后"自启是否已指向当前路径"就是成功判据，它同时覆盖
        // "本来就无需修"（无需修复 ≠ 失败）与"刚修好了"两种情形；
        // 只有"需要修却没修成"才会落到 false。
        return IsAlreadyConsistent(startup, currentExePath);
    }

    /// <summary>
    /// 修复后自启是否已与当前路径一致。读取失败一律视为"不一致"（无法确认就算没修好）。
    /// </summary>
    private static bool IsAlreadyConsistent(IStartupManager startup, string currentExePath)
    {
        try
        {
            return !NeedsRepair(startup, currentExePath);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException
                                      or InvalidOperationException)
        {
            // 读不回来 → 无法确认已经修好，按"没修好"上报。
            return false;
        }
    }
}
