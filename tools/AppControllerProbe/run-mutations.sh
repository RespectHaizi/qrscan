#!/usr/bin/env bash
#
# AppControllerProbe 变异灵敏度复核（可一键重跑）
#
# ── 它解决什么问题 ─────────────────────────────────────────────────────────────
# 「探针有鉴别力」这件事本身也会悄悄失效：一条断言可能因为写成恒真式、或因为场景
# 选得不对（例如驱动用的数据让被测分支根本没走到），表现得全绿却什么都没验证。
# 报告里说「变异全部被抓住」如果是跑在某个临时副本上、没留在仓库里，CI 与后来者
# 都无法复核。
#
# 本脚本用**真实变异**来复核：把一个故意写错的 AppController.cs 交给**同一份**探针，
# 要求探针变红。
#
# 本任务的三样东西恰好都是"最容易悄悄坏掉、又最难肉眼发现"的：
#   · 反向坐标换算 —— 写死 0 只在多显示器下暴露；
#   · 四条退出路径的释放 —— 漏一条只在长跑后暴露；
#   · 状态机的两条边界规则 —— 只在连按两次热键时暴露。
# 所以这里对每一样都至少有一个变异盯着。
#
# ── 做法 ──────────────────────────────────────────────────────────────────────
#   0. **对照组**：先拿仓库里真实的源文件跑一次，要求探针**全绿退出 0**。
#      没有这一步，"变异让探针变红"就可能只是因为探针本来就红着。
#   1. 读仓库里真实的 src/QrScan/AppController.cs；
#   2. 每个变异对它做**一处**文本替换，写成一个临时副本；
#   3. 生成一个 csproj：编译**仓库里真实的** Program.cs + 变异副本 + 其余真实源文件；
#   4. 跑完整探针，**以"探针跑完且声明了 ≥1 项断言失败"为通过**；
#   5. 任一变异未能让探针变红 → 脚本自己以非 0 退出。
#
# **绝不修改仓库里的任何源文件** —— 全部副本都在临时目录，退出时清理。
#
# ── 判据为什么不是退出码 ──────────────────────────────────────────────────────
# "构建失败"与"探针没跑起来"同样会给出非 0 退出码，那样会把「变异根本没跑」误报成
# 「变异被抓住了」。所以必须要求：探针确实跑完并打印了汇总行，且**声明了至少 1 项失败**。
#
# ── 依赖 ──────────────────────────────────────────────────────────────────────
# bash、python、.NET SDK 8。
#
# ── 用法 ──────────────────────────────────────────────────────────────────────
#   bash tools/AppControllerProbe/run-mutations.sh
#
# 退出码：0 = 对照组全绿且全部变异都被抓住；非 0 = 有变异逃逸/无效，或对照组本身红。

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TARGET="$ROOT/src/QrScan/AppController.cs"
PROGRAM="$ROOT/tools/AppControllerProbe/Program.cs"
CORE="$ROOT/src/QrScan.Core/QrScan.Core.csproj"
ASSET="$ROOT/tests/QrScan.Core.Tests/Assets/standard.png"

export DOTNET_ROOT="${DOTNET_ROOT:-C:\\Users\\dell\\.dotnet}"
export PATH="/c/Users/dell/.dotnet:$PATH"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

command -v python >/dev/null 2>&1 || { echo "需要 python"; exit 2; }
[ -f "$TARGET" ]  || { echo "找不到源文件：$TARGET"; exit 2; }
[ -f "$PROGRAM" ] || { echo "找不到探针源码：$PROGRAM"; exit 2; }
[ -f "$CORE" ]    || { echo "找不到 Core 项目：$CORE"; exit 2; }
[ -f "$ASSET" ]   || { echo "找不到测试样张：$ASSET"; exit 2; }

# MSBuild 读 csproj 里的绝对路径时不认 MSYS 的 /c/... 形式，要转成 Windows 形式。
win_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# 探针需要的其余真实源文件（与 tools/AppControllerProbe/AppControllerProbe.csproj 保持一致）。
LINKED_REL=(
  "src/QrScan/UI/OverlayWindow.cs"
  "src/QrScan/UI/ToastWindow.cs"
  "src/QrScan/UI/ResultPresenter.cs"
  "src/QrScan/UI/TrayHost.cs"
  "src/QrScan/Native/HotkeyManager.cs"
  "src/QrScan/Native/User32.cs"
  "src/QrScan.Core/HotkeySpec.cs"
)

LINKED_WIN=""
for rel in "${LINKED_REL[@]}"; do
  LINKED_WIN="${LINKED_WIN}$(win_path "$ROOT/$rel");"
done

echo "===== AppControllerProbe 变异复核 ====="
echo "源文件（只读）：$TARGET"
echo "探针源码（只读）：$PROGRAM"
echo "临时目录：$WORK"
echo

python - "$TARGET" "$PROGRAM" "$WORK" \
        "$(win_path "$PROGRAM")" "$(win_path "$CORE")" "$(win_path "$ASSET")" \
        "$(win_path "$TARGET")" "$LINKED_WIN" <<'PY' || exit 2
