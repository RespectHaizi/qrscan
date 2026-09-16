# QrScan

Windows 轻量级常驻托盘的二维码扫码器。按全局快捷键呼出，框选屏幕上任意二维码 —— 网页、聊天窗口、图片都一视同仁 —— **自动复制到剪贴板**，并可一键打开。

---

## 特性

| | |
|---|---|
| **常驻托盘** | 常驻期**零窗体**（`ApplicationContext` + `NotifyIcon`），不占任务栏、不弹多余窗口 |
| **全局热键** | 默认 `Ctrl+Shift+Q`；热键被占用时会明确告诉你原因，而不是「按了没反应」 |
| **全屏框选** | 遮罩背景是**冻结的桌面快照**，所以「框住的」与「送去识别的像素」逐像素一致 |
| **只做二维码** | 只启用 QR Code 解码器，更快也更少误报 |
| **识别引擎** | [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) 原生库（C++），支持反色码、旋转、缩略图小码 |
| **多码** | 一屏多个码时全部识别，提示条上可循环切换 |
| **自动复制** | 识别成功即刻写入剪贴板；被占用时重试 3 次并**明确报错**，绝不静默失败 |
| **可打开** | 仅放行 `http` / `https` / `mailto` / `tel` 与确实存在的本地路径 |
| **零运行时依赖**（发布后） | 框架依赖单文件发布：一个 `QrScan.exe`，约 3.2 MB |

---

## 环境要求

| | |
|---|---|
| 构建 | .NET SDK 8 |
| 运行 | **.NET 8 Desktop Runtime**（本程序是框架依赖发布，不自带运行时） |
| 系统 | Windows 10 / 11（x64） |

---

## 快速开始

```bash
git clone <this-repo>
cd qrscan

# 构建
dotnet build -c Release

# 跑测试
dotnet test tests/QrScan.Core.Tests

# 运行
dotnet run --project src/QrScan
```

### 发布为单个 exe

```bash
dotnet publish src/QrScan -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

产物是**单个** `publish/QrScan.exe`。

> **单文件 ≠ 自包含。** 目标机仍需安装 .NET 8 Desktop Runtime。单文件只是把应用自己的托管程序集与原生 `ZXing.dll` 打进一个文件；首次运行会把原生库解压到 `%TEMP%\.net\QrScan\<哈希>\`。

---

## 使用

1. 启动后会常驻在系统托盘。
   > ⚠️ **Windows 11 默认把新的托盘图标折叠进溢出面板**（任务栏右下角的 `^` 箭头里）。首次使用可能需要把它拖到常驻区 —— 这不是程序的问题，但确实是最容易让人以为「没启动」的地方。
2. 按 `Ctrl+Shift+Q`，屏幕变暗后**框选**任意二维码。
3. 内容会**自动复制到剪贴板**，同时弹出一条轻提示条：
   - `[打开]` —— 仅当内容可打开时出现
   - `[复制]` —— 重新复制
   - `[还有 N 个 ▸]` —— 一屏多个码时循环切换

**配置文件**：`%APPDATA%\QrScan\config.json`（修改后需重启生效）

```jsonc
{
  "hotkey": "Ctrl+Shift+Q",
  "toastDismissMs": 2000,
  "recentLimit": 10,
  "selfTestOnStartup": true
}
```

**托盘菜单**：扫码 · 最近 10 条 · 开机自启 · 关于 · 退出

---

## 项目结构

```
QrScan.sln
src/
├── QrScan.Core/            类库 —— 业务逻辑，**不引用 WinForms**
│   ├── Abstractions/       接口：屏幕采集、配置、自启、外部启动
│   ├── Models/             QrPayload / AppConfig / PayloadKind
│   ├── QrDecoder.cs        zxing-cpp 封装（多码排序、启动自检）
│   ├── ScreenCapture.cs    虚拟桌面冻结位图（GDI BitBlt）
│   ├── LuminanceConverter.cs  Bitmap → 8 位灰度
│   ├── PayloadClassifier.cs   「能不能打开」的判定与白名单
│   ├── ShellLauncher.cs       唯一的副作用出口
│   ├── ConfigStore.cs / StartupManager.cs / SingleInstance.cs / HotkeySpec.cs
└── QrScan/                 WinForms 应用
    ├── Program.cs          单实例保护 → 消息循环
    ├── TrayApplicationContext.cs
    ├── AppController.cs    状态机与编排
    ├── Native/             User32 P/Invoke、热键
    └── UI/                 TrayHost / OverlayWindow / ToastWindow / ResultPresenter
