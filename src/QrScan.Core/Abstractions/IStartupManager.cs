namespace QrScan.Core;

/// <summary>
/// 开机自启的开关。抽成接口是因为它读写注册表（有环境副作用），
/// 按项目规则"有副作用/环境依赖 → 实例 + 接口"；
/// 并且键路径可注入，使"读到的是什么、写到哪去了"能被真实验证。
/// </summary>
public interface IStartupManager
{
    /// <summary>
    /// 当前是否已启用开机自启。
    /// 由 <see cref="RegisteredPath"/> 是否为 null 决定，因此读取失败时视为未启用。
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 注册表中记录的可执行文件路径（已去掉引号与参数）；未启用时为 null。
    ///
    /// <para>
    /// 读取这件事本身是需求的组成部分：用户把 exe 从下载文件夹挪到桌面后，
    /// 注册表里的路径会**永久指向旧位置**，而托盘菜单里的勾还稳稳地打着 ——
    /// 一个非常隐蔽的失效。调用方靠比对它与 <see cref="StartupManager.CurrentExecutablePath"/>
    /// 来发现并修复这种情况。
    /// </para>
    /// </summary>
    string? RegisteredPath { get; }

    /// <summary>
    /// 启用或关闭开机自启。
    /// 关闭时对"本来就不存在"是幂等的，不抛异常。
    /// </summary>
    /// <param name="enabled">true 写入当前 exe 路径；false 移除该项。</param>
    void SetEnabled(bool enabled);
}