import io, os, sys

(target_path, probe_path, work,
 program_win, core_win, asset_win, target_win, linked_raw) = sys.argv[1:9]

original = io.open(target_path, encoding='utf-8').read()
probe_original = io.open(probe_path, encoding='utf-8').read()
LINKED_WIN = [p for p in linked_raw.split(';') if p]

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>{assembly}</AssemblyName>
    <RootNamespace>AppControllerProbe</RootNamespace>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{program}" />
    <Compile Include="{target}" Link="AppController.cs" />
{linked}
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="{core}" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="{asset}" />
  </ItemGroup>
</Project>
"""


def write_csproj(path, program, target, assembly):
    linked = "\n".join(
        '    <Compile Include="%s" Link="%s" />' % (p, os.path.basename(p.replace('\\', '/')))
        for p in LINKED_WIN)
    io.open(path, 'w', encoding='utf-8', newline='\n').write(
        CSPROJ.format(program=program, target=target, linked=linked,
                      core=core_win, asset=asset_win, assembly=assembly))


# 每一处替换都必须**恰好命中预期次数**，否则说明源文件已漂移、这个变异不再是有意义的
# 复核（例如"删掉冻结位图的释放"结果一处都没删，那这个变异当然抓不到任何东西）。
MUTATIONS = [
    # ── 反向坐标换算：第 1 号陷阱（副屏在主屏左/上 → 原点为负）──
    ("bareZeroOriginX",
     "ScreenToLocal 的 X 丢掉原点分量（等价于写死 0）",
     [("        physical.X - virtualBounds.X,", "        physical.X, /* mutated */", 1)]),

    ("swappedOriginAxes",
     "ScreenToLocal 的 Y 误用了 X 的原点分量（方形选区上看不出来）",
     [("        physical.Y - virtualBounds.Y,", "        physical.Y - virtualBounds.X, /* mutated */", 1)]),

    ("widthFromVirtualScreen",
     "ScreenToLocal 的宽度取自虚拟桌面而不是选区",
     [("        physical.Width,\n        physical.Height);",
       "        virtualBounds.Width, /* mutated */\n        physical.Height);", 1)]),

    # ── 资源纪律：四条退出路径共用的那一次释放 ──
    ("noOverlayDispose",
     "finally 不再释放遮罩",
     [("            overlay?.Dispose();", "            // mutated: 不再释放遮罩", 1)]),

    ("noFrozenDispose",
     "finally 不再释放冻结位图（双 4K 下每次扫码泄漏 ~66MB）",
     [("            frozen?.Dispose();", "            // mutated: 不再释放冻结位图", 1)]),

    # ── 状态机：两条边界规则 ──
    ("decodingNotIgnored",
     "Decoding/Presenting 期间不再忽略热键（改成又开始一次框选 → 重入）",
     [("            case AppState.Decoding:\n            case AppState.Presenting:\n                break;",
       "            case AppState.Decoding:\n            case AppState.Presenting:\n                StartCapture(); /* mutated */\n                break;", 1)]),

    # ── 错误归因：截屏的两类异常 ──
    ("noInvalidOpNormalize",
     "不再把截屏的 InvalidOperationException 归一为截屏失败（更正 5 被撤销）",
     [("        catch (InvalidOperationException ex)",
       "        catch (InvalidOperationException ex) when (false) /* mutated */", 1)]),

    ("wrongCaptureErrorTitle",
     "截屏失败的标题误报为「扫码失败」（归因错误）",
     [('            _tray.ShowBalloon("截屏失败", ex.Message);',
       '            _tray.ShowBalloon("扫码失败", ex.Message); /* mutated */', 1)]),

    # ── 托盘记录 ──
    ("noTrayCallback",
     "成功识别后不再回调托盘「最近 N 条」",
     [("                _onPayloadDecoded(payload);",
       "                // mutated: 不再回调托盘记录", 1)]),
]

# 针对**探针自身**的变异：验证"防自欺"的闸门真的会响。
HARNESS_MUTATIONS = [
    ("deletedSection",
     "删掉整整一节（第 1 节 反向坐标换算）—— 断言总数闸门应当把它变成红",
     [('        Section("1. 反向坐标换算 ScreenToLocal（负原点是全程序第 1 号陷阱）",\n            () => SectionCoordinateConversion());',
       '        // mutated: 整节删除', 1)]),
]


def apply_edits(text, edits, label):
    for old, new, expected in edits:
        found = text.count(old)
        if found != expected:
            sys.stderr.write(
                f"变体 {label} 的替换未命中：期望 {expected} 处，实际 {found} 处\n"
                f"  被替换文本开头：{old[:70]!r}\n")
            sys.exit(1)
        text = text.replace(old, new)
    return text


names = []

# ── 对照组：两份源文件都用仓库里真实的那份 ──
control = os.path.join(work, 'control')
os.makedirs(control, exist_ok=True)
write_csproj(os.path.join(control, 'Control.csproj'), program_win, target_win, 'ControlProbe')

# ── 第一类：产品代码（AppController.cs）变异；Program.cs 用真实的那份 ──
for name, desc, edits in MUTATIONS:
    text = apply_edits(original, edits, name)
    if text == original:
        sys.stderr.write(f"变体 {name} 没有产生任何变化\n")
        sys.exit(1)

    d = os.path.join(work, name)
    os.makedirs(d, exist_ok=True)
    # 变异副本就放在 csproj 旁边，用相对名引用 —— 免去再做一次 Windows 路径转换。
    write_csproj(os.path.join(d, 'MutantProbe.csproj'), program_win, 'AppController.cs', 'MutantProbe')
    io.open(os.path.join(d, 'AppController.cs'), 'w', encoding='utf-8', newline='\n').write(text)
    io.open(os.path.join(d, 'description.txt'), 'w', encoding='utf-8').write(desc)
    names.append(name)

# ── 第二类：探针自身（Program.cs）变异；产品源文件全部用真实的 ──
for name, desc, edits in HARNESS_MUTATIONS:
    text = apply_edits(probe_original, edits, name)
    d = os.path.join(work, name)
    os.makedirs(d, exist_ok=True)
    write_csproj(os.path.join(d, 'MutantProbe.csproj'), 'Program.cs', target_win, 'MutantProbe')
    io.open(os.path.join(d, 'Program.cs'), 'w', encoding='utf-8', newline='\n').write(text)
    io.open(os.path.join(d, 'description.txt'), 'w', encoding='utf-8').write(desc)
    names.append(name)

if not names:
    sys.stderr.write("没有生成任何变异 —— 脚本什么都没验证，这不是通过\n")
    sys.exit(1)

io.open(os.path.join(work, 'names.txt'), 'w', encoding='utf-8', newline='\n').write(
    "\n".join(names) + "\n")
print(f"已生成对照组 + {len(names)} 个变异副本"
      f"（{len(MUTATIONS)} 产品 + {len(HARNESS_MUTATIONS)} 探针）：{', '.join(names)}")
print()
PY

run_probe() {
  # $1 = csproj 路径；$2 = 输出文件。
  #
  # QRSCAN_REPO_ROOT：变异副本在临时目录里构建，从程序目录向上找不到 QrScan.sln，
  # 而 §5 的源码文本检查必须读**仓库里真实的**那几份文件（那正是它的目的）。
  #
  # timeout 是兜底：探针自己也有 90 秒看门狗（退出码 3），卡死一律算"无法判定"而非"红"。
  QRSCAN_REPO_ROOT="$ROOT" timeout 150 dotnet run --project "$1" -c Release > "$2" 2>&1
  echo $?
}

# ── 阶段 0：对照组 ────────────────────────────────────────────────────────────
echo "----- 阶段 0：对照组（真实实现，要求探针全绿退出 0）-----"
control_code="$(run_probe "$WORK/control/Control.csproj" "$WORK/control/out.txt")"
control_summary="$(grep -E '^===== 结果：' "$WORK/control/out.txt" | tail -1)"
control_fails="$(printf '%s' "$control_summary" | sed -n 's/.*\/ \([0-9][0-9]*\) 失败.*/\1/p')"

if [ -z "$control_summary" ]; then
  echo "  [无效] 对照组探针没跑起来（退出码 = $control_code，无汇总行）"
  grep -E 'error|错误|看门狗' "$WORK/control/out.txt" | head -5 | sed 's/^/         | /'
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
caught=0
escaped=0
invalid=0

while IFS= read -r raw_name; do
  name="${raw_name%$'\r'}"
  [ -n "$name" ] || continue
  d="$WORK/$name"
  desc="$(cat "$d/description.txt" 2>/dev/null || echo '(无描述)')"

  code="$(run_probe "$d/MutantProbe.csproj" "$d/out.txt")"

  summary="$(grep -E '^===== 结果：' "$d/out.txt" | tail -1)"
  fails="$(printf '%s' "$summary" | sed -n 's/.*\/ \([0-9][0-9]*\) 失败.*/\1/p')"
  first_fail="$(grep -E '^  \[FAIL\]' "$d/out.txt" | head -1)"

  if [ -z "$summary" ] || [ -z "$fails" ]; then
    invalid=$((invalid + 1))
    printf '  [无效] %-24s %s\n' "$name" "$desc"
    printf '         探针没跑完（退出码 = %s，无汇总行）——不是证据\n' "$code"
    grep -E 'error|错误|看门狗' "$d/out.txt" | head -3 | sed 's/^/         | /'
  elif [ "$fails" -gt 0 ]; then
    caught=$((caught + 1))
    printf '  [抓住] %-24s %s\n' "$name" "$desc"
    printf '         %s\n' "$summary"
    [ -n "$first_fail" ] && printf '         首个失败断言：%s\n' "$first_fail"
  else
    escaped=$((escaped + 1))
    printf '  [逃逸] %-24s %s\n' "$name" "$desc"
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
