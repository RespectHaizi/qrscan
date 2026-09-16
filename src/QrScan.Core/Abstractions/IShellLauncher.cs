namespace QrScan.Core;

/// <summary>
/// 唯一的"执行外部动作"出口。抽成接口是为了让"自定义 scheme 绝不触达启动器"
/// 可被测试断言，而不必真的启动进程。
/// </summary>
public interface IShellLauncher
{
    /// <summary>
    /// 打开目标。可接受的目标只有两类：白名单 URL（<c>http</c>/<c>https</c>/<c>mailto</c>/<c>tel</c>），
    /// 或"确实存在的本地文件/目录"（由 <see cref="PayloadClassifier.Classify"/> 判定为
    /// <see cref="Models.PayloadKind.FilePath"/>）。
    ///
    /// <para>
    /// **这是由实现强制执行的契约，不只是调用方的义务。** <see cref="ShellLauncher"/> 会
    /// 先复核 <see cref="PayloadClassifier.CanOpen"/>，不符合就抛
    /// <see cref="System.InvalidOperationException"/>，**绝不触达进程启动**。
    /// 所以即使调用点写错，也不会静默把一个自定义 scheme 交给系统协议处理器执行。
    /// </para>
    ///
    /// <para>
    /// 测试替身与后续调用方（如 UI 层的 <c>ResultPresenter</c>）仍应遵循同一约定：
    /// **先判 <see cref="PayloadClassifier.CanOpen"/> 再调用**。
    /// 这里的强制只是纵深防御，不该被当作可以省略前置检查的理由 ——
    /// 一个静默被拒的调用会让用户看到"点了没反应"，而前置检查能给出可读提示。
    /// </para>
    /// </summary>
    /// <param name="target">要打开的目标。前后空白会被忽略。</param>
    /// <exception cref="System.ArgumentException">目标为 null 或空白（调用方 bug）。</exception>
    /// <exception cref="System.InvalidOperationException">目标不在白名单内（被实现拒绝）。</exception>
    void Open(string target);
}
