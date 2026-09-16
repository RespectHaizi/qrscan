#!/usr/bin/env bash
#
# ToastWindowProbe 变异灵敏度复核（可一键重跑）
#
# ── 它解决什么问题 ─────────────────────────────────────────────────────────────
# 「探针有鉴别力」这件事本身也会悄悄失效：一条断言可能因为写成恒真式、或因为场景
# 选得不对（例如驱动用的尺寸小于实际值，于是被测分支根本没走到），表现得全绿却什么
# 都没验证。报告里说「变异全部被抓住」如果是跑在某个临时副本上、没留在仓库里，控制者
# 与 CI 都无法复核。
#
# 本脚本用**真实变异**来复核：把一个故意写错的 ToastWindow.cs 交给**同一份**探针，
# 要求探针变红。
#
# ── 做法 ──────────────────────────────────────────────────────────────────────
#   0. **对照组**：先拿仓库里真实的源文件跑一次，要求探针**全绿退出 0**。
#      没有这一步，"变异让探针变红"就可能只是因为探针本来就红着。
#   1. 读仓库里真实的 src/QrScan/UI/ToastWindow.cs；
#   2. 每个变异对它做**一处**文本替换，写成一个临时副本；
#   3. 生成一个 csproj：编译**仓库里真实的那份** Program.cs + 变异副本；
#   4. 跑完整探针，**以"探针跑完且声明了 ≥1 项断言失败"为通过**；
#   5. 任一变异未能让探针变红 → 脚本自己以非 0 退出。
#
# **绝不修改仓库里的任何源文件** —— 全部副本都在临时目录，退出时删除。
#
# ── 判据为什么不是退出码 ──────────────────────────────────────────────
# "构建失败"与"项目找不到"同样会给出非 0 退出码，那样会把「变异根本没跑起来」
# 误报成「变异被抓住了」。本脚本的第一版就犯过这个错（names.txt 被写成
# CRLF，路径全错，于是 12/12 全是假通过）。所以必须要求：探针确实跑完并打印了汇总行，
# 且**声明了至少 1 项断言失败**。
#
# ── 依赖 ────────────────────────────────────────────────────────────────────
# bash、python（本仓库已用 python 生成测试样张）、.NET SDK 8。
#
# ── 用法 ────────────────────────────────────────────────────────────────────
#   bash tools/ToastWindowProbe/run-mutations.sh
#
# 退出码：0 = 对照组全绿且全部变异都被抓住；非 0 = 有变异逃逸/无效，或对照组本身红。

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/src/QrScan/UI/ToastWindow.cs"
PROGRAM="$ROOT/tools/ToastWindowProbe/Program.cs"

export DOTNET_ROOT="${DOTNET_ROOT:-C:\\Users\\dell\\.dotnet}"
export PATH="/c/Users/dell/.dotnet:$PATH"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

command -v python >/dev/null 2>&1 || { echo "需要 python"; exit 2; }
[ -f "$SRC" ] || { echo "找不到源文件：$SRC"; exit 2; }
[ -f "$PROGRAM" ] || { echo "找不到探针源码：$PROGRAM"; exit 2; }

# MSBuild 读 csproj 里的绝对路径时不认 MSYS 的 /c/... 形式，要转成 Windows 形式。
win_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

echo "===== ToastWindowProbe 变异复核 ====="
echo "源文件（只读）：$SRC"
echo "探针源码（只读）：$PROGRAM"
echo "临时目录：$WORK"
echo
# ── 阶段 1：生成每个变异的源文件副本与 csproj ─────────────────────────────────
python - "$SRC" "$PROGRAM" "$WORK" "$(win_path "$PROGRAM")" "$(win_path "$SRC")" <<'PY' || exit 2
import io, os, sys

src_path, probe_path, work, program_win, source_win = sys.argv[1:6]
original = io.open(src_path, encoding='utf-8').read()

