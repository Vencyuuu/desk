# DeskNest · 桌面收纳

面向 Windows 10/11 x64 的桌面整理工具，提供 macOS 风格的收纳卡片、底部快捷应用 Dock 和可调整的玻璃材质。

DeskNest 启动后直接融入桌面，不显示管理主窗口。通过桌面右键创建卡片，通过系统托盘管理卡片、Dock 和设置。

> **文件托管方式：**拖入卡片的文件会移动到数据目录，原位置不再保留；拖入 Dock 则只固定引用，不移动原文件。建议先用少量测试文件熟悉操作。

## 目录

- [主要特性](#主要特性)
- [运行要求与安装](#运行要求与安装)
- [快速开始](#快速开始)
- [卡片使用说明](#卡片使用说明)
- [Dock 使用说明](#dock-使用说明)
- [外观与字体设置](#外观与字体设置)
- [数据目录备份与迁移](#数据目录备份与迁移)
- [升级与卸载](#升级与卸载)
- [常见问题](#常见问题)
- [已知限制](#已知限制)
- [从源码构建](#从源码构建)
- [制作 GitHub 发布包](#制作-github-发布包)
- [测试与项目结构](#测试与项目结构)
- [问题反馈](#问题反馈)

## 主要特性

| 功能 | 说明 |
| --- | --- |
| 桌面卡片 | 创建、改名、移动、缩放、折叠、锁定、移除和恢复卡片 |
| 桌面右键集成 | 在空白处创建卡片，或将多个选中文件收纳到一张新卡片 |
| 真实文件托管 | 文件移动到专用目录，使用系统默认程序打开 |
| 原生文件菜单 | 调用经典 Windows Shell 右键菜单及可用的第三方菜单扩展 |
| 文件名模糊搜索 | 支持中文、大小写不敏感、全半角及多个关键词 |
| 快捷应用 Dock | 拖入固定、拖出移除、拖动排序、名称提示及悬停放大 |
| 独立卡片颜色 | 每张卡片可以设置自己的背景色和标题色 |
| 字体预览与联动 | 使用本机已安装字体，总字体同步或分区域独立配置 |
| 外观调整 | 浅色、深色、跟随系统，卡片透明度、玻璃效果、Dock 高度与底部间距 |
| 数据迁移 | 复制校验后切换目录，支持关联手动搬移后的完整数据 |
| 托盘常驻 | 按需打开设置，支持当前用户登录后自启动 |

## 运行要求与安装

### 运行要求

- Windows 10 或 Windows 11，x64 系统，使用 Windows Explorer 桌面。
- **.NET 8 Desktop Runtime x64**。请安装桌面运行时；仅安装普通 .NET Runtime 或 ASP.NET Core Runtime 不足以运行 WPF 应用。
- 将程序和数据放在当前用户具有读写权限的位置。

当前发布方式为需要外部运行时的 ZIP 压缩包，没有安装向导或自动更新器。

普通用户只需运行时，无需安装 Visual Studio 或 .NET SDK；从源码构建时才需要完整开发工具。当前没有单文件或自包含运行时版本。

### 安装步骤

1. 从仓库的 **Releases** 页面下载发布压缩包，例如 `DeskNest-Desktop-Windows-x64.zip`。不要将 GitHub 的源码 ZIP 当作可运行发布包。
2. 将压缩包完整解压到一个固定目录，例如 `%LOCALAPPDATA%\Programs\DeskNest`。不要直接在压缩包内运行。
3. 保留发布目录中的全部文件，包括 `DeskNest.exe`、主程序 DLL、运行配置、`shell-extension.txt` 和原生 Shell 扩展 DLL。不要只复制 EXE。
4. 启动 `DeskNest.exe`。通知区域会出现 DeskNest 托盘图标，初次运行显示底部 Dock；卡片由用户创建。
5. 如果找不到托盘图标，展开任务栏通知区域的隐藏图标。

默认启动会注册当前用户的桌面右键菜单，通常不需要管理员权限。程序目录应保持固定，以便右键菜单和自启动项找到应用。

解压后的核心文件如下，其中 Shell 扩展文件名的哈希随构建变化：

```text
DeskNest-Desktop/
├─ DeskNest.exe
├─ DeskNest.dll
├─ DeskNest.deps.json
├─ DeskNest.runtimeconfig.json
├─ DeskNest.Shell.<内容哈希>.dll
├─ shell-extension.txt
└─ 使用说明.md
```

如果仓库尚未提供 Release 附件，可按[从源码构建](#从源码构建)生成此目录。程序不会自动开启自启动，需要在托盘或设置页中自行勾选。

## 快速开始

1. 右键桌面空白处，选择 **新建 DeskNest 收纳卡片**。
2. 将几个文件拖入卡片，确认原文件已移动到托管目录。
3. 双击卡片中的文件，使用系统默认程序打开。
4. 点击卡片标题改名，按 Enter 保存。
5. 将常用应用的快捷方式或文件夹拖入底部 Dock，单击图标打开。
6. 右键托盘图标，选择 **设置…**，调整外观、字体和数据位置。

Windows 11 的桌面菜单入口可能位于 **显示更多选项** 中。

## 卡片使用说明

### 创建与整理

| 操作 | 方法 |
| --- | --- |
| 创建空卡片 | 桌面空白处右键，或从托盘新建 |
| 收纳桌面多选文件 | 选中文件后右键，选择“新建收纳卡片并收纳所选文件” |
| 导入文件 | 将文件或支持移动的文件夹拖入卡片 |
| 卡片间移动 | 将文件从一张卡片拖到另一张卡片 |
| 修改名称 | 点击标题，Enter 保存、Esc 取消，移开键盘焦点也会保存 |
| 移动卡片 | 拖动标题区域 |
| 调整大小 | 拖动右下角；锁定后不能调整 |
| 折叠与展开 | 点击标题旁箭头，或双击标题空白处 |
| 打开文件 | 双击文件，或选中后按 Enter |
| 使用系统菜单 | 右键卡片中的文件 |
| 移回桌面 | 选中文件，从托盘对应卡片菜单选择“所选文件移回桌面” |
| 显示或隐藏 | 使用托盘中的显示 / 隐藏入口；双击托盘图标可显示卡片和已启用的 Dock |

导入遇到同名文件时自动编号，不覆盖已有文件。导入中的单项失败会报告原因，不会阻断其他可处理项目。卡片支持文件多选与拖出，实际复制或移动行为由目标程序和系统拖放规则决定。

### 搜索与快捷键

搜索框只匹配当前卡片内的文件名，不搜索文件内容，也不递归搜索子文件夹。

- 输入“工报”可匹配“工作报告”。
- 支持间隔字符匹配、大小写不敏感和全半角归一化。
- 可用空格输入多个关键词。
- Ctrl+F 定位搜索框；Esc 或清除按钮清空搜索。
- F2 编辑卡片名称；Ctrl+N 新建卡片。

### 独立颜色与卡片管理

进入 **托盘 → 收纳卡片 → 对应卡片**：

- 选择背景颜色、标题颜色，只影响这一张卡片。
- 背景色继续使用全局卡片透明度和玻璃效果。
- 标题颜色优先级：**卡片独立颜色 → 全局标题颜色 → 主题文字颜色**。
- “背景恢复跟随主题”和“标题恢复全局设置”可分别清除覆盖。
- 还可编辑名称及标记颜色、锁定卡片、打开真实目录、移回文件和移除卡片。

**移除卡片会保留托管文件**，可从托盘“恢复已移除卡片”找回。文件原生右键菜单中的“删除”则作用于真实文件，与移除卡片不同。

## Dock 使用说明

Dock 位于主显示器可用桌面底部，保留 Windows 任务栏的位置，不修改任务栏设置。

- 拖入文件夹、`.exe`、`.lnk`、`.url` 或 `.appref-ms` 固定入口。
- 固定操作只记录路径，不复制或移动源文件。
- 单击图标打开目标；悬停显示名称并放大图标。
- 在 Dock 内拖动图标可调整顺序。
- 将图标拖出 Dock 后松开可移除固定项，源文件或应用不会被删除；Esc 可取消拖动。
- 固定项较多时可用滚轮横向浏览。
- 空 Dock 仍保留拖入区域；也可在托盘管理固定项或隐藏整个 Dock。

如果固定项原来的路径发生变化，请移除失效项后重新拖入。数据目录迁移会尝试修复指向托管目录内部的固定项，外部路径仍由用户管理。

## 外观与字体设置

从 **托盘 → 设置…** 打开设置页。更改即时应用并自动保存，设置页不会在正常启动时自动弹出。

| 设置 | 范围或行为 |
| --- | --- |
| 开机自启动 | 当前用户登录 Windows 后运行，默认不开启 |
| 主题 | 浅色、深色、跟随系统 |
| Dock 高度 | 64–128 逻辑像素 |
| Dock 距底部距离 | 0–300 逻辑像素，默认 12；以可用桌面底边为基准，按 DPI 换算并限制在工作区内 |
| 卡片背景透明度 | 0–80%，文字和文件图标保持清晰 |
| 玻璃效果 | 可开启或关闭 |
| 总字体 | 一次同步卡片、Dock 和设置页字体 |
| 分区域字体 | 分别选择卡片、Dock 和设置页字体 |
| 卡片标题字号 | 10–32，编辑框和标题区域随行高调整 |
| 全局标题颜色 | 自选颜色或跟随主题；允许单卡片覆盖 |

字体列表来自本机已安装字体，每项以对应字体预览，支持键盘输入名称定位。修改总字体后仍可单独覆盖某一区域；三个区域字体不同时，总字体栏留空。

卡片字体用于标题、文件名、搜索及其他文字；Dock 字体用于名称和拖动提示；设置页字体也用于分区编辑对话框。图标不随文字字体更换。字体缺失时使用默认字体，同时保留原选择；安装相应字体后重启程序即可使用。

玻璃材质使用**本地壁纸模糊采样、透明着色和边缘高光**近似液态玻璃。它不是 Apple Liquid Glass，也不会实时折射其他应用窗口。壁纸读取失败时回退到主题背景。

## 数据目录备份与迁移

### 程序目录与数据目录

程序目录存放 EXE 和 DLL；数据目录存放真实文件及布局。**覆盖更新程序不需要重新导入文件。**

默认数据入口为 `%LOCALAPPDATA%\DeskNest`。在资源管理器地址栏输入此路径即可打开。

```text
DeskNest/
├─ layout.json              卡片布局、Dock、字体、颜色及其他设置
├─ layout.json.bak          上一次布局备份
├─ layout.json.damaged-*    布局恢复时保留的损坏原件（如有）
├─ Files/
│  └─ <卡片 ID>/            真实托管文件；包含已移除卡片保留的文件
├─ location.json            更改活动目录后的定位信息（如有）
├─ Requests/                桌面右键多选请求临时清单
└─ .desknest.lock           防止多个实例同时使用数据的运行锁
```

卡片以稳定 ID 对应文件夹，修改卡片名称不会改动文件路径。布局以临时文件加原子替换方式保存，并保留备份。

更改数据目录后，`layout.json` 与 `Files` 位于新的**活动数据目录**；原入口目录保留定位信息。默认 Shell 请求目录仍在 `%LOCALAPPDATA%\DeskNest\Requests`。不要因为迁移成功就删除整个原入口目录。

### 备份

1. 从托盘退出 DeskNest，使布局保存完成。
2. 完整复制活动数据目录，包含 `layout.json`、`Files` 及保留文件。
3. 如果更改过位置，同时备份原入口目录中的定位信息，至少保留 `location.json`。
4. 将备份放在其他位置；仅备份 JSON 无法恢复真实托管文件。

自动布局备份不是完整文件备份，不能代替单独的数据备份。

### 迁移到新目录

进入 **设置 → 数据目录 → 迁移到新目录…**，选择一个空目录：

1. 迁移前保存布局，暂停应用内文件操作。
2. 复制全部数据，逐文件进行 SHA-256 校验，支持跨磁盘复制。
3. 验证成功后切换活动目录，重新加载卡片和文件监听。
4. 更新内部 Dock 引用，以及托管 `.lnk` 中可重新定位的目标、工作目录和图标路径。
5. 原目录保留为备份，不自动删除；后续修改以新活动目录为准。

复制阶段支持取消。文件正在被写入、目标非空、空间不足、校验失败或检测到源目录变化时不会切换。迁移前请关闭正在编辑托管文件的程序。若完整副本已生成但最后切换失败，该副本可能保留，可在确认完整后使用关联入口。

### 关联已有目录

手动搬移前请退出程序。搬移后通过 **设置 → 数据目录 → 关联已有目录…** 选择包含 `layout.json` 和 `Files` 的完整目录。

关联会使用所选目录的卡片、文件和设置，不合并两套布局，也不会将旧目录中新增的文件自动合并过去。若已配置目录在启动时不可用，程序会提示重新关联，避免直接生成空白布局。

DeskNest 无法统一改写外部程序的“最近文件”、文档内嵌绝对路径等引用。跨电脑恢复后，外部应用路径、文件关联和字体可能需要在新电脑重新配置。

## 升级与卸载

### 保留数据升级

1. 在目标电脑从托盘退出旧版。
2. 按上文完整备份数据；改过目录的还需保留原入口定位信息。
3. 将新版压缩包完整解压并覆盖**原程序目录**，不要覆盖或清空数据目录。
4. 从原位置运行 `DeskNest.exe`，程序应继续读取现有卡片和文件。
5. 抽查几个文件的打开操作，确认正常后再处理旧备份。

如果移动了程序目录，请在新位置启动，然后执行 **托盘 → 桌面右键菜单 → 安装 / 修复**。已开启自启动的，请关闭再开启一次以更新程序路径。

升级后出现空卡片列表时，先检查数据目录位置，不要重新导入同一批文件；使用“关联已有目录”选择原来的完整数据目录。旧版本不保证能读取新版本增加的全部配置，回退前请保留备份。

### 卸载并保留文件

1. 关闭开机自启动。
2. 执行 **托盘 → 桌面右键菜单 → 移除菜单集成**。
3. 退出程序，再删除程序目录。
4. 保留数据目录，或先将托管文件移回桌面 / 其他位置。

**不要直接删除含 `Files` 的数据目录，除非已备份或取回全部文件。** Shell 扩展 DLL 若仍被 Explorer 占用，可在退出登录后再删除。

## 常见问题

### 双击后提示缺少运行时

确认已安装 **.NET 8 Desktop Runtime x64**，并完整解压发布包。普通 .NET Runtime 与 ASP.NET Core Runtime 不能代替桌面运行时。

### 启动后没有窗口

这是正常行为。检查任务栏托盘隐藏图标及桌面底部 Dock，通过桌面右键或托盘创建卡片。

### 桌面右键找不到入口

Windows 11 先检查“显示更多选项”。再从托盘执行“桌面右键菜单 → 安装 / 修复”，确认程序目录中的原生扩展 DLL 和 `shell-extension.txt` 未被移走。

### Dock 图标无法打开

目标文件、快捷方式或应用路径可能已改变。检查实际路径，重新固定；跨电脑使用时需要在目标电脑安装对应应用。

### 数据目录提示被占用

同一份数据不能同时被多个 DeskNest 实例使用。退出其他正在使用它的实例后重试，不要仅删除锁文件来绕过保护。

### 设置无法保存或迁移失败

检查目录写入权限、磁盘剩余空间，关闭正在编辑相关文件的程序。迁移使用空目标目录；已有完整 DeskNest 数据应选择“关联已有目录”。保留原数据和错误信息后再排查。

## 已知限制

- 当前面向 Windows x64，未提供 macOS / Linux 版本。
- 文件夹跨磁盘的普通拖入托管暂不支持；同卷文件夹、跨卷单个文件可以托管。数据目录迁移采用单独的复制校验流程，可跨磁盘。
- 数据迁移遇到符号链接或目录联接会明确拒绝复制，不跟随链接搬移外部数据。
- 桌面嵌入依赖 Explorer 的桌面视图结构。不同 Windows 版本、Win+D、Explorer 重启及多显示器组合仍需进一步兼容性验证。
- 原生菜单受系统文件关联及第三方扩展影响，不能保证所有第三方菜单命令在每台电脑上行为一致。
- 不同显示器壁纸、拼贴或跨屏壁纸可能造成玻璃采样不完全对齐。
- 普通文件导入暂不支持取消；复制迁移支持取消。文件操作进行中会阻止退出。
- 当前没有自动分类、云同步、安装器或自动更新功能。

## 从源码构建

### 开发环境

- Windows x64。
- .NET 8 SDK。
- Visual Studio 或 Visual Studio Build Tools，安装 C++ x64 工具及 Windows SDK。
- PowerShell。

在仓库根目录运行：

```powershell
.\Build.ps1

# 构建后启动
.\Build.ps1 -Run
```

构建脚本发布 WPF 程序并编译原生 Shell 扩展，产物位于 `artifacts/DeskNest-Desktop/`。构建前请退出正在使用该目录的 DeskNest。脚本会复制 README 为发布目录中的 `使用说明.md`，不会自动生成 ZIP。

Shell 扩展使用内容哈希命名，`shell-extension.txt` 指向当前 DLL。发布时应包含完整运行文件与当前扩展，不需要发布测试数据、构建缓存或用户数据。

### 命令行入口

```powershell
.\artifacts\DeskNest-Desktop\DeskNest.exe --new-card
.\artifacts\DeskNest-Desktop\DeskNest.exe --quit
.\artifacts\DeskNest-Desktop\DeskNest.exe --install-shell
.\artifacts\DeskNest-Desktop\DeskNest.exe --uninstall-shell

# 独立入口 / 测试实例；默认不自动安装桌面菜单
.\artifacts\DeskNest-Desktop\DeskNest.exe --data-dir "$PWD\artifacts\my-test-data"
```

重复启动会将命令交给同一入口的现有实例。`--data-dir` 指定的是稳定入口目录；活动数据位置可能再通过 `location.json` 指向其他目录。不同入口实例仍不能同时使用同一份活动数据。Shell 多选交接参数由扩展生成，日常使用无需手动传递。

## 制作 GitHub 发布包

以下步骤供仓库维护者使用，普通用户无需执行。

1. 退出正在使用构建产物的 DeskNest，运行 `Build.ps1`。
2. 在独立测试数据目录运行所需回归检查，确认卡片、Dock、右键菜单和数据更新流程可用。
3. 打包主程序运行文件、`shell-extension.txt` 指定的当前原生扩展，以及使用说明。构建目录可能留有以前的扩展 DLL，发布时只需包含当前文件。
4. 在 GitHub 创建对应版本的 Release，上传 ZIP；发布说明写明运行要求、新增功能、修复内容、已知限制和更新方式。
5. 在另一台 Windows x64 电脑上完整解压验证，并使用测试数据确认覆盖更新后布局和文件仍可读取。

在仓库根目录执行以下命令可制作压缩包；输出为 `artifacts/DeskNest-Desktop-Windows-x64.zip`：

```powershell
$releaseDirectory = (Resolve-Path '.\artifacts\DeskNest-Desktop').Path
$extension = (Get-Content -LiteralPath (
    Join-Path $releaseDirectory 'shell-extension.txt'
) -Raw).Trim()

$fileNames = @(
    'DeskNest.exe'
    'DeskNest.dll'
    'DeskNest.deps.json'
    'DeskNest.runtimeconfig.json'
    'shell-extension.txt'
    '使用说明.md'
    $extension
)
$files = $fileNames | ForEach-Object {
    Join-Path $releaseDirectory $_
}

Compress-Archive -LiteralPath $files `
    -DestinationPath '.\artifacts\DeskNest-Desktop-Windows-x64.zip' `
    -Force
```

发布包不应包含真实用户的 `Files`、`layout.json`、`location.json`、测试夹具、本机目录映射或构建缓存。源码仓库也应排除本机专用配置；`.gitignore` 当前已排除 `artifacts`、`.build`、`bin`、`obj` 等产物目录。项目许可应由维护者明确选择并在根目录提供相应 `LICENSE`，README 不预设许可类型。

## 测试与项目结构

主程序使用 C# / .NET 8 / WPF；原生菜单扩展使用 C++ / `IExplorerCommand`，不向 Explorer 加载 .NET 运行时。

```text
src/DeskNest/                主程序、视图、配置、存储及桌面互操作
src/DeskNest.Shell/          原生右键菜单扩展
tests/DeskNest.SmokeTests/   文件操作、配置、迁移及通信检查
tests/DeskNest.RenderTests/  WPF 控件、渲染及桌面集成检查
tests/DeskNest.ShellTests/   原生扩展检查
docs/implementation.md      实现说明与验证范围
```

先运行 `Build.ps1`。RenderTests 引用发布目录中的主程序程序集。

```powershell
# 文件、配置、迁移和通信
dotnet run --project tests/DeskNest.SmokeTests -- artifacts/storage-tests

# 已安装桌面菜单时的额外验证
dotnet run --project tests/DeskNest.SmokeTests -- artifacts/storage-tests --shell-installed

# 控件与设置
dotnet run --project tests/DeskNest.RenderTests -- artifacts/settings-tests --settings
dotnet run --project tests/DeskNest.RenderTests -- artifacts/typography-tests --typography
dotnet run --project tests/DeskNest.RenderTests -- artifacts/card-tests --card-interactions
dotnet run --project tests/DeskNest.RenderTests -- artifacts/visual-tests --visual-probe --assert-visuals

# 迁移及真实桌面 / 多进程链路
dotnet run --project tests/DeskNest.RenderTests -- artifacts/data-tests --data-directory
dotnet run --project tests/DeskNest.RenderTests -- artifacts/desktop-tests --desktop
dotnet run --project tests/DeskNest.RenderTests -- artifacts/drag-stability --drag-stability
dotnet run --project tests/DeskNest.RenderTests -- artifacts/pipeline-tests --pipeline --relocated
```

测试使用指定产物目录下的隔离文件。部分测试会显示测试卡片或启动隔离应用实例；命名管道、Explorer 嵌入及注册表检查需要交互式 Windows 用户会话，受限沙箱或无桌面 CI 可能无法运行。原生扩展独立检查说明见 `tests/DeskNest.ShellTests`，更多验证范围见 [实现说明](docs/implementation.md)。

代码格式遵循 `.editorconfig`，C# 可运行 `dotnet format whitespace --folder`。主要业务逻辑使用中文注释。

## 问题反馈

提交 Issue 时请提供 Windows 版本、DeskNest 版本或使用的发布包、显示缩放比例、问题复现步骤和错误信息。涉及桌面布局时请说明显示器数量；涉及路径时可脱敏，但保留路径结构与文件类型。

请勿公开上传托管文件、完整个人数据目录或包含隐私内容的截图。对于文件操作问题，先保留原数据及备份，再使用可公开的测试文件复现。
