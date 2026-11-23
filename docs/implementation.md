# 桌面模式实现

## 交互入口

主程序没有管理窗口，也不在启动时弹出设置。桌面空白菜单创建空卡片；多选菜单把完整文件集合提交给常驻实例。高级操作与配置集中在托盘，Dock 只负责启动与添加应用。新建、改名和出错时可以出现短暂对话框。

旧 `MainWindow` 已移除。WPF `Window` 只作为生成 HWND 和控件树的技术载体；其原生样式变为 `WS_CHILD`，父级为 Explorer 的 `SHELLDLL_DefView`。

## 模块

| 模块 | 责任 |
| --- | --- |
| `App` | 单实例、命令行、选择清单校验、命令投递、常驻启动 |
| `DesktopCommandChannel` | 当前用户专用命名管道，完整传递 JSON 文件集合 |
| `AppController` | 托盘菜单、卡片 / Dock 生命周期、设置、文件移动期间退出保护 |
| `DesktopLayer` | 定位桌面视图、修改 HWND 父级与样式、物理坐标移动和缩放 |
| `FenceWindow` | 直接操作卡片、折叠、缩放、物理位置保存 |
| `DockWindow` | 快捷应用引用、真实图标、悬停动画和工作区底部定位 |
| `GlassSurface` | 壁纸一次性模糊缓存、区域采样、透明着色与边缘高光 |
| `ShellIntegration` | 当前用户专用注册表键的安装 / 移除和 Shell 刷新通知 |
| `DeskNest.Shell` | 原生 C++ COM 类工厂与 IExplorerCommand 多选入口 |
| `StorageService` | 文件真实移动、重名保护、原子布局保存、备份恢复 |
| `ShellService` | 默认关联打开、经典原生文件菜单和真实图标 |

## 桌面嵌入

枚举顶层窗口，找到其下的 `SHELLDLL_DefView`，将卡片和 Dock 转为其子窗口并置于图标列表上方。没有替换 Explorer，也没有写入桌面背景或图标布局。没有找到桌面时暂时隐藏，定时重试，不回退为普通应用主窗口。

窗口重建检查每两秒执行一次。Explorer 销毁子窗口时，已关闭的卡片和 Dock 会重新创建。实际窗口销毁 / 恢复过程仍需跨 Windows 版本进行人工检查。

子窗口不调用 `Window.DragMove`，而是使用鼠标捕获、屏幕坐标与 `ScreenToClient` 移动。保存的坐标明确标记为物理像素，宽高保留 WPF 逻辑单位；旧布局在实际移动或保存时迁移。底部 Dock 按主屏工作区定位，不修改 Windows 任务栏。

## Shell 多选

当前用户注册表键：

```text
HKCU\Software\Classes\DesktopBackground\Shell\DeskNest.Create
HKCU\Software\Classes\AllFilesystemObjects\shell\DeskNest.Create
HKCU\Software\Classes\CLSID\{683ABCCE-019F-436B-A287-CAE2A3BFE665}\InprocServer32
```

桌面空白入口运行 `--new-card`。多选入口使用 `ExplorerCommandHandler` 和 `MultiSelectModel=Player`。原生 DLL 的 `Invoke(IShellItemArray*)` 枚举完整选择集合，写入一份 UTF-8 JSON 清单，然后启动 DeskNest。不能用 `%1` 静态命令替代，否则可能对每个文件分别启动，无法保证同卡收纳。

DLL 只做清单交接，不移动文件。COM 边界捕获 C++ 异常，不让异常进入 Explorer。主程序校验请求目录、清单大小、操作类型和文件数量，再通过当前用户专用管道交接给已有实例。原生扩展依据微软的 [IExplorerCommand::Invoke 文档](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iexplorercommand-invoke) 实现。

扩展使用静态 C++ 运行库，不要求额外安装 VC 运行库。构建生成带内容版本的 DLL 名称，避免覆盖 Explorer 已加载的文件；`shell-extension.txt` 指向当前版本。

## 玻璃材质

WPF 分层子窗口和桌面宿主的组合不适合直接假定系统 DWM 背景材质可用。本实现读取本地壁纸，模糊一次并共享缓存；拖动时仅改变区域采样，再叠加浅 / 深色透明着色和高光。这是一种 Liquid Glass 的视觉近似，不等同于实时模糊其他窗口，也不声称拥有苹果的折射渲染。

微软提供的 [DWM 系统背景枚举](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type) 对 Acrylic 的版本和窗口行为有明确约束；当前实现没有把简单透明色宣称为原生 Acrylic。

## 文件语义

标题点击进入行内编辑，拖动超过系统阈值后才移动卡片，避免点击与拖动互相误触。名称保存失败会回滚；显示名称变化不改变分区 ID 或文件目录。搜索框在已有文件缓存中过滤，不在每次输入时重新枚举目录或提取图标，目录监听更新后继续应用当前查询。原底部信息栏已移除。

卡片托管会移动真实文件，Dock 固定只保留引用。重名自动编号，不覆盖。移除卡片只移除布局记录，其目录继续保留。原生文件菜单的删除作用于真实文件，按 Windows 的行为执行。

数据目录及其祖先不能被拖入自身。文件夹跨卷移动仍明确拒绝；后续应使用 `IFileOperation` 补充进度和取消，而非自行执行未经完整验证的复制后递归删除。

## 当前验证证据

- 发布构建通过，C# 和原生 C++ 均完成编译；C++ 启用 `/W4 /WX`。
- 28 项文件 / 通信 / 状态与搜索检查通过，包含重名、恢复、真实图标、同用户命名管道、Dock 持久化、真实菜单入口及中文 / 全半角 / 多关键词模糊匹配。
- 9 项卡片控件检查通过：点击标题编辑、Enter 保存且目录不变、Esc 取消、空名称保护、失焦保存、即时搜索、无结果状态、Esc 清空、底栏移除且文件不变。
- 5 项原生扩展检查通过：COM 类工厂与中文标题、无选择隐藏、真实多选启用、一份清单交接并启动进程、扩展自身不移动源文件。
- 桌面集成检查通过：卡片和 Dock 的真实父级是 Explorer 桌面；不显示任务栏项；两文件请求只创建一张卡片；折叠、移动及物理坐标保存有效。
- 真正的应用多进程检查通过：启动常驻实例，再由第二个进程传递清单；两份隔离测试文件被托管，第二进程退出，常驻进程没有普通主窗口。测试最后通过 `--quit` 退出自己的实例。
- 当前用户菜单移除 / 重新安装检查通过，禁用和启用状态均保存成功；测试结束保留最新版菜单入口。
- 卡片浅深色、Dock 使用真实 WPF 控件树生成预览并检查。

以上不会代替对 Win+D、Explorer 重启、多显示器壁纸、真实鼠标拖放和每个第三方菜单命令的完整人工兼容性验证；这些仍明确保留为待验证范围。
