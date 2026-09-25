# WarThunder Skin Manager

管理《战争雷霆》`UserSkins` 自定义涂装的桌面工具。

核心流程：**导入并解构涂装 → 存入资源库 → 按载具组织 → 部件级贴图适配（用户选择）→ 动态重写 `blk` → 以热重载友好的方式输出到游戏 `UserSkins/WTSM`**。

解决两个痛点：

1. **目录堆积导致扫描慢/崩溃**：一个载具大量皮肤全部堆在一个文件夹，游戏扫描缓慢甚至崩溃。
2. **同载具皮肤部件不齐**：例如载具 A 一个皮肤适配了导弹贴图、另一个只做了主体，导致外观不一致。

> 详细设计见 [`docs/`](docs/)。

## ⚠️ 使用声明

- 本程序只是一个**本地管理工具**，设计意图是让用户**个人**管理、组合自己在游戏内使用的自定义涂装；程序本身不提供、不附带、不传播任何涂装资源。
- **涂装作品的著作权归其原作者所有。** 使用「导出」功能得到的文件中可能包含多位原作者的作品——**公开分享、二次分发前，你必须事先取得所含作品原作者的授权**，并按其要求署名。未经授权的二次分发（包括修改后分发）或任何商业用途，都可能侵犯原作者权益，请自行承担相应责任。
- 因使用本程序而产生的任何版权纠纷与后果，由使用者自行承担；开发者不对用户的使用方式负责，发现侵权内容请向分发者主张。
- 本程序与 Gaijin Entertainment 无任何关联；《战争雷霆》（War Thunder）及相关名称、商标归 Gaijin Entertainment 所有。

**首次使用**：启动时会弹出初始设置向导，指定游戏 UserSkins 目录（输出涂装）与程序资源存储目录（皮肤库）后即可开始导入；关闭向导稍后配置时，任何库操作都会自动引导到设置页完成配置。

---

## 技术栈

- **.NET 10 / WPF**（`net10.0-windows`）
- **CommunityToolkit.Mvvm**（`[ObservableProperty]` / `[RelayCommand]` 源生成）
- **纯 WPF**：不使用 WinForms、无第三方 UI 包、无网络依赖
  - 文件夹选择使用 .NET 8+ 内置的 `Microsoft.Win32.OpenFolderDialog`
- 界面文案全部外置为 **JSON 语言文件**（`{loc:Loc key}` 绑定）
- **SharpCompress**（压缩包导入：zip / 7z / rar / tar / gzip 等，见 §3.1）

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
│   ├── Assets/units.csv              # 内置载具译名表（嵌入资源，内部标识→各语言译名）
│   ├── Assets/units_weaponry.csv     # 内置武器名表（嵌入资源，用于识别武器部件）
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
├── config.json                    # 三个目录路径、语言、导入选项记忆等
├── lang/zh-CN.json                # 语言文件（首次运行自动生成，可替换）
├── lang/_zh-CN.defaults.json      # 内置文案基线（内部文件，用于内置文案更新）
├── mappings/vehicles.json         # 载具内部标识 ↔ 显示名
├── mappings/vehicle_countries.json# 载具 → 国家（用户手动归类，优先于前缀推断）
├── mappings/part_groups.json      # 多源复用组（可选，§3.13：组内 from 的贴图可互相选用）
├── loadouts/<载具Id>.json         # 该载具激活的涂装包（{ activePackageId }）
├── ref/units.csv                  # 载具译名表（首次启动自动写出，可直接改；可单独替换更新）
├── ref/units_weaponry.csv         # 武器名表（同上）
├── ref/_units.defaults.csv        # 基线（内部文件：判断表有没有被改过，决定能否随程序更新）
├── ref/_units_weaponry.defaults.csv
└── previews/                      # 预览图缓存（png，键=涂装包 id）
```

> 部件贴图配置属于**涂装包**（`packages/<Id>/meta.json` 的 `parts`），不属于载具。
> `ref/` 里的两张表**用户表优先、内置表兜底**，替换后立即生效（无需重启）；设置页可「导出内置表」作为更新起点。

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

## 许可

本项目采用 **GNU AGPL-3.0** 许可，详见 [`LICENSE`](LICENSE)。
