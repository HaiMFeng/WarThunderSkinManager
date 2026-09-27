# shop.blkx 参考（外部资料说明书）

> **性质**：本文档是对《战争雷霆》客户端数据文件 `shop.blkx` 的调查记录与使用说明，
> **与本项目当前代码无关**——程序尚未接入该文件，此处的解析方法与数据结论仅供
> 后续功能设计（如载具国家自动归类的精准化）参考。
>
> 数据快照位置：`docs/ref/shop.blkx`（来源见下，注意其内容会随游戏版本演进）。

---

## 1. 来源

| 项 | 值 |
|---|---|
| 上游项目 | [gszabi99/War-Thunder-Datamine](https://github.com/gszabi99/War-Thunder-Datamine) |
| 文件路径 | `char.vromfs.bin_u/config/shop.blkx`（仓库内） |
| 原始出处 | 游戏客户端 `char.vromfs.bin` 包内的 `config/shop.blk`，经 blkx（JSON 化）导出 |
| 更新方式 | 上游项目随游戏版本自动解包提交，可直接跟踪其 git 历史 |

`War-Thunder-Datamine` 是社区维护的战雷数据解包仓库，把游戏各 `.vromfs.bin` 虚拟文件系统
解包为可读文本（`.blkx` 即 JSON 化的 blk）。`shop.blkx` 对应**科技树商店配置**：
每辆载具在哪个国家的哪个军种分支、第几级（rank）。

## 2. 内容与结构

文件本质是一棵 **JSON 树**，层级为：

```
country_x（国家）
└── force_y（军种：army / aviation / helicopters / ships / boats）
    └── 载具id（叶子块）
        └── rank = 数字（科技树等级）
        └── …其他商店展示字段（价格、奖励倍率等，与本参考无关）
```

**判定规则**：任何「键的值是一个含 `rank` 键的对象」即视为一个**载具条目**，键名即载具内部 id。
军种块与国家块本身不含 `rank`，可据此与载具条目区分。

实测数据规模（2025-09 快照）：

| 指标 | 数值 |
|---|---|
| 去重后载具 id 总数 | **3296** |
| 国家数 | 10（`country_usa` / `country_germany` / `country_ussr` / `country_britain` / `country_japan` / `country_china` / `country_italy` / `country_france` / `country_israel` / `country_sweden`） |
| 军种 | army（陆军）/ aviation（空军）/ helicopters（直升机）/ ships（海军主力）/ boats（海军近岸） |

条目示例（简化）：

```json
"country_usa": {
  "aviation": {
    "f_15e": { "rank": 8, "...": "..." },
    "f_15a_iaf": { "rank": 7 }
  },
  "army": { "us_m2a4": { "rank": 1 } }
}
```

注意同一载具只会出现在一个国家 / 军种下；**不存在跨军种复用同一 id 的情况**。

## 3. 与其他数据源的关系

### 3.1 与 units.csv（内置译名表）的区别

| | `shop.blkx` | `units.csv` |
|---|---|---|
| 内容 | 国家 / 军种 / rank 归属 | 内部 id → 各语言译名 |
| id 形态 | **裸载具 id**（`f_15e`） | **槽位级 id**（`f_15e_1` 短名 / `f_15e_0` 全名 / `f_15e_2` 类型 / `f_15e_shop` 商店名，**无裸 id 行**） |
| 覆盖 | 在售科技树载具（3296） | 全部本地化键（含非载具：`air_defence/*` 防炮阵地、`killstreak` 等；归一化后约 4536 个裸 id） |
| 来源 | 客户端 `shop.blk`（Datamine 解包） | 社区整理的本地化导出 |

两表**正交**：译名查询按 units.csv 的槽位后缀规则（`id+"_1" → id+"_0" → id → id+"_shop"`），
国籍查询按 shop.blkx 的裸 id——互不影响。

### 3.2 双向覆盖结论（实测）

- **shop → units.csv**：仅 **52** 个 shop 载具在 units.csv 中无任何槽位行
  （多为新载具：`j_16`、`mig_35`、`h145m`、希腊系涂装机等）——显示名回退裸 id 即可，影响极小。
- **units.csv → shop**：归一化后 units.csv 约 1292 个 id 不在 shop 中，
  绝大多数是**非载具本地化键**（`air_defence/*`、`killstreak`、`structures/*`），
  另有少量退役 / 活动载具；真正“在售载具缺失”的极少。

### 3.3 与前缀表（CountryResolver）的对比

现行前缀表（`f → france`、`su → ussr` 这类社区经验规则）存在系统性误判：

| 载具 id | 前缀表判定（错误） | shop.blkx 实际 |
|---|---|---|
| `f_15e` | fr（`f_` 被当成法系飞机前缀） | **usa** / aviation |
| `su_30mkk` | ussr | **china** / aviation |
| `f-84f_germany` | fr | **germany** / aviation（缴获 / 外销变体各自归国） |
| `mig_23mla` / `mig_23mld` | 都 ussr | germany / ussr（分国正确） |

且涂装社区 id 与官方 id 高度一致（`f_15e`、`su_30mkk`、`yak-9k`、`la-7` 等实测全部命中）。

## 4. 解析方法

文件是标准 JSON（blkx 已 JSON 化），任意 JSON 解析器可读。要点：

1. **读为字典树**，递归遍历；
2. **载具条目判定**：`value is object && value 含 "rank" 键` → 记 `key → country/force`；
   否则继续下钻（军种块、国家块都不含 `rank`）；
3. **id 归一化**（做匹配时需要）：
   - `_` 与 `-` 视为等价（涂装侧 `a_26c` ↔ shop `a-26c`、`mig-29m_9_15` 这类混合拼写真实存在）；
   - 不 strip 任何尾部数字段——shop 的 id 本身就是裸 id；
4. 输出 `载具id → (country, force)` 扁平映射供查表（快照约 3296 条，内存可忽略）。

C# 侧可用 `System.Text.Json` 的 `JsonDocument` 流式遍历，无需实体类。

## 5. 使用方式（设计意图，尚未实现）

**精准国籍查表**，替换 / 兜底现行前缀规则：

```
查表顺序：
1. shop 映射精确命中（含 _ / - 归一化）→ 直接采用（含军种）
2. 未命中（如中队载具 su-30sm、j-20a 不在 shop）→ 回退现行前缀表
3. 仍无 → unclassified
```

- 收益：消除前缀表的系统性误判（`f_15e`→fr、`su_30mkk`→ussr 这类），
  且附带获得**军种**信息（陆 / 空 / 直升机 / 海军）；
- 代价：需随游戏版本更新快照（可从上游仓库拉取，或让用户自行替换 `docs/ref/shop.blkx`）；
- 不影响译名链路：显示名仍走 units.csv（§3.1 的后缀规则）。

## 6. 注意事项

- **版本漂移**：新版本会新增 / 移除载具，快照需定期更新；未命中的 id 必须有兜底路径；
- **非载具键**：军种块下可能存在非载具的分组节点（无 `rank`），按 §4 的判定规则天然过滤；
- **数据许可**：Datamine 仓库内容来自游戏客户端解包，仅限社区工具内部使用，勿直接再分发原始文件
  到公开制品中（文档内引用少量条目示例无碍）。
