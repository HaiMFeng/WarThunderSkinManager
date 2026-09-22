# WarThunder Skin Manager

管理《战争雷霆》`UserSkins` 自定义涂装的桌面工具。

核心流程：**导入并解构涂装 → 存入资源库 → 按载具组织 → 部件级贴图适配（用户选择）→ 动态重写 `blk` → 以热重载友好的方式输出到游戏 `UserSkins/WTSM`**。

解决两个痛点：

1. **目录堆积导致扫描慢/崩溃**：一个载具大量皮肤全部堆在一个文件夹，游戏扫描缓慢甚至崩溃。
2. **同载具皮肤部件不齐**：例如载具 A 一个皮肤适配了导弹贴图、另一个只做了主体，导致外观不一致。

> 详细设计见 [`docs/`](docs/)。

---

## 技术栈

- **.NET 10 / WPF**（`net10.0-windows`）
- **CommunityToolkit.Mvvm**（`[ObservableProperty]` / `[RelayCommand]` 源生成）
- **纯 WPF**：不使用 WinForms、无第三方 UI 包、无网络依赖
  - 文件夹选择使用 .NET 8+ 内置的 `Microsoft.Win32.OpenFolderDialog`
- 界面文案全部外置为 **JSON 语言文件**（`{loc:Loc key}` 绑定）
- 规划中：`SharpCompress`（压缩包导入：zip / 7z / rar / tar 等）

---

## 目录结构

```
WarThunderSkinManager/
├── WarThunderSkinManager.sln
├── WarThunderSkinManager/            # WPF 主工程
│   ├── Models/                       # 数据模型（见 docs/软件功能设计.md §6）
│   ├── Services/                     # ConfigService / BlkParser / LocalizationManager
│   ├── ViewModels/                   # MainViewModel（CommunityToolkit.Mvvm）
│   ├── Views/                        # 涂装 / 载具 / 设置 三个页面
│   ├── Themes/ThemeResources.xaml    # 设计令牌与组件样式（设计系统）
│   ├── Localization/LocExtension.cs  # {loc:Loc key} 标记扩展
│   └── App.xaml(.cs) / MainWindow.xaml(.cs)
├── docs/                             # 设计文档
│   ├── 软件功能设计.md
│   ├── 界面设计规范.md
│   ├── 涂装文件结构与BLK格式参考.md
│   └── ref/
└── .gitignore
```

---

## 运行时目录（由用户在设置中指定）

| 目录 | 说明 |
|------|------|
| **游戏 UserSkins 目录** | 程序在其下建立 `WTSM/` 作为激活输出 |
| **程序资源存储目录** | 皮肤库，可能几百 GB：`blobs/`（内容寻址去重）+ `packages/<Id>/`（blk + meta.json） |
| **程序配置目录** | 默认 `%LocalAppData%\WarThunderSkinManager` |

配置目录内容：

```
<配置目录>/
├── config.json            # 三个目录路径、同步设置、语言
├── lang/zh-CN.json        # 语言文件（首次运行自动生成，可替换）
├── mappings/vehicles.json # 载具内部标识 ↔ 显示名
└── previews/              # 预览图缓存（png，键=涂装包 id）
```

---

## 构建与运行

前置：Windows + **.NET 10 SDK**。

```bash
# 构建
dotnet build

# 运行
dotnet run --project WarThunderSkinManager
```

---

## 设计文档

- [`docs/软件功能设计.md`](docs/软件功能设计.md) —— 功能模块、三个目录、数据组织（实体模型）、物理落盘与去重
- [`docs/界面设计规范.md`](docs/界面设计规范.md) —— 设计令牌、组件规范、动效规范、国际化
- [`docs/涂装文件结构与BLK格式参考.md`](docs/涂装文件结构与BLK格式参考.md) —— blk 语法与贴图规范

---

## 功能进度

**已完成**
- WPF 外壳：自定义窗口（`WindowChrome` + 自绘标题栏）、左侧图标导航、页面切换动效
- 设计系统：设计令牌、按钮/输入框/卡片/复选框/滚动条等组件样式与过渡动画
- 国际化：语言文件加载与 `{loc:Loc}` 绑定，切换语言刷新界面
- 配置：三个目录 + 同步设置（自动同步、缓冲时间）的读写；选择目录后自动保存
- 数据模型：`Vehicle` / `VehiclePart` / `SkinPackage` / `TexMapping` / `ActiveLoadout` / `Import` / `Country`
- blk 解析：`replace_tex` / `set_tex` → `TexMapping`，并做基础校验（`*`、扩展名、`param`、贴图存在性）

**计划中**
- 导入与解构管道：贴图算哈希入 `blobs/`、写 `packages/<Id>/meta.json`、由 `from` 聚合部件
- 载具管理界面：国家分类、显示名映射、预览图
- 部件级贴图适配与激活输出（重写 `blk` + 调度贴图到 `UserSkins/WTSM/`）
- 导出 / 恢复原始模组
- 压缩包导入（含加密包密码输入）
