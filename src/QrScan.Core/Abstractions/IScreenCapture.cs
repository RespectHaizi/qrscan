using System.Drawing;

namespace QrScan.Core;

/// <summary>屏幕几何与冻结位图采集。有环境依赖 → 实例 + 接口（见规格 §4.1 的规则）。</summary>
public interface IScreenCapture
{
    /// <summary>虚拟桌面的物理像素矩形。**原点可以是负数**（副屏在主屏左/上时）。</summary>
    Rectangle VirtualScreenPhysical();

    /// <summary>抓取整个虚拟桌面。调用方负责 <c>Dispose</c>。</summary>
    Bitmap CaptureVirtualScreen();
}
