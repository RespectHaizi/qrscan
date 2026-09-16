"""生成 QrScan 的测试样张。可重复执行（幂等）。

用法：python tools/make-test-assets.py
"""
import io
import os
from PIL import Image, ImageFilter

try:
    import segno
except ImportError:
    raise SystemExit("请先安装依赖： python -m pip install segno pillow")

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "tests", "QrScan.Core.Tests", "Assets")
os.makedirs(OUT, exist_ok=True)

URL_A = "https://example.com/hello?from=qrscan"
URL_B = "https://example.org/second-code"
URL_TINY = "https://example.com/tiny"
PLAIN = "hello world, this is plain text"
WIFI = "WIFI:T:WPA;S:mynet;P:s3cret;;"


def render(text, scale=8, border=2):
    """把文本渲染成 RGB 位图。

    用 segno 的 save(kind='png') 写出到内存再读回，而不是 to_pil() ——
    segno 1.6.6 没有 to_pil（它属于可选的 PIL 插件，本环境不可用）。
    """
    buffer = io.BytesIO()
    segno.make(text, micro=False, error="m").save(
        buffer, kind="png", scale=scale, border=border)
    buffer.seek(0)
    return Image.open(buffer).convert("RGB")


def make(text, scale=8, border=2):
    return render(text, scale=scale, border=border)


def save(img, name):
    path = os.path.join(OUT, name)
    img.save(path)
    return path


# 1) 标准码
save(make(URL_A), "standard.png")
# 2) 反色码（白码深底）—— 深色主题网页的常见形态
inverted = Image.eval(make(URL_A).convert("L"), lambda p: 255 - p).convert("RGB")
save(inverted, "inverted.png")
# 3) 模糊 + 缩小 —— 模拟屏幕上被缩放过的图
save(make(URL_A).convert("RGB").resize((140, 140)).filter(ImageFilter.GaussianBlur(0.6)), "blurred.png")
# 4) 缩略图小码
save(render(URL_TINY, scale=3, border=1), "tiny.png")
# 5) 旋转 45 度
save(make(URL_A).convert("RGB").rotate(45, expand=True, fillcolor="white"), "rotated.png")
# 6) 一图两码：左 = URL_A，右 = URL_B
a, b = make(URL_A), make(URL_B)
canvas = Image.new("RGB", (a.width + b.width + 80, max(a.height, b.height)), "white")
canvas.paste(a, (0, 0))
canvas.paste(b, (a.width + 80, 0))
save(canvas, "multi.png")
# 7) 纯文本
save(make(PLAIN), "plaintext.png")
# 8) WiFi 码
save(make(WIFI), "wifi.png")
# 9) 无二维码
save(Image.new("RGB", (400, 120), "white"), "nocode.png")
# 10) 启动自检用（21x21 version-1，内容 "QRSCAN"）
save(render("QRSCAN", scale=8, border=4), "selftest.png")

print("已生成到", OUT)
for name in sorted(os.listdir(OUT)):
    print("  ", name)
