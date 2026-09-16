namespace QrScan.Core.Models;

/// <summary>识别出的文本类别，决定提示条上是否提供「打开」。</summary>
public enum PayloadKind
{
    /// <summary>白名单协议（http/https/mailto/tel）。</summary>
    Url,

    /// <summary>确实存在的本地文件或目录。</summary>
    FilePath,

    /// <summary>其余一切：纯文本、WiFi 码、vCard、自定义 scheme。</summary>
    PlainText,
}