# 每一处替换都必须**恰好命中预期次数**，否则说明源文件已漂移、这个变异不再是有意义的
# 复核（例如"删掉正文绘制"结果一处都没删，那这个变异当然抓不到任何东西）。
MUTATIONS = [
    ("noNoActivate",
     "去掉 WS_EX_NOACTIVATE（第二道保险）",
     [("cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;",
       "cp.ExStyle |= WS_EX_TOOLWINDOW;", 1)]),

    ("noShowWithoutActivation",
     "让 ShowWithoutActivation 返回 false（第一道保险）",
     [("protected override bool ShowWithoutActivation => true;",
       "protected override bool ShowWithoutActivation => false;", 1)]),

    ("noHoverGuard",
     "去掉 if (_hovering) return;（悬停不再暂停淡出）",
     [("if (_hovering) return;", "if (false) return; /* mutated */", 1)]),

    ("timerNeverStarts",
     "不再启动淡出 Timer",
     [("_fadeTimer.Start();", "", 1)]),

    ("noHitTest",
     "命中测试恒不命中",
     [("""        for (int i = 0; i < _buttonRects.Length; i++)
            if (_buttonRects[i].Contains(p))
                return i;""",
       "        // mutated: 命中测试恒不命中", 1)]),

    ("noMessageText",
     "不再绘制正文",
     [("""        TextRenderer.DrawText(g, Message, Font, textRect, Color.White,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);""",
       "        // mutated: 正文不再绘制", 1)]),

    ("noButtonLabels",
     "不再绘制按钮标签",
     [("""            TextRenderer.DrawText(g, _actions[i].Label, Font, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);""",
       "            // mutated: 按钮标签不再绘制", 1)]),

    ("noClampAtAll",
     "clamp 直接返回入参（完全不出屏幕保护）",
     [("        return new Point(x, y);", "        return requested;", 1)]),

    ("clampWithEstimate",
     "构造函数改用 (360,120) 尺寸估计，而不是实际尺寸",
     [("Location = PlaceAwayFromPointer(atPhysical, Size);",
       "Location = PlaceAwayFromPointer(atPhysical, new Size(360, 120));", 1)]),

    ("noPointerAvoid",
     "去掉指针避让（两处调用点都退回纯 clamp）",
     [("Location = PlaceAwayFromPointer(atPhysical, Size);",
       "Location = ClampToVirtualScreen(atPhysical, Size);", 1),
      ("Location = PlaceAwayFromPointer(new Point(Left, Top), Size);",
       "Location = ClampToVirtualScreen(new Point(Left, Top), Size);", 1)]),

    ("noButtonGuard",
     "OnMouseDown 不再区分鼠标键（右键也会触发动作）",
     [("""        if (e.Button != MouseButtons.Left)
            return;""",
       "        // mutated: 不再区分鼠标键", 1)]),

    ("noNoPrefix",
     "去掉所有 TextFormatFlags.NoPrefix（& 会被当成助记符吃掉）",
     # 顺序要紧：先拿走带 VerticalCenter 的那行，否则下一条通配会把它们一并吃掉。
     [("TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);",
       "TextFormatFlags.VerticalCenter);", 1),
      (" | TextFormatFlags.NoPrefix)", ")", 2),
      ("new Size(LabelMeasureBox, LabelMeasureBox), TextFormatFlags.NoPrefix);",
       "new Size(LabelMeasureBox, LabelMeasureBox));", 1)]),
]

CSProj = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>MutantProbe</AssemblyName>
    <RootNamespace>ToastWindowProbe</RootNamespace>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{program}" />
    <Compile Include="{source}" Link="ToastWindow.cs" />
  </ItemGroup>
