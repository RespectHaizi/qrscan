using System.Drawing.Drawing2D;
using Timer = System.Windows.Forms.Timer;

namespace QrScan.UI;

/// <summary>提示条的语义类别，决定背景色。</summary>
internal enum ToastKind
{
    Success,
    Failure,
    Info,
}

/// <summary>提示条上的一个动作按钮。<paramref name="Id"/> 是调用方约定的动作标识，<paramref name="Label"/> 是显示文字。</summary>
internal sealed record ToastAction(string Id, string Label);

/// <summary>
/// 无边框提示条。
///
/// 两个不可协商的设计点：
/// 1. <c>WS_EX_NOACTIVATE</c> —— 弹出时绝不抢焦点，否则会打断用户正在输入的窗口；
/// 2. 鼠标悬停时暂停淡出计时 —— 否则你正要点「打开」它消失了。
///
/// 按钮为自绘 + 命中测试，而非子控件：既避免了子控件的焦点语义，
/// 也让"不激活也能点击"的行为完全确定。
/// </summary>
internal sealed class ToastWindow : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;   // ★ 不抢焦点
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // ★ 不进 Alt+Tab

    private const int ContentPadding = 14;
    private const int ButtonHeight = 26;
    private const int ButtonSpacing = 8;
    private const int ButtonPaddingX = 14;

    /// <summary>按钮标签的度量画布：够大到不会成为约束，又不会让 TextRenderer 内部算术溢出。</summary>
    private const int LabelMeasureBox = 4096;

    /// <summary>
    /// 指针位置的来源。默认就是 <see cref="Cursor.Position"/>。
    ///
    /// 之所以留成一个 <c>internal</c>、可替换的接缝：探针环境里无法真的移动鼠标
    /// （<see cref="Cursor.Position"/> 只读），而「提示条绝不落在指针底下」这条不变量
    /// 必须能被自动验证。生产代码从不读写它。
    /// </summary>
    internal static Func<Point> PointerPositionSource { get; set; } = static () => Cursor.Position;

    private readonly ToastAction[] _actions;
    private readonly Timer _fadeTimer;
    private readonly int _dismissMs;

    private Rectangle[] _buttonRects = Array.Empty<Rectangle>();
    private int _hoveredButton = -1;
    private bool _hovering;
    private DateTime _shownAt;

    public event Action<ToastAction>? ActionClicked;

    public ToastWindow(string message, ToastAction[] actions, Point atPhysical, int dismissMs, ToastKind kind)
    {
        _actions = actions;
        _dismissMs = dismissMs;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f);

        BackColor = kind switch
        {
            ToastKind.Failure => Color.FromArgb(200, 40, 40),
            ToastKind.Success => Color.FromArgb(32, 32, 32),
            _ => Color.FromArgb(48, 48, 48),
        };

        Message = message;
        Kind = kind;

        Size = MeasureToast();

        // 用**实际尺寸**做 clamp，而不是某个尺寸上界估计。
        // 实测教训：原先用 (360,120) 作估计，但它并不是上界 ——
        // 正文宽度上限 340 + 左右各 14 = 368 > 360，高度更是随换行无限增长。
        // 于是 "保存到屏幕内" 这个保证会漏 —— 实测一个 366×197 的提示条被放在
        // (1560,960)：右边界 1926（越 6px）、下边界 1157（越 77px，约四成高度跑到屏外）。
        //
        // 同时要避让指针：锚点常常就是用户松开鼠标的位置（选区右下角），
        // 提示条若落在指针底下会被 OnMouseEnter 永久暂停淡出。详见 PlaceAwayFromPointer。
        Location = PlaceAwayFromPointer(atPhysical, Size);

        _fadeTimer = new Timer { Interval = 50 };
        _fadeTimer.Tick += OnFadeTick;
    }

    public string Message { get; }

    public ToastKind Kind { get; }

    protected override bool ShowWithoutActivation => true;    // ★ 不抢焦点的第一道保险

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;  // ★ 第二道（含：不进 Alt+Tab）
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 用实际尺寸再 clamp 一次。
        //
        // 实测事实（请注意）：**本次赋值在 Show() 期间不会生效** ——
        // 建句柄后立即读原生矩形仍是构造期的位置（例：1560），而同样一次 OnShown
        // 在 Show() 返回后再手动调用却能把窗口移到 1552。也就是说真正保证
        // "不出屏幕" 的是构造函数里那一句，这里是一道冗余保险（在后续重显等时机才起作用）。
        // 保留它是因为它是简报指定的形状，且对未来的尺寸变更仍然有意义。
        // 走同一套避让逻辑（含指针避让）：这道保险在后续重显等时机确实会生效，
        // 因此不能让这条路径把提示条挪回指针底下。
        Location = PlaceAwayFromPointer(new Point(Left, Top), Size);

        _shownAt = DateTime.UtcNow;
        _fadeTimer.Start();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovering = true;                 // 悬停暂停淡出
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovering = false;
        _hoveredButton = -1;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int hit = HitTest(e.Location);
        if (hit != _hoveredButton)
        {
            _hoveredButton = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // 只响应左键：右键点在按钮上不应触发动作。提示条上"取消/关闭"的直觉手势就是右键，
        // 若这里不区分鼠标键，右键会意外执行「打开」。
        if (e.Button != MouseButtons.Left)
            return;

        int hit = HitTest(e.Location);
        if (hit >= 0)
        {
            ActionClicked?.Invoke(_actions[hit]);
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.PageUnit = GraphicsUnit.Pixel;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var border = new Pen(Color.FromArgb(90, 255, 255, 255));
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var textRect = new Rectangle(ContentPadding, ContentPadding, Width - ContentPadding * 2, Height - ContentPadding * 2);
        TextRenderer.DrawText(g, Message, Font, textRect, Color.White,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        for (int i = 0; i < _buttonRects.Length; i++)
        {
            Rectangle r = _buttonRects[i];
            bool hovered = i == _hoveredButton;

            using var fill = new SolidBrush(hovered
                ? Color.FromArgb(70, 255, 255, 255)
                : Color.FromArgb(35, 255, 255, 255));
            g.FillRectangle(fill, r);

            using var pen = new Pen(Color.FromArgb(140, 255, 255, 255));
            g.DrawRectangle(pen, r);

            TextRenderer.DrawText(g, _actions[i].Label, Font, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _fadeTimer.Stop();
        _fadeTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        if (_hovering) return;                                   // 悬停时暂停

        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds >= _dismissMs)
            Close();
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _buttonRects.Length; i++)
            if (_buttonRects[i].Contains(p))
                return i;

        return -1;
    }

    private Size MeasureToast()
    {
        const int MaxTextWidth = 340;

        // 用 TextRenderer 而非 Graphics.MeasureString：
        // (1) 不需要在构造函数里就强制创建窗体句柄；
        // (2) 与 OnPaint 中的 TextRenderer.DrawText 是同一套度量，不会出现"量出来 2 行、画出来 3 行"。
        //
        // TextFormatFlags 必须与 OnPaint 那边**逐位一致**（含 NoPrefix）。不一致会"量出来一个尺寸、
        // 画出来另一个"；而 NoPrefix 尤其隐蔽：少了它，正文里的 `&` 会被当成助记符前缀吞掉、
        // 其后一个字符被加上下划线 —— 一条 `https://x.com/a?b=1&c=2` 会显示成 `a?b=1c=2`。
        Size textSize = TextRenderer.MeasureText(
            Message, Font, new Size(MaxTextWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        int textWidth = Math.Min(textSize.Width, MaxTextWidth);

        int buttonsWidth = 0;
        if (_actions.Length > 0)
        {
            foreach (var action in _actions)
            {
                Size s = TextRenderer.MeasureText(action.Label, Font,
                    new Size(LabelMeasureBox, LabelMeasureBox), TextFormatFlags.NoPrefix);
                buttonsWidth += s.Width + ButtonPaddingX * 2;
            }

            buttonsWidth += ButtonSpacing * (_actions.Length - 1);
        }

        int contentWidth = Math.Max(textWidth, buttonsWidth);
        int width = contentWidth + ContentPadding * 2;
        int height = ContentPadding * 2 + textSize.Height;

        if (_actions.Length > 0)
        {
            height += ButtonSpacing + ButtonHeight;

            // 版面确定后再算按钮矩形（与 MeasureToast 用同一套参数）
            int x = ContentPadding;
            int y = height - ContentPadding - ButtonHeight;
            _buttonRects = new Rectangle[_actions.Length];
            int available = width - ContentPadding * 2;
            int each = (available - ButtonSpacing * (_actions.Length - 1)) / _actions.Length;

            for (int i = 0; i < _actions.Length; i++)
            {
                _buttonRects[i] = new Rectangle(x, y, each, ButtonHeight);
                x += each + ButtonSpacing;
            }
        }

        return new Size(width, height);
    }

    /// <summary>
    /// 算出提示条的位置，并保证它**不会盖住指针**。
    ///
    /// 为什么需要：调用方把锚点设为选区的右下角，而那正是用户松开鼠标的位置（向右下
    /// 拖拽时）。若提示条左上角恰好落在指针之下，`OnMouseEnter` 会立刻触发、`_hovering`
    /// 变 true，于是**淡出被无限暂停** —— 提示条永远不消失，与「2 秒后自动淡出」的设计
    /// 相悖，而且没有任何报错（看起来只是「提示条赖着不走」）。
    ///
    /// 依次尝试：原位 → 指针上方 → 指针左侧 → 指针右侧，每个候选都重新 clamp
    /// （挪动不能把提示条推出桌面）。指针不在原位矩形内时（常见情形）第一个候选即返回，
    /// 行为与引入避让之前完全一致。
    /// </summary>
    private static Point PlaceAwayFromPointer(Point anchor, Size toastSize)
    {
        const int Gap = 12;

        Point pointer = PointerPositionSource();

        // 排序：原位优先，其上是「指针上方」（提示条通常就贴在选区下方，往上挪最自然），
        // 然后是左、右。左右两条在指针贴近上下边缘时才是唯一可行的选择。
        Point[] candidates =
        {
            anchor,
            new Point(anchor.X, pointer.Y - Gap - toastSize.Height),
            new Point(pointer.X - Gap - toastSize.Width, anchor.Y),
            new Point(pointer.X + Gap, anchor.Y),
        };

        foreach (Point candidate in candidates)
        {
            Point placed = ClampToVirtualScreen(candidate, toastSize);
            if (!new Rectangle(placed, toastSize).Contains(pointer))
                return placed;
        }

        // 四个候选都被 clamp 拽回指针下方（提示条比可用空间还大，或指针贴着桌面边角）。
        // 退回原位 —— 与引入避让之前的行为一致，至少不比它差。
        return ClampToVirtualScreen(anchor, toastSize);
    }

    /// <summary>
    /// 把锚点 clamp 到虚拟桌面内，保证提示条**完整可见且不出边界**。
    /// 必须传入提示条的**实际尺寸** —— 任何小于实际尺寸的估计都会让这个保证漏。
    /// </summary>
    private static Point ClampToVirtualScreen(Point requested, Size toastSize)
    {
        Rectangle vs = SystemInformation.VirtualScreen;

        int x = Math.Clamp(requested.X, vs.Left, Math.Max(vs.Left, vs.Right - toastSize.Width));
        int y = Math.Clamp(requested.Y, vs.Top, Math.Max(vs.Top, vs.Bottom - toastSize.Height));
        return new Point(x, y);
    }
}
