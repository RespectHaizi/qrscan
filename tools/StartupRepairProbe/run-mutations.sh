#!/usr/bin/env bash
#
# StartupRepairProbe 变异灵敏度复核（可一键重跑）
#
# ── 它解决什么问题 ─────────────────────────────────────────────────────────────
# 「探针有鉴别力」这件事本身也会悄悄失效：一条断言可能因为写成恒真式、或因为场景
# 选得不对（例如守卫被删了、而恰好被驱用的那个场景有别的守卫兜住），表现得全绿却
# 什么都没验证。报告里说「变异全部被抓住」如果是跑在某个临时副本上、没留在仓库里，
# 控制者与 CI 都无法复核。
#
# 本脚本用**真实变异**来复核：把故意写错的源文件交给**同一份**探针，要求探针变红。
#
# ── 三类变异，走三条不同的路径 ────────────────────────────────────────────────
#   [产品] 变异 src/QrScan/StartupPathRepair.cs → 编译变异副本，行为断言应当变红。
#   [调用点] 变异 src/QrScan/TrayApplicationContext.cs → 探针 §8 的**文本断言**应当变红。
#           它不需要编译：脚本把变异文件放进一个**假的仓库根**，
#           并通过 QRSCAN_REPO_ROOT 让探针去读那一份。
#   [探针] 变异 tools/StartupRepairProbe/Program.cs → 验证「防自欺闸门」真的会响
#           （删掉一整节时，断言总数闸门必须把它变成红）。
#
# ── 做法 ──────────────────────────────────────────────────────────────────────
#   0. **对照组**：先拿仓库里真实的源文件跑一次，要求探针**全绿退出 0**。
#      没有这一步，"变异让探针变红"就可能只是因为探针本来就红着。
#   1~3. 每类变异各做**一处**文本替换（命中次数必须与预期完全一致，否则视为脚本失效）；
#   4. 跑完整探针，**以"探针跑完且声明了 ≥1 项断言失败"为通过**；
#   5. 任一变异未能让探针变红 → 脚本自己以非 0 退出。
#
# **绝不修改仓库里的任何源文件** —— 全部副本都在临时目录，退出时删除。
#
# ── 判据为什么不是退出码 ──────────────────────────────────────────────────────
# "构建失败"与"项目找不到"同样会给出非 0 退出码，那样会把「变异根本没跑起来」
# 误报成「变异被抓住了」。同仓库的 ToastWindowProbe 第一版就犯过这个错
# （names.txt 被写成 CRLF、路径全错，于是 12/12 全是假通过）。所以必须要求：
# 探针确实跑完并打印了汇总行，且**声明了至少 1 项断言失败**。
#
# ── 依赖 ──────────────────────────────────────────────────────────────────────
# bash、python（本仓库已用 python 生成测试样张）、.NET SDK 8。
#
# ── 用法 ──────────────────────────────────────────────────────────────────────
#   bash tools/StartupRepairProbe/run-mutations.sh
#
# 退出码：0 = 对照组全绿且全部变异都被抓住；非 0 = 有变异逃逸/无效，或对照组本身红。

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
IMPL="$ROOT/src/QrScan/StartupPathRepair.cs"
CALLSITE="$ROOT/src/QrScan/TrayApplicationContext.cs"
PROGRAM="$ROOT/tools/StartupRepairProbe/Program.cs"
CORE="$ROOT/src/QrScan.Core/QrScan.Core.csproj"

export DOTNET_ROOT="${DOTNET_ROOT:-C:\\Users\\dell\\.dotnet}"
export PATH="/c/Users/dell/.dotnet:$PATH"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

command -v python >/dev/null 2>&1 || { echo "需要 python"; exit 2; }
for f in "$IMPL" "$CALLSITE" "$PROGRAM" "$CORE"; do
  [ -f "$f" ] || { echo "找不到源文件：$f"; exit 2; }
done

# MSBuild 读 csproj 里的绝对路径时不认 MSYS 的 /c/... 形式，要转成 Windows 形式。
win_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

echo "===== StartupRepairProbe 变异复核 ====="
echo "实现（只读）：$IMPL"
echo "调用点（只读）：$CALLSITE"
echo "探针源码（只读）：$PROGRAM"
echo "临时目录：$WORK"
echo

# ── 阶段 0+1：生成对照组与全部变异副本 ────────────────────────────────────────
python - "$IMPL" "$CALLSITE" "$PROGRAM" "$WORK" \
          "$(win_path "$PROGRAM")" "$(win_path "$IMPL")" "$(win_path "$CORE")" "$ROOT" <<'PY' || exit 2
