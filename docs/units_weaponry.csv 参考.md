# units_weaponry.csv 参考（外部资料说明书）

> **性质**：对《战争雷霆》本地化文件 `units_weaponry.csv` 的调查记录与使用说明。
> 本项目已将其作为**嵌入资源**（`Assets/units_weaponry.csv`）用于部件"武器"识别（§3.6，
> `Services/Catalog/WeaponCatalog`），本文保留格式与解析结论供维护参考。
>
> 数据快照即嵌入资源本体（随程序发布；内容会随游戏版本演进——更新时从上游仓库
> 拉取新表替换 `WarThunderSkinManager/Assets/units_weaponry.csv` 重新编译即可）。

## 1. 来源

| 项 | 值 |
|---|---|
| 上游项目 | [gszabi99/War-Thunder-Datamine](https://github.com/gszabi99/War-Thunder-Datamine) |
| 文件路径 | `lang.vromfs.bin_u/lang/units_weaponry.csv`（仓库内） |
| 原始出处 | 游戏客户端 `lang.vromfs.bin` 本地化包内的 `lang/units_weaponry.csv`（武器名本地化表） |
| 实测规模 | 约 **7277** 行（2025-09 快照） |
| 更新方式 | 上游项目随游戏版本自动解包提交，可直接跟踪其 git 历史 |

## 2. 格式

与 `units.csv` 完全同构（见 `units.csv 参考.md` §2）：

- 无 BOM 的 UTF-8；分隔符 `;`；字段双引号包裹（`""` 转义引号）；
- 表头首列 `<ID|readonly|noverify>`，其后为 `<English>` … `<Chinese>` / `<TChinese>` … 各语言列
  及 `<Comments>` / `<max_chars>`；
- 行示例：

```
"weapons/cannon_am23_tu_95_turret";"23 mm AM-23 cannon";…    ← 类别/武器名/变体
"weapons/su_rds37/short";"☢RDS-37";…                         ← 变体行（short 短名，☢ 是游戏字库图标字形）
"su_r_73";…                                                  ← 无类别的裸 ID
```

**本表只读首列 ID**，各语言译文列一律不解析——用途只需要"名字集合"，不需要译文。

## 3. ID 形态与类别过滤（关键）

ID 通常是 `类别/武器名/变体…` 的斜杠路径：

| 形态 | 示例 | 是否采纳 |
|---|---|---|
| `weapons/<名>/<变体>` | `weapons/su_r_73e_default/short` | ✅ 采纳 |
| 无类别**裸 ID** | `su_r_73`、`128mm_pzgr_ts` | ✅ 采纳 |
| 其他类别 | `explosiveType/...`、`modification/...`、`sonicDamage/...`、`weapons_types/...` | ❌ 忽略（经查是爆炸参数 / 改装件 / 角度阈值等，不是武器） |

## 4. 解析方法（本程序实现，`WeaponCatalog`）

**建索引**（表来源变化时重建，1 秒 TTL 缓存）：

1. 逐行取首列 ID（截到第一个 `;`，去引号与 BOM；`<` 开头的是表头行，跳过）；
2. **类别过滤**（§3）：只采纳 `weapons/…` 与裸 ID，取「类别之后的**第一段**」为武器名
   （`weapons/su_r_73e_default/short` → `su_r_73e`）；
3. **去变体标记**：名字尾部 `_default` / `_short` / `_user_cannon` / `_user_gun` 去掉；
4. **归一化**：去掉所有非字母数字字符后转小写（`su_r_73e` → `sur73e`；`-` / `.` / 空格一并去除）；
5. **双形态登记**：除整体名外，再登记「去掉首个前缀段」的形态——表里的 `cn_pl12` 也能命中
   部件 `pl12_missile_c`（去掉 `cn` 前缀后命中 `pl12`）；
6. 键长 < 3 的丢弃（太短容易误判）。

**匹配**（`IsWeapon(fromWithoutTypeSuffix)`）：

1. 调用方先去掉贴图类型后缀（`_c` / `_n` / `_c_dmg`…，属涂装 blk 的命名习惯，见格式文档）；
2. 去掉 `*` 通配符后按 `_` 分段，**从尾部最多丢 3 段**逐级查表
   （`su_r_77_1_missile` → `su_r_77_1` → `su_r_77` → `su_r`——部件名里常带 `_missile` / `_rail` /
   `_pod` 这类挂载词），任一级命中即算武器；
3. 全部未命中 → 不是武器。

## 5. 用途

- **部件行"武器 / 导弹"红色标签**（§3.6）：涂装包属性页里每个部件位置（`from`）若被判定为武器
  （导弹 / 炸弹 / 机炮 / 火箭弹…），行尾显示红色提示；
- **仅供人工识别参考**：不参与部件匹配、不影响 blk 输出、不写任何数据——判错了最多是标签显示错，
  修正途径是更新本表；
- 典型价值：`from` 命名极不统一（格式文档 §6 的老问题），光看 `cn_pl12_missile_c` 这种名字
  无法机械判断是不是武器，用武器名表模糊命中是最省力的方案。

## 6. 更新与用户替换

与 `units.csv` 完全一致：

- 首次启动导出内置表到 `<配置目录>/ref/units_weaponry.csv`；
- 基线机制决定能否跟随程序更新（§3.9 同思路）：没改过 → 跟随更新；改过 → 保留用户表；
- 替换后约 1 秒自动生效（`DataTables.Stamp` 来源标记），无需重启；
- 也可在设置页「**更新资源**」（§3.15）一键从上游仓库检查并下载新版本（写入用户表，不动基线）。

## 7. 注意事项

- **只维护 ID 集合**：本程序升级此表时不需要核对任何译文列，diff 首列即可；
- 译文列里的 `☢` / `☢` 类前缀是游戏字库的弹药图标字形（同 units.csv 的国旗占位符家族），
  与 ID 无关；
- 误判案例的处理优先级：先确认 `from` 的实际形态（是否带贴图后缀 / 挂载词），
  再考虑往表里补 ID（通过用户表替换，不必改代码）。
