using System.Drawing.Drawing2D;

namespace QrScan.UI;

/// <summary>
/// 框选遮罩。背景是**冻结位图**（不是实时桌面），因此：
/// 1) 用户框选的区域与送去解码的像素逐像素一致；
/// 2) 遮罩自身绝不会被截进去（调用方保证了"先截屏后显示"的顺序）。
///
/// 这是一个"物理像素画布"：<see cref="Form.AutoScaleMode"/> 设为 <see cref="AutoScaleMode.None"/>，
/// 全程不参与 WinForms 的 DPI 缩放，跨屏不同缩放不会产生累积误差。
/// </summary>
/// <remarks>
/// **所有权**：构造时传入的冻结位图由 <c>AppController</c> 拥有并负责释放，本窗体
/// 只读不释放 —— 因此这里没有 <c>Dispose(bool)</c> 覆写。在全程序四条退出路径上
/// （取消 / 无码 / 成功 / 抛异常）统一由 <c>AppController</c> 的 <c>try/finally</c> 释放它。
/// </remarks>
internal sealed class OverlayWindow : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private const int MinimumDragPixels = 3;

    private readonly Bitmap _frozen;
    private readonly Rectangle _virtualBounds;

    private Point _anchor;
    private Point _current;
    private bool _dragging;

    /// <summary>选区的**屏幕物理像素**坐标；用户取消时为 null。</summary>
    public Rectangle? SelectedRegionPhysical { get; private set; }

    public OverlayWindow(Bitmap frozenScreen, Rectangle virtualBoundsPhysical)
    {
        _frozen = frozenScreen;
        _virtualBounds = virtualBoundsPhysical;

        AutoScaleMode = AutoScaleMode.None;          // ★ 关键：绕开 WinForms DPI 缩放
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;

        Bounds = virtualBoundsPhysical;              // 直接就是物理像素矩形
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Windows 可能按它建议的矩形缩放/挪动我们（混合 DPI 下），重新断言。
        Bounds = _virtualBounds;
        Activate();
        Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right)
        {
            Cancel();
            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        _dragging = true;
        _anchor = e.Location;
        _current = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging) return;

        _current = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (!_dragging || e.Button != MouseButtons.Left) return;

        _dragging = false;
        Rectangle local = Normalize(_anchor, e.Location);

        if (local.Width < MinimumDragPixels || local.Height < MinimumDragPixels)
        {
            Cancel();
            return;
        }

        // 唯一一次坐标换算：客户区（== 遮罩局部）→ 屏幕物理像素
        SelectedRegionPhysical = new Rectangle(
            local.X + _virtualBounds.X,
            local.Y + _virtualBounds.Y,
            local.Width,
            local.Height);

        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Escape)
            Cancel();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.PageUnit = GraphicsUnit.Pixel;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // 等尺寸目标矩形 → 严格 1:1，不用 DrawImageUnscaled（其行为受 Graphics.DpiX/DpiY 影响）
        g.DrawImage(_frozen, new Rectangle(0, 0, _frozen.Width, _frozen.Height));

        Rectangle selection = _dragging ? Normalize(_anchor, _current) : Rectangle.Empty;

        using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
        using var region = new Region(new Rectangle(0, 0, _frozen.Width, _frozen.Height));
        if (selection.Width > 0 && selection.Height > 0)
            region.Exclude(selection);
        g.FillRegion(dim, region);

        if (selection.Width > 0 && selection.Height > 0)
        {
            using var border = new Pen(Color.FromArgb(255, 0, 160, 255), 2);
            g.DrawRectangle(border, selection);

            DrawSizeLabel(g, selection);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            // 忽略系统建议矩形，重新断言我们的物理像素边界。
            Bounds = _virtualBounds;
            Invalidate();
            return;
        }

        base.WndProc(ref m);
    }

    private void DrawSizeLabel(Graphics g, Rectangle selection)
    {
        string text = $"{selection.Width} × {selection.Height}";
        SizeF size = g.MeasureString(text, Font);
        float x = selection.Left;
        float y = selection.Top - size.Height - 6;
        if (y < 0) y = selection.Bottom + 6;                      // 顶到屏幕边缘时挪到下方

        using var background = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        g.FillRectangle(background, x, y, size.Width + 8, size.Height + 4);
        g.DrawString(text, Font, Brushes.White, x + 4, y + 2);
    }

    private void Cancel()
    {
        SelectedRegionPhysical = null;
        Close();
    }

    private static Rectangle Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));
}
