#!/usr/bin/env bash
#
# ResultPresenterProbe 变异灵敏度复核（可一键重跑）
#
# ── 它解决什么问题 ─────────────────────────────────────────────────────────────
# 「探针有鉴别力」这件事本身也会悄悄失效：一条断言可能因为写成恒真式、或因为场景
# 选得不对（例如驱动用的尺寸小于实际值，于是被测分支根本没走到），表现得全绿却什么
# 都没验证。报告里说「变异全部被抓住」如果是跑在某个临时副本上、没留在仓库里，后来者
# 与 CI 都无法复核。
#
# 本脚本用**真实变异**来复核：把一个故意写错的 ResultPresenter.cs 交给**同一份**探针，
# 要求探针变红。
#
# ── 做法 ──────────────────────────────────────────────────────────────────────
#   0. **对照组**：先拿仓库里真实的源文件跑一次，要求探针**全绿退出 0**。
#      没有这一步，"变异让探针变红"就可能只是因为探针本来就红着。
#   1. 读仓库里真实的 src/QrScan/UI/ResultPresenter.cs；
#   2. 每个变异对它做**一处**文本替换，写成一个临时副本；
#   3. 生成一个 csproj：编译**仓库里真实的那份** Program.cs + 变异副本 + 真实 ToastWindow.cs；
#   4. 跑完整探针，**以"探针跑完且声明了 ≥1 项断言失败"为通过**；
#   5. 任一变异未能让探针变红 → 脚本自己以非 0 退出。
#
# **绝不修改仓库里的任何源文件** —— 全部副本都在临时目录，退出时删除。
#
# ── 判据为什么不是退出码 ──────────────────────────────────────────────────────
# "构建失败"与"项目找不到"同样会给出非 0 退出码，那样会把「变异根本没跑起来」
# 误报成「变异被抓住了」。ToastWindowProbe 的第一版就犯过这个错（names.txt 被写成
# CRLF，路径全错，于是 12/12 全是假通过）。所以必须要求：探针确实跑完并打印了汇总行，
# 且**声明了至少 1 项断言失败**。
#
# ── 依赖 ──────────────────────────────────────────────────────────────────────
# bash、python（本仓库已用 python 生成测试样张）、.NET SDK 8。
#
# ── 用法 ──────────────────────────────────────────────────────────────────────
#   bash tools/ResultPresenterProbe/run-mutations.sh
#
# 退出码：0 = 对照组全绿且全部变异都被抓住；非 0 = 有变异逃逸/无效，或对照组本身红。

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/src/QrScan/UI/ResultPresenter.cs"
TOAST="$ROOT/src/QrScan/UI/ToastWindow.cs"
PROGRAM="$ROOT/tools/ResultPresenterProbe/Program.cs"
CORE="$ROOT/src/QrScan.Core/QrScan.Core.csproj"

export DOTNET_ROOT="${DOTNET_ROOT:-C:\\Users\\dell\\.dotnet}"
export PATH="/c/Users/dell/.dotnet:$PATH"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

command -v python >/dev/null 2>&1 || { echo "需要 python"; exit 2; }
[ -f "$SRC" ] || { echo "找不到源文件：$SRC"; exit 2; }
[ -f "$TOAST" ] || { echo "找不到源文件：$TOAST"; exit 2; }
[ -f "$PROGRAM" ] || { echo "找不到探针源码：$PROGRAM"; exit 2; }
[ -f "$CORE" ] || { echo "找不到 Core 项目：$CORE"; exit 2; }

# MSBuild 读 csproj 里的绝对路径时不认 MSYS 的 /c/... 形式，要转成 Windows 形式。
win_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

echo "===== ResultPresenterProbe 变异复核 ====="
echo "源文件（只读）：$SRC"
echo "探针源码（只读）：$PROGRAM"
echo "临时目录：$WORK"
echo

# ── 阶段 0+1：生成对照组与全部变异副本 ────────────────────────────────────────
python - "$SRC" "$PROGRAM" "$WORK" "$(win_path "$PROGRAM")" "$(win_path "$SRC")" \
          "$(win_path "$TOAST")" "$(win_path "$CORE")" <<'PY' || exit 2
import io, os, sys

src_path, probe_path, work, program_win, src_win, toast_win, core_win = sys.argv[1:8]
original = io.open(src_path, encoding='utf-8').read()
probe_original = io.open(probe_path, encoding='utf-8').read()

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>{assembly}</AssemblyName>
    <RootNamespace>ResultPresenterProbe</RootNamespace>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{program}" />
    <Compile Include="{source}" Link="ResultPresenter.cs" />
    <Compile Include="{toast}" Link="ToastWindow.cs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="{core}" />
  </ItemGroup>