</Project>
"""

# 针对**探针自身**的变异：验证那些"防自欺"的闸门真的会响。
# 这一类与前一类不同：它改的是 Program.cs，产品代码保持真实。
HARNESS_MUTATIONS = [
    ("deletedSection",
     "删掉整整一节（第 6 节「文字确实被绘制」）—— 断言总数闸门应当把它变成红",
     [('        Section("6. 文字确实被绘制", CheckTextPainted);',
       '        // mutated: 整节删除', 1)]),
]

names = []


# ── 对照组：两份源文件都用仓库里真实的那份 ──
control = os.path.join(work, "control")
os.makedirs(control, exist_ok=True)
io.open(os.path.join(control, "ControlProbe.csproj"), 'w', encoding='utf-8', newline='\n').write(
    CSProj.format(program=program_win, source=source_win)
          .replace("<AssemblyName>MutantProbe</AssemblyName>", "<AssemblyName>ControlProbe</AssemblyName>"))


def emit(name, desc, mutated_text, mutated_file, csproj_text):
    d = os.path.join(work, name)
    os.makedirs(d, exist_ok=True)
    io.open(os.path.join(d, mutated_file), 'w', encoding='utf-8', newline='\n').write(mutated_text)
    io.open(os.path.join(d, "MutantProbe.csproj"), 'w', encoding='utf-8', newline='\n').write(csproj_text)
    io.open(os.path.join(d, "description.txt"), 'w', encoding='utf-8').write(desc)
    names.append(name)


def apply_edits(text, edits, label):
    for old, new, expected in edits:
        found = text.count(old)
        if found != expected:
            sys.stderr.write(
                f"变体 {label} 的替换未命中：期望 {expected} 处，实际 {found} 处\n"
                f"  被替换文本开头：{old[:60]!r}\n")
            sys.exit(1)
        text = text.replace(old, new)
    return text


# ── 第一类：产品代码（ToastWindow.cs）变异 ──
for name, desc, edits in MUTATIONS:
    text = apply_edits(original, edits, name)
    if text == original:
        sys.stderr.write(f"变体 {name} 没有产生任何变化\n")
        sys.exit(1)

    emit(name, desc, text, "ToastWindow.cs",
         CSProj.format(program=program_win, source="ToastWindow.cs"))

# ── 第二类：探针自身（Program.cs）变异 ──
probe_original = io.open(probe_path, encoding='utf-8').read()
for name, desc, edits in HARNESS_MUTATIONS:
    text = apply_edits(probe_original, edits, name)
    emit(name, desc, text, "Program.cs",
         CSProj.format(program="Program.cs", source=source_win))

io.open(os.path.join(work, "names.txt"), 'w', encoding='utf-8', newline='\n').write("\n".join(names) + "\n")

if not names:
    sys.stderr.write("没有生成任何变异 —— 脚本什么都没验证，这不是通过\n")
    sys.exit(1)

print(f"已生成对照组 + {len(names)} 个变异副本（{len(MUTATIONS)} 产品 + {len(HARNESS_MUTATIONS)} 探针）：{', '.join(names)}")
print()
PY

# ── 阶段 0：对照组 —— 真实实现必须让探针全绿 ─────────────────────────────────
echo "----- 阶段 0：对照组（真实实现，要求探针全绿退出 0）-----"
dotnet run --project "$WORK/control/ControlProbe.csproj" -c Release > "$WORK/control/out.txt" 2>&1
control_code=$?
control_summary="$(grep -E '^===== 结果：' "$WORK/control/out.txt" | tail -1)"
control_fails="$(printf '%s' "$control_summary" | sed -n 's/.*\/ \([0-9][0-9]*\) 失败.*/\1/p')"

if [ -z "$control_summary" ]; then
  echo "  [无效] 对照组探针没跑起来（dotnet 退出码 = $control_code，无汇总行）"
  grep -E 'error|错误' "$WORK/control/out.txt" | head -5 | sed 's/^/         | /'
  exit 2
fi

if [ "$control_code" -ne 0 ] || [ "${control_fails:-1}" -ne 0 ]; then
  echo "  [无效] 对照组本身就是红的：$control_summary"
  echo "         ← 在这个前提下「变异被抓住」毫无意义，先修探针。"
  grep -E '^  \[FAIL\]' "$WORK/control/out.txt" | head -5 | sed 's/^/         /'
  exit 2
fi

echo "  [通过] 对照组：$control_summary（退出码 0）"
echo "         ← 下面任何变异「变红」都是因为它真的改变了行为，而不是探针本来就红。"
echo

# ── 阶段 2：逐个跑探针 ────────────────────────────────────────────────────────
#
# 判据**不能只看退出码**："构建失败"与"项目找不到"同样会给出非 0 退出码，那样会把
# 「变异根本没跑起来」误报成「变异被抓住了」。本脚本的第一版就犯了这个错 ——
# names.txt 被写成 CRLF，路径全错，于是 12/12 全是假通过。
# 所以必须要求：探针确实跑完并打印了汇总行，且**声明了至少 1 项断言失败**。
caught=0
escaped=0
invalid=0

while IFS= read -r raw_name; do
  name="${raw_name%$'\r'}"          # 防御性去尾 \r
  [ -n "$name" ] || continue
  d="$WORK/$name"
  desc="$(cat "$d/description.txt" 2>/dev/null || echo '(无描述)')"

  dotnet run --project "$d/MutantProbe.csproj" -c Release > "$d/out.txt" 2>&1
  code=$?

  # 探针的汇总行：===== 结果：N 通过 / M 失败 =====
  summary="$(grep -E '^===== 结果：' "$d/out.txt" | tail -1)"
  fails="$(printf '%s' "$summary" | sed -n 's/.*\/ \([0-9][0-9]*\) 失败.*/\1/p')"
  passes="$(printf '%s' "$summary" | sed -n 's/^===== 结果：\([0-9][0-9]*\) 通过.*/\1/p')"
  first_fail="$(grep -E '^  \[FAIL\]' "$d/out.txt" | head -1)"

  if [ -z "$summary" ] || [ -z "$fails" ] || [ -z "$passes" ]; then
    invalid=$((invalid + 1))
    printf '  [无效] %-22s %s\n' "$name" "$desc"
    printf '         探针没跑起来（dotnet 退出码 = %s，无汇总行）——不是证据\n' "$code"
    grep -E 'error|错误' "$d/out.txt" | head -3 | sed 's/^/         | /'
  elif [ "$fails" -gt 0 ]; then
    caught=$((caught + 1))
    printf '  [抓住] %-22s %s\n' "$name" "$desc"
    printf '         %s\n' "$summary"
    [ -n "$first_fail" ] && printf '         首个失败断言：%s\n' "$first_fail"
  else
    escaped=$((escaped + 1))
    printf '  [逃逸] %-22s %s\n' "$name" "$desc"
    printf '         %s（探针跑完了却全绿）\n' "$summary"
    printf '         ← 这个变异没被抓住，说明探针在该路径上缺少鉴别力。\n'
  fi
  echo
done < "$WORK/names.txt"

total=$((caught + escaped + invalid))

if [ "$total" -eq 0 ]; then
  echo "===== 变异复核结果：一个变异都没跑 —— 这不是通过 ====="
  echo "退出码 1"
  exit 1
fi

echo "===== 变异复核结果：$caught/$total 被抓住，$escaped 个逃逸，$invalid 个无效 ====="
echo "      （对照组：$control_summary）"

if [ "$escaped" -ne 0 ] || [ "$invalid" -ne 0 ]; then
  echo "退出码 1（有变异逃逸或未能运行）"
  exit 1
fi

echo "退出码 0（对照组全绿且全部变异都被抓住）"
exit 0
