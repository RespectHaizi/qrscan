namespace QrScan.Core.Models;

/// <summary>一次识别结果。</summary>
public sealed record QrPayload
{
    /// <summary>二维码承载的文本。</summary>
    public required string Text { get; init; }

    /// <summary>符号类型，例如 "QR Code"。</summary>
    public required string Format { get; init; }

    /// <summary>是否为反色码（白码深底）。供 UI 提示用，不影响复制内容。</summary>
    public required bool IsInverted { get; init; }

    /// <summary>在送入识别的图像中的左上角 X（局部物理像素）。</summary>
    public required int Left { get; init; }

    /// <summary>在送入识别的图像中的左上角 Y（局部物理像素）。</summary>
    public required int Top { get; init; }
}
