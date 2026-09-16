using QrScan.Core;

namespace QrScan;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!SingleInstance.TryAcquire(out IDisposable? release))
        {
            // 全局约束：绝不弹 MessageBox。这里是唯一的例外 ——
            // 第二个实例还没有托盘图标可用，只能靠模态框告知。
            MessageBox.Show(
                "QrScan 已在运行（见系统托盘）。",
                "QrScan",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using (release)
        {
            // DPI 感知必须在任何窗体创建之前生效
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
    }
}