import io, os, sys

impl_path, callsite_path, probe_path, work, program_win, impl_win, core_win, ROOT = sys.argv[1:9]

impl_original = io.open(impl_path, encoding='utf-8').read()
callsite_original = io.open(callsite_path, encoding='utf-8').read()
probe_original = io.open(probe_path, encoding='utf-8').read()

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>{assembly}</AssemblyName>
    <RootNamespace>StartupRepairProbe</RootNamespace>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{program}" />
    <Compile Include="{impl}" Link="StartupPathRepair.cs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="{core}" />
  </ItemGroup>
</Project>
"""


def emit_compiled(name, desc, program, impl, assembly, mutated_file, mutated_text):
    d = os.path.join(work, name)
    os.makedirs(d, exist_ok=True)
    csproj = os.path.join(d, 'MutantProbe.csproj')
    io.open(csproj, 'w', encoding='utf-8', newline='\n').write(
        CSPROJ.format(program=program, impl=impl, core=core_win, assembly=assembly))
    io.open(os.path.join(d, mutated_file), 'w', encoding='utf-8', newline='\n').write(mutated_text)
    io.open(os.path.join(d, 'description.txt'), 'w', encoding='utf-8').write(desc)
    io.open(os.path.join(d, 'plan.txt'), 'w', encoding='utf-8', newline='\n').write(
        f"{name}|{desc}|{csproj}|{ROOT}\n")


def emit_callsite(name, desc, mutated_text):
    """调用点变异：不编译，只把变异过的文件放进一个假的仓库根。
    探针 §8 通过 QRSCAN_REPO_ROOT 去读它。"""
    fake_root = os.path.join(work, name)
    target_dir = os.path.join(fake_root, 'src', 'QrScan')
    os.makedirs(target_dir, exist_ok=True)
    io.open(os.path.join(target_dir, 'TrayApplicationContext.cs'),
            'w', encoding='utf-8', newline='\n').write(mutated_text)
    io.open(os.path.join(work, name + '.desc'), 'w', encoding='utf-8').write(desc)
    return fake_root


# ── 对照组 ──
control_dir = os.path.join(work, 'control')
os.makedirs(control_dir, exist_ok=True)
control_csproj = os.path.join(control_dir, 'Control.csproj')
io.open(control_csproj, 'w', encoding='utf-8', newline='\n').write(
    CSPROJ.format(program=program_win, impl=impl_win, core=core_win, assembly='ControlProbe'))

# 每一处替换都必须**恰好命中预期次数**，否则说明源文件已漂移、这个变异不再是有意义的
# 复核（例如"删掉未启用守卫"结果一处都没删，那这个变异当然抓不到任何东西）。
MUTATIONS = [
    ("ordinalCaseSensitive",
     "比较改成区分大小写（Ordinal）—— 同一路径每次启动都会被误判为陈旧并白重写一次",
     [("StringComparison.OrdinalIgnoreCase))",
       "StringComparison.Ordinal)) /* mutated */", 1)]),

    ("noEnabledGuard",
     "删掉「未启用就直接返回」守卫 —— 会把用户主动关掉的自启又打开",
     [("        if (!startup.IsEnabled)\n            return false;\n\n", "", 1)]),

    ("invertEnabledGuard",
     "「未启用」守卫取反",
     [("if (!startup.IsEnabled)", "if (startup.IsEnabled) /* mutated */", 1)]),

    ("noNullPathGuard",
     "删掉「读不到路径就直接返回」守卫 —— 会把 null 当成一个路径去比较并重写",
     [("        if (registered is null)\n            return false;\n\n", "", 1)]),

    ("noSwallowAtAll",
     "异常过滤改成永不匹配 —— 注册表相关异常直接抛进构造函数，托盘程序起不来",
     [("""        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException
                                      or InvalidOperationException)""",
       "        catch (Exception ex) when (false) /* mutated: 不再吞任何异常 */", 2)]),

    # 本脚本最关键的一条：`catch (Exception)` 看起来"更稳"，实际把真正的编程错误
    # 也一起吞掉。探针的「清单外异常必须逃逸」正是为它准备的。
    ("blanketCatch",
     "异常过滤改成 catch (Exception) —— 吞掉一切，包括清单外的编程错误",
     [("""        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException
                                      or InvalidOperationException)""",
       "        catch (Exception ex) /* mutated: 吞掉一切 */", 2)]),

    ("writeFalseInsteadOfTrue",
     "重写时传 false —— 本该修好自启，实际把用户的自启删掉了",
     [("            startup.SetEnabled(true);\n            return true;",
       "            startup.SetEnabled(false); /* mutated */\n            return true;", 1)]),

    ("returnFalseAfterRepair",
     "修好了却返回 false（返回值与实际副作用脱节）",
     [("            startup.SetEnabled(true);\n            return true;",
       "            startup.SetEnabled(true);\n            return false; /* mutated */", 1)]),

    ("repairWithoutWriting",
     "声称修好了却根本没写（返回值与实际副作用脱节）",
     [("            startup.SetEnabled(true);\n            return true;",
       "            return true; /* mutated: 没写却说修好了 */", 1)]),

    ("trimBeforeCompare",
     "比较前 trim 掉首尾空白 —— 场景 H（尾随空格）会被判为一致、不再重写",
     [("if (string.Equals(registered, currentExePath, StringComparison.OrdinalIgnoreCase))",
       "if (string.Equals(registered.Trim(), currentExePath, StringComparison.OrdinalIgnoreCase)) /* mutated */",
       1)]),

    ("normSeparators",
     "比较前把正斜杠归一为反斜杠 —— 场景 G 会被判为一致、不再重写",
     [("if (string.Equals(registered, currentExePath, StringComparison.OrdinalIgnoreCase))",
       "if (string.Equals(registered.Replace('/', '\\\\'), currentExePath, StringComparison.OrdinalIgnoreCase)) /* mutated */",
       1)]),
]

# 调用点变异：探针 §8 的文本断言应当抓住它们。
CALLSITE_MUTATIONS = [
    ("callSiteSilentSave",
     "首次落盘失败改回静默 —— §8 的「绝不静默失败」文本断言应当变红",
     [("""                _tray.ShowBalloon("配置保存失败", message);
                presenter.ShowMessage(message, ToastKind.Failure, StartupToastAnchor());