tests/QrScan.Core.Tests/    xUnit
tools/                      行为探针与变异复核（见下）
```

### 一条刻意的架构约束

`QrScan.Core` 的 csproj **不设置 `UseWindowsForms`**。因此 Core 里任何 `using System.Windows.Forms;` 都会**编译失败** —— 「业务逻辑不依赖 UI」这条边界由编译器强制，而不是靠约定。

---

## 测试

```bash
dotnet test tests/QrScan.Core.Tests        # 116 个用例
```

### 行为探针（`tools/`）

托盘交互、遮罩框选、提示条这类 UI 行为没法用常规单元测试覆盖。本仓库的做法是把它们做成**可独立运行的探针程序**：每个探针打印每项断言的期望值与实测值，任一项失败则进程退出码非 0。

| 探针 | 断言数 | 覆盖 |
|---|---|---|
| `OverlayWindowProbe` | 36 | 框选遮罩：坐标换算（含负原点）、`WM_DPICHANGED`、绘制是否 1:1、取消路径 |
| `ToastWindowProbe` | 88 | 提示条：不抢焦点、悬停暂停、按钮命中测试、位置钳制 |
| `ResultPresenterProbe` | 88 | 剪贴板重试、按钮矩阵、多码切换、「打开」白名单边界 |
| `AppControllerProbe` | 51 | 状态机、反向坐标换算、资源释放在各条退出路径上 |
| `StartupRepairProbe` | 59 | 开机自启路径自动修复 |
| `HotkeyOccupancyProbe` | — | 交互式工具：抢占热键，用于手工验证「热键被占用」的提示路径 |

```bash
for p in OverlayWindowProbe ToastWindowProbe ResultPresenterProbe AppControllerProbe StartupRepairProbe; do
  dotnet run --project tools/$p -c Release; echo "$p exit=$?"
done
```

### 变异复核

**一个永远为绿的检查比没有检查更危险。** 因此每个探针都配了 `run-mutations.sh`：把产品源码复制到临时目录、做一处定向改写（删掉一个守卫、把某个常量改成 0……），再跑完整探针，**要求探针真的变红**才算「抓住」。

判据是「探针输出了汇总行且失败数 > 0」，而**不是**退出码 —— 否则编译失败会被误算成「变异被抓住」。脚本自带对照组（先跑未变异版本要求全绿）与零变异保护。

```bash
bash tools/ToastWindowProbe/run-mutations.sh
```

> 探针用 `<Compile Include="..\..\src\..." Link="..." />` **编入真实源文件**，而不是副本 —— 副本会随源码演进而漂移，探针就会开始验证一份并不存在的代码。

---

## 已知限制

| 限制 | 说明 |
|---|---|
| **多显示器场景未经真机验证** | 「副屏在主屏左侧/上方」时虚拟桌面原点为**负数**。负原点的坐标换算已有自动化覆盖（用合成的负原点矩形），但**真实的负源坐标抓取尚未在双屏机器上执行过**。在多屏机器上首次使用时请确认：框住的区域与识别到的内容是否一致 |
| 仅二维码 | 不识别条形码（只启用 QR Code 解码器，改配置即可扩展） |
| 需要 .NET 8 Desktop Runtime | 框架依赖发布 |
| DRM / 硬件叠加层区域 | 截出来是黑块，属系统保护，无法规避 |
| 首次运行会解压原生库 | 约 3 MB，解压到 `%TEMP%\.net\QrScan\<哈希>\` |
| 自定义协议 scheme 不可打开 | 这是**刻意的安全取舍**：二维码内容来源不可信，而 `ShellExecute` 会把自定义 scheme 交给系统中注册的协议处理器。若扫到这类码，只能手动复制粘贴 |
| 盘符映射到网络共享时 | 路径存在性检查会触发一次网络访问。UNC 路径（`\\`）已被排除，但 `net use Z: ...` 这类映射盘符无法在不引入卡顿的前提下识别 |

---

## 许可

尚未指定开源许可证。若需在他人项目中复用，请先联系作者。

---

## 致谢

二维码识别由 [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) 提供（Apache-2.0），通过官方的
[`ZXingCpp`](https://www.nuget.org/packages/ZXingCpp) .NET 包装包使用。
