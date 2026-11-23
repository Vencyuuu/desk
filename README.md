# DeskNest · 桌面收纳

Windows 10/11 x64 桌面收纳工具，采用 macOS 风格的桌面卡片和底部应用 Dock。**启动后没有管理主窗口**，日常入口为桌面右键和系统托盘。

## 运行新版

先从托盘退出旧版，再运行 `artifacts/DeskNest-Desktop/DeskNest.exe`。需要 .NET 8 Desktop Runtime，当前机器已安装。请保留完整目录，包括原生 Shell 扩展 DLL 和 `shell-extension.txt`，不要只复制 EXE。

默认启动时会安装当前用户的右键菜单，不需要管理员权限。本次开发已在当前用户注册过桌面菜单。移动发布目录后，请从新位置启动程序，或在托盘中选择“桌面右键菜单 → 安装 / 修复”。

## 桌面收纳

- 右键桌面空白处 → **新建 DeskNest 收纳卡片**，直接创建卡片。
- 选中多个文件 → 右键 → **新建收纳卡片并收纳所选文件**，全部所选项移入同一张新卡片。
- Windows 11 上这些入口可能位于“显示更多选项”。
- 卡片是 Explorer 桌面视图的子窗口，不在任务栏或 Alt+Tab 中出现；不再使用普通窗口沉底模拟桌面。
- 点击卡片名称直接编辑：Enter 保存，Esc 取消，移开键盘焦点自动保存；空名称不会覆盖原名。
- 拖动标题仍可移动卡片，拖动右下角调整大小，双击标题空白处或点击箭头折叠。
- 卡片内搜索框即时模糊搜索文件名，支持中文、大小写不敏感、全半角及多个关键词。
  例如“工报”可匹配“工作报告”；Ctrl+F 定位搜索，Esc 或清除按钮恢复全部文件。
- 不显示底部信息栏；操作失败继续通过对话框或托盘提示。
- 文件可拖入卡片、跨卡片移动、多选拖出；同名自动编号，不覆盖已有文件。
- 双击文件或 Enter 使用默认程序打开；文件右键保留经典 Windows Shell 菜单和可用的第三方扩展。
- 卡片名称、颜色、锁定、真实目录、所选文件移回桌面、移除等操作统一放在托盘的“收纳卡片”子菜单中。
- 移除卡片保留文件，从托盘“恢复已移除卡片”可以取回。

## 应用 Dock

- 固定在主显示器工作区底部，位于 Windows 任务栏上方，不修改任务栏设置。
- 单击真实应用图标启动，悬停放大；关闭系统动画时停止过渡动画。
- 拖入 `.exe`、`.lnk`、`.url` 或 `.appref-ms` 固定到 Dock，**只保存引用，不移动源文件**。
- 点击加号或从托盘“底部快捷应用栏”添加应用。
- 从托盘可调整顺序、移除固定项、显示或隐藏 Dock。

## 托盘设置

蓝色分区图标是 DeskNest。右键可新建卡片、配置各卡片、恢复卡片、显示 / 隐藏全部、管理 Dock、切换外观、安装 / 移除桌面菜单、打开收纳目录及退出。双击托盘显示卡片和已启用的 Dock。

“外观”提供浅色、深色、跟随系统，以及可关闭的玻璃效果。玻璃使用**本地壁纸模糊采样、透明着色和边缘高光**近似液态玻璃，不是 Apple Liquid Glass，也不是对其他应用窗口内容的实时折射。壁纸无法读取时回退为普通主题面色。

## 数据与保护

默认目录为 `%LOCALAPPDATA%\DeskNest`：

```text
layout.json               卡片位置、外观、Dock 固定项和菜单设置
layout.json.bak           上一次布局备份
layout.json.damaged-*     恢复时保留的损坏原件
Files/<分区 ID>/           普通文件系统中的真实托管文件
Requests/                 Shell 多选请求的临时清单，交接成功后移除
```

分区使用稳定 ID，改名不改变文件路径。布局原子保存并保留备份。移动过程中禁止退出；移动失败逐项提示，源文件不另行删除。文件原生右键菜单中的“删除”作用于真实文件，与移除卡片不同。

## 边界与待验证项

- 文件夹跨磁盘移动仍暂不支持；同卷文件夹及跨卷单个文件可托管。
- 桌面嵌入依赖 Explorer 的桌面视图结构，已在当前机器验证。不同 Windows 版本、多显示器、Win+D 和 Explorer 重启的完整人工兼容性检查尚未覆盖。
- 玻璃基于静态壁纸采样；不同显示器使用不同壁纸、拼贴或跨屏壁纸时，采样可能不能完全对齐。
- 托盘菜单、鼠标拖放和所有第三方菜单命令仍需实际使用验证；现有自动检查不等同于这些交互的完整覆盖。
- 移动后真实路径会改变。依赖相对路径的应用或快捷方式建议整体文件夹一起收纳。
- 尚无自动分类、开机启动、安装器、云同步或长任务取消按钮。

## 开发

主程序使用 .NET 8 / WPF；菜单扩展使用原生 C++ / IExplorerCommand，不把 .NET 运行时加载进 Explorer。需要 .NET SDK、Visual Studio C++ x64 工具和 Windows SDK。

```powershell
# 构建整个发布目录；-Run 可在完成后启动。
.\Build.ps1

# 常规文件、通信和 Dock 状态检查。
dotnet run --project tests/DeskNest.SmokeTests -- artifacts/storage-tests

# 已注册菜单的验证。
dotnet run --project tests/DeskNest.SmokeTests -- artifacts/storage-tests --shell-installed

# 卡片 / Dock 离屏渲染预览。
dotnet run --project tests/DeskNest.RenderTests -- artifacts/previews-desktop

# 真实桌面嵌入及真正应用的多进程链路检查：仅使用隔离测试文件。
dotnet run --project tests/DeskNest.RenderTests -- artifacts/desktop-integration --desktop
dotnet run --project tests/DeskNest.RenderTests -- artifacts/desktop-pipeline --pipeline
```

原生扩展的独立检查见 `tests/DeskNest.ShellTests`；它将扩展加载到测试进程，接收选择清单的程序是测试替身，不移动源文件。命名管道、Explorer 嵌入和注册表验证需在普通用户会话运行；受限沙箱可能拒绝。

```powershell
# 独立数据目录不自动注册桌面菜单。
& .\artifacts\DeskNest-Desktop\DeskNest.exe --data-dir D:\Env\desk\artifacts\my-test-data

# 部署或维护时使用；命令会转交给已有实例。
& .\artifacts\DeskNest-Desktop\DeskNest.exe --new-card
& .\artifacts\DeskNest-Desktop\DeskNest.exe --quit
& .\artifacts\DeskNest-Desktop\DeskNest.exe --install-shell
& .\artifacts\DeskNest-Desktop\DeskNest.exe --uninstall-shell
```

没有 Prettier 配置；C# 使用 `.editorconfig` 和 `dotnet format whitespace --folder`。源代码主要逻辑有中文注释。没有执行 Git 提交或推送。实现细节与验证范围见 `docs/implementation.md`。