</Project>
"""


def write_csproj(path, program, source, assembly):
    io.open(path, 'w', encoding='utf-8', newline='\n').write(
        CSPROJ.format(program=program, source=source, toast=toast_win,
                      core=core_win, assembly=assembly))


# ── 对照组：两份源文件都用仓库里真实的那份 ──
control = os.path.join(work, 'control')
os.makedirs(control, exist_ok=True)
write_csproj(os.path.join(control, 'Control.csproj'), program_win, src_win, 'ControlProbe')

# 每一处替换都必须**恰好命中预期次数**，否则说明源文件已漂移、这个变异不再是有意义的
# 复核（例如"删掉失败提示"结果一处都没删，那这个变异当然抓不到任何东西）。
MUTATIONS = [
    ("noRetry",
     "剪贴板不再重试（只尝试 1 次）",
     [("for (int attempt = 0; attempt < ClipboardRetryAttempts; attempt++)",
       "for (int attempt = 0; attempt < 1; attempt++) /* mutated */", 1)]),

    ("alwaysShowOpen",
     "「打开」判定改成总是显示 —— 直接击穿安全边界（本脚本最关键的一条）",
     [("if (PayloadClassifier.CanOpen(kind))",
       "if (true) /* mutated */", 1)]),

    ("noModulo",
     "多码切换去掉取模（不再回绕）",
     [("int next = (index + 1) % payloads.Count;",
       "int next = index + 1; /* mutated */", 1)]),

    ("noClosePrevious",
     "Replace 不再关闭旧提示条（泄漏）",
     [("        previous?.Close();", "        // mutated: 不再关闭旧提示条", 1)]),

    ("silentCopyFailure",
     "复制失败时静默（不弹失败提示）",
     [("""        if (!TrySetClipboardText(payloads[0].Text))
        {
            ShowCopyFailure(payloads, index: 0, atPhysical);
            return;
        }""",
       "        // mutated: 复制失败时静默", 1)]),

    ("noRetryButton",
     "失败提示条不再带「重试」按钮",
     [('new[] { new ToastAction("retry", "重试") },', "Array.Empty<ToastAction>(),", 1)]),

    ("noCopyButton",
     "按钮矩阵不再包含「复制」",
     [('actions.Add(new ToastAction("copy", "复制"));', "// mutated: 不再添加「复制」", 1)]),

    # 「中文文案」是本项目的全局约束，而只断言按钮 Id 的话，改标签一个字都不会变红。
    # 这个变异专测「标签断言真的有鉴别力」。
    ("openLabelEnglish",
     "「打开」按钮的标签改成英文（击穿「全部面向用户文案使用中文」）",
     [('actions.Add(new ToastAction("open", "打开"));',
       'actions.Add(new ToastAction("open", "Open")); /* mutated */', 1)]),

    ("nextLabelOffByOne",
     "next 标签的 N 多算一个（Count 而不是 Count - 1）",
     [('actions.Add(new ToastAction("next", $"还有 {payloads.Count - 1} 个 ▸"));',
       'actions.Add(new ToastAction("next", $"还有 {payloads.Count} 个 ▸"));', 1)]),

    ("noTruncate",
     "长文本不再截断",
     [("""        string message = current.Text.Length <= MaxMessageLength
            ? current.Text
            : current.Text[..MaxMessageLength] + "…";""",
       "        string message = current.Text; /* mutated */", 1)]),

    ("maxMessageLength100",
     "MaxMessageLength 改成 100（截断阈值漂移）",
     [("private const int MaxMessageLength = 120;",
       "private const int MaxMessageLength = 100; /* mutated */", 1)]),

    ("retryDelayZero",
     "重试间隔改成 0（去掉退避）",
     [("private const int ClipboardRetryDelayMs = 50;",
       "private const int ClipboardRetryDelayMs = 0; /* mutated */", 1)]),

    # retryDelayZero 改的是**常量**；这个改的是**调用点**，留常量不动，
    # 专测「只断言常量值」是抓不到的机制漂移（必须靠 §2 的时序断言）。
    ("sleepArgumentOne",
     "调用点改成 Thread.Sleep(1)（常量仍为 50，时序断言应当把它变成红）",
     [("Thread.Sleep(ClipboardRetryDelayMs);",
       "Thread.Sleep(1); /* mutated */", 1)]),

    # 专测「copy 与 render 各取一个下标」这类漂移：
    # 下面这条把重试写入的码写死为第 0 个，而渲染仍用 index。单码与 index==0 的场景
    # 都看不出区别，**只有**「切换失败后重试」那个场景（index==1）会暴露 ——
    # 所以它是 §3 那三条剪贴板断言的鉴别力证明。
    ("retryCopyUsesIndexZero",
     "重试写入剪贴板时写死第 0 个码（copy 与 render 各取一个下标）",
     [("""            if (TrySetClipboardText(payloads[index].Text))
                ShowPayloadToast(payloads, index, atPhysical);""",
       """            if (TrySetClipboardText(payloads[0].Text)) /* mutated: copy 与 render 下标脱钩 */
                ShowPayloadToast(payloads, index, atPhysical);""", 1)]),

    ("noTryOpenCatch",
     "打开失败不再提示（异常静默吞掉）",
     [("""        catch (Exception ex)
        {
            ShowMessage($"无法打开：{ex.Message}", ToastKind.Failure, atPhysical);
        }""",
       """        catch (Exception)
        {
            // mutated: 打开失败不再提示
        }""", 1)]),

    ("unconditionalActiveClear",
     "_active 无条件清空（旧窗体关闭时会擦掉新状态）",
     [("""            if (ReferenceEquals(_active, toast))
                _active = null;""",
       "            _active = null; /* mutated */", 1)]),

    # 注：**故意不使用"整块删掉空结果分支"这种变异** —— 它会让 payloads[0] 越界抛
    # 未处理异常，进程直接崩掉、不打印汇总行，于是与"构建失败"无法区分。
    # 脚本对这种情况只能计为"无效"（拒绝把模糊证据当成抓住），所以改用同语义但
    # 可优雅检出的变异。
    ("noCodeWrongKind",
     "空结果（未识别到二维码）的语义改成失败（Kind 漂移）",
     [("ShowMessage(NoCodeMessage, ToastKind.Info, atPhysical);",
       "ShowMessage(NoCodeMessage, ToastKind.Failure, atPhysical); /* mutated */", 1)]),
]

# 针对**探针自身**的变异：验证那些"防自欺"的闸门真的会响。
# 这一类改的是 Program.cs，产品代码保持真实。
HARNESS_MUTATIONS = [
    ("deletedSection",
     "删掉整整一节（第 6 节 安全边界）—— 断言总数闸门应当把它变成红",
     [('        Section("6. 安全边界：自定义 scheme 绝不触达启动器", CheckSecurityBoundary);',
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


def emit(name, desc, program, source, assembly, mutated_file, mutated_text):
    d = os.path.join(work, name)
    os.makedirs(d, exist_ok=True)
    write_csproj(os.path.join(d, 'MutantProbe.csproj'), program, source, assembly)
    io.open(os.path.join(d, mutated_file), 'w', encoding='utf-8', newline='\n').write(mutated_text)
    io.open(os.path.join(d, 'description.txt'), 'w', encoding='utf-8').write(desc)
    names.append(name)


names = []

# ── 第一类：产品代码（ResultPresenter.cs）变异；Program.cs 用真实的那份 ──
for name, desc, edits in MUTATIONS:
    text = apply_edits(original, edits, name)
    if text == original:
        sys.stderr.write(f"变体 {name} 没有产生任何变化\n")
        sys.exit(1)

    emit(name, desc, program_win, 'ResultPresenter.cs', 'MutantProbe',
         'ResultPresenter.cs', text)

# ── 第二类：探针自身（Program.cs）变异；两份产品源文件都用真实的 ──
for name, desc, edits in HARNESS_MUTATIONS:
    text = apply_edits(probe_original, edits, name)
    emit(name, desc, 'Program.cs', src_win, 'MutantProbe', 'Program.cs', text)

if not names:
    sys.stderr.write("没有生成任何变异 —— 脚本什么都没验证，这不是通过\n")
    sys.exit(1)

io.open(os.path.join(work, 'names.txt'), 'w', encoding='utf-8', newline='\n').write(
    "\n".join(names) + "\n")
print(f"已生成对照组 + {len(names)} 个变异副本"
      f"（{len(MUTATIONS)} 产品 + {len(HARNESS_MUTATIONS)} 探针）：{', '.join(names)}")
print()
PY

# ── 阶段 0：对照组 —— 真实实现必须让探针全绿 ─────────────────────────────────
echo "----- 阶段 0：对照组（真实实现，要求探针全绿退出 0）-----"
dotnet run --project "$WORK/control/Control.csproj" -c Release > "$WORK/control/out.txt" 2>&1
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
  echo "         ← 在这个前提下「变异被抓往」毫无意义，先修探针。"
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
    printf '  [无效] %-24s %s\n' "$name" "$desc"
    printf '         探针没跑起来（dotnet 退出码 = %s，无汇总行）——不是证据\n' "$code"
    grep -E 'error|错误' "$d/out.txt" | head -3 | sed 's/^/         | /'
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