""",
       "                // mutated: 静默吞掉\n", 1)]),

    ("callSiteNoFilePath",
     "落盘失败的消息里不再给出具体文件路径",
     [('                string message = $"无法写入 {configStore.FilePath}：{ex.Message}";',
       '                string message = "配置保存失败"; /* mutated */', 1)]),

    # 注：本条的方向在最终修复波里**翻转过**。原先是「修复挪到 TrayHost 之后」= 缺陷
    #（简报要求它在之前）；修复 6 把需求倒了过来 —— 修复必须在 TrayHost **之后**，
    # 否则失败时弹不出气泡。所以现在要变异的是"挪回之前"。
    ("callSiteRepairBeforeTrayHost",
     "自启修复挪回 new TrayHost( 之前 —— 失败时弹不出气泡，失败又变回隐蔽",
     [("        _tray = new TrayHost(startup, loaded.Config.Hotkey);",
       """        StartupPathRepair.RepairUsingCurrentExecutable(startup); /* mutated: 挪到 TrayHost 之前 */

        _tray = new TrayHost(startup, loaded.Config.Hotkey);""",
       1)]),

    ("callSiteSaveAfterHotkey",
     "在落盘之前先构造 HotkeyManager —— 破坏更正 6（失败消息里的路径必须已存在）",
     [("        // 首次运行把默认配置落盘。",
       '        _ = new HotkeyManager("Ctrl+Shift+Q"); /* mutated: 把热键构造提到落盘之前 */\n\n'
       "        // 首次运行把默认配置落盘。", 1)]),

    ("callSiteInlineTryRepair",
     "在 TrayApplicationContext 里又内联一份实现（两处实现会各自漂移）",
     [("    private void AddRecent(QrPayload payload)",
       "    private void TryRepairStartupPath(IStartupManager s) { } /* mutated */\n\n"
       "    private void AddRecent(QrPayload payload)", 1)]),
]

# 探针自身变异：验证「防自欺闸门」真的会响。产品代码保持真实。
HARNESS_MUTATIONS = [
    ("deletedSection",
     "删掉整整一节（第 6 节 异常吞掉）—— 断言总数闸门应当把它变成红",
     [('        Section("6. 异常吞掉：只吞清单里的四类", CheckSwallowSemantics);',
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


plan = []

# ── 第一类：产品代码（StartupPathRepair.cs）变异；Program.cs 用真实的那份 ──
for name, desc, edits in MUTATIONS:
    text = apply_edits(impl_original, edits, name)
    if text == impl_original:
        sys.stderr.write(f"变体 {name} 没有产生任何变化\n")
        sys.exit(1)
    emit_compiled(name, desc, program_win, 'StartupPathRepair.cs', 'MutantProbe',
                  'StartupPathRepair.cs', text)
    plan.append((name, desc, os.path.join(work, name, 'MutantProbe.csproj'), ROOT))

# ── 第二类：调用点变异（文本断言）──
for name, desc, edits in CALLSITE_MUTATIONS:
    text = apply_edits(callsite_original, edits, name)
    if text == callsite_original:
        sys.stderr.write(f"变体 {name} 没有产生任何变化\n")
        sys.exit(1)
    fake_root = emit_callsite(name, desc, text)
    plan.append((name, desc, control_csproj, fake_root))

# ── 第三类：探针自身（Program.cs）变异；两份产品源文件都用真实的 ──
for name, desc, edits in HARNESS_MUTATIONS:
    text = apply_edits(probe_original, edits, name)
    if text == probe_original:
        sys.stderr.write(f"变体 {name} 没有产生任何变化\n")
        sys.exit(1)
    emit_compiled(name, desc, 'Program.cs', impl_win, 'MutantProbe', 'Program.cs', text)
    plan.append((name, desc, os.path.join(work, name, 'MutantProbe.csproj'), ROOT))

if not plan:
    sys.stderr.write("没有生成任何变异 —— 脚本什么都没验证，这不是通过\n")
    sys.exit(1)

io.open(os.path.join(work, 'plan.tsv'), 'w', encoding='utf-8', newline='\n').write(
    "".join(f"{n}\t{d}\t{c}\t{r}\n" for n, d, c, r in plan))

print(f"已生成对照组 + {len(plan)} 个变异"
      f"（{len(MUTATIONS)} 产品 + {len(CALLSITE_MUTATIONS)} 调用点 + "
      f"{len(HARNESS_MUTATIONS)} 探针）：{', '.join(n for n, _, _, _ in plan)}")
print()
PY

# ── 阶段 0：对照组 —— 真实实现必须让探针全绿 ─────────────────────────────────
echo "----- 阶段 0：对照组（真实实现，要求探针全绿退出 0）-----"
QRSCAN_REPO_ROOT="$ROOT" dotnet run --project "$WORK/control/Control.csproj" -c Release \
  > "$WORK/control/out.txt" 2>&1
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

# ── 阶段 2：逐个跑 ────────────────────────────────────────────────────────────
caught=0
escaped=0
invalid=0

while IFS=$'\t' read -r name desc csproj run_root; do
  [ -n "$name" ] || continue

  QRSCAN_REPO_ROOT="$run_root" dotnet run --project "$csproj" -c Release \
    > "$WORK/$name.out.txt" 2>&1
  code=$?

  # 探针的汇总行：===== 结果：N 通过 / M 失败 =====
  summary="$(grep -E '^===== 结果：' "$WORK/$name.out.txt" | tail -1)"
  fails="$(printf '%s' "$summary" | sed -n 's/.*\/ \([0-9][0-9]*\) 失败.*/\1/p')"
  passes="$(printf '%s' "$summary" | sed -n 's/^===== 结果：\([0-9][0-9]*\) 通过.*/\1/p')"
  first_fail="$(grep -E '^  \[FAIL\]' "$WORK/$name.out.txt" | head -1)"

  if [ -z "$summary" ] || [ -z "$fails" ] || [ -z "$passes" ]; then
    invalid=$((invalid + 1))
    printf '  [无效] %-26s %s\n' "$name" "$desc"
    printf '         探针没跑起来（dotnet 退出码 = %s，无汇总行）——不是证据\n' "$code"
    grep -E 'error|错误' "$WORK/$name.out.txt" | head -3 | sed 's/^/         | /'
  elif [ "$fails" -gt 0 ]; then
    caught=$((caught + 1))
    printf '  [抓住] %-26s %s\n' "$name" "$desc"
    printf '         %s\n' "$summary"
    [ -n "$first_fail" ] && printf '         首个失败断言：%s\n' "$first_fail"
  else
    escaped=$((escaped + 1))
    printf '  [逃逸] %-26s %s\n' "$name" "$desc"
    printf '         %s（探针跑完了却全绿）\n' "$summary"
    printf '         ← 这个变异没被抓住，说明探针在该路径上缺少鉴别力。\n'
  fi
  echo
done < "$WORK/plan.tsv"

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
