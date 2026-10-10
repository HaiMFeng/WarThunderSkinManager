# WT Live API 分析报告（涂装 / Camouflage 类）

> 适用站点：`https://live.warthunder.com/`
> 分析目标：摸清涂装（camouflage）类内容的 API 端点、请求方式，支撑"应用内涂装浏览器"。
> 分析日期：2026-10-09
> 分析方法：`playwright-cli`（Chromium）抓取 + 反编译 `main.js` + 直接 HTTP 调用验证
> 参考资产：`docs\units.csv 参考.md`、`WarThunderSkinManager\Assets\units.csv`

---

[TOC]

## 1. 核心结论（速览）

| 结论 | 说明 |
|---|---|
| **列表/筛选/预览无需登录** | `get_regular` 匿名可调用，返回内容与登录态一致（已用 `cn_m1a2t` 对拍验证）。WT Live 对 feed 类接口**未做认证限制**。 |
| **主列表端点** | `POST /api/feed/get_regular/` |
| **载具字段名来源** | URL / 参数里的 `vehicle=<裸 id>`，取自 `units.csv` 首列键**去掉类别前缀与 `_N` 后缀**（如 `cn_m1a2t_0` → `cn_m1a2t`）。 |
| **分页机制** | `page` 从 `0` 开始，每页 **25** 条；某页返回 `<25` 即末页。 |
| **时间序** | `sort=created` 时按发布时间**倒序**返回，越往后越早（与用户观察一致）。 |
| **下载直链已内嵌列表** | 每条涂装已带 `file.link`（形如 `https://live.warthunder.com/dl/<hash>/`），无需额外请求即可得到下载地址。 |
| **指定作者公开主页（`get_user`）免登录** | 给定有效作者 id，`POST /api/feed/get_user/` **匿名即返回该作者作品**（HTTP 200，25/页，结构同 `get_regular`）；可作"按作者浏览"公开能力。详见 §3.5。 |
| **你自己的订阅/隐藏需完整 SSO（不可裸 Cookie 复刻）** | `get_subscribes_users` / `get_hidden` 等**私人**接口拒绝匿名与"裸 Cookie + JWT 复刻"（实测均 404）；必须走 Gaijin gsea SSO 客户端兑换（浏览器/WebView2 内 `fetch` 或网络拦截）。详见 §11、§12.9。 |
| **产品范围决策：放弃登录与订阅展示** | 本涂装浏览器**不实现用户登录**，也**不展示订阅作者作品/个人内容/隐藏内容**。范围仅保留公开能力（列表浏览、按载具筛选、按作者浏览、预览、下载）。详见 §14。 |

---

## 2. 页面与入口

- 涂装主页：`https://live.warthunder.com/feed/camouflages/`
- 首屏为服务端渲染 HTML，正文列表由 JS（`main.js` 中的 `Feed` 对象）异步拉取。
- 内联脚本给出页面默认配置：

```js
Feed.sort    = 'rating';   // 默认"最佳/周榜" → 标题 "Best camouflages for the past week"
Feed.period  = 7;          // 近 7 天
Feed.content = 'camouflage';
Feed.page    = 0;
Feed.type    = 'regular';
```

`Feed` 对象的完整默认配置（来自 `main.js`）：

```js
{ type:"regular", content:null, sort:"date", user:null, period:null,
  searchString:null, page:0, subtype:"all", additional:{} }
```

---

## 3. 涂装列表接口：`POST /api/feed/get_regular/`（重点）

### 3.1 请求

- **URL**：`https://live.warthunder.com/api/feed/get_regular/`
- **方法**：`POST`
- **Content-Type**：`application/x-www-form-urlencoded; charset=UTF-8`
- **推荐 Headers**：
  - `X-Requested-With: XMLHttpRequest`（站点校验，建议带）
  - `Referer: https://live.warthunder.com/feed/camouflages/`
  - `User-Agent: <常规浏览器 UA>`

#### 参数表

| 参数 | 必填 | 取值 | 说明 |
|---|---|---|---|
| `content` | 是 | `camouflage` | 内容类型，固定为 camouflage（涂装） |
| `sort` | 否 | `created` / `rating` / `comments` / `downloads` | `created`=最近发布（时间倒序）；`rating`=热门；其余见名知义 |
| `page` | 是 | 整数，从 `0` 起 | 分页页码 |
| `period` | 否 | `0` / `1` / `7` / `30` / `365` 等 | 时间范围（天）；`0` 或不传=全部时间 |
| `subtype` | 否 | `all` | 子类型筛选，默认 `all` |
| `searchString` | 否 | `#tag`（多个用空格分隔） | **标签搜索**（就是页面 URL 的 `q` 参数；**不带 `#` 的裸词返回 0 条**，详见 §3.6） |
| `user` | 否 | `0` | **对 `get_regular` 无效（2026-10-10 实测被忽略）**：传非零作者 id 仍返回各作者混合的常规流；按作者浏览必须改用 `get_user`（§3.5） |
| `vehicle` | 否 | `units.csv` 裸 id，如 `cn_m1a2t` | **按载具筛选**（见第 4 节） |
| `additional` | 否 | `{}` | 附加过滤，普通列表为空对象 |

> 注意：`main.js` 中常规 feed **没有** `vehicle` 过滤的 UI，但 `get_regular` 接口实际支持 `vehicle` 参数（实测有效）。这是实现"某载具全部涂装"的关键。
>
> **更正（2026-10-10 实测）：`user` 参数对 `get_regular` 不生效。** 同一套 `content/sort/page/...`
> 只改 `user`，`user=132424191` 与 `user=0` 返回的是**同一形态的常规流**（实测首屏 `author.id`
> 出现 18 个不同值）。按作者浏览的唯一正确入口是 **`POST /api/feed/get_user/`**（§3.5，
> 站点 `Feed.type='profile'`）：实测同一 `user=132424191` 下 `author.id` **恒等于该 id**、分页有效
> （`page=0/1` 内容不同）、返回结构逐字段同 `get_regular`。即"按作者"在站点侧走的是**另一个端点**，
> 不是同一个端点的参数。

### 3.2 响应结构

顶层：

```json
{
  "status": "OK",
  "data": {
    "list": [ /* 涂装数组，见下 */ ],
    "pageTitle": "<string>",
    "link": "<string>"          // 当前筛选对应的页面链接
  }
}
```

> **更正（2026-10-09 实测）**：顶层 `status` 是**字符串** `"OK"`（本文档此前记为 `<int>`）；
> 成败按它判定（与 `posts/get` 的 "ERR" 同一习惯）。`data` 的键实测为 `list` / `pageTitle` / `link`。

单条涂装（`data.list[i]`）字段（已用真实响应核对）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `lang_group` | int | **帖子定位 id**，用于构造网址：`https://live.warthunder.com/post/<lang_group>/<language>/` |
| `id` | int | 该语言版本的具体帖子 id |
| `language` | string | 语言码，如 `en` / `ru` … |
| `type` | string | 固定 `camouflage` |
| `created` | int | 发布时间（Unix 秒） |
| `author` | object | `{id, nickname, avatar}`，头像来自 `cdn-live.warthunder.com` |
| `likes` / `views` / `downloads` / `comments` | int | 互动计数 |
| `description` | string | 简介（含 HTML 标签与 `#标签`） |
| `images` | **array** | 预览图**数组**（2026-10-09 实测：1~4 张，非对象）；每项 `{id, type, src, width, height, ratio}`，`src` 为 `cdn-live.warthunder.com` 缩略图（路径带 `_lq` 低清，`width`/`height` 就是这张图的尺寸——实测恒为 386 宽）。取首项作列表卡片缩略图 |
| `file` | object | **下载信息**：`{id, name, link, type, size}`；`link` 即下载直链 |
| `pbr_ready` | bool | 是否 PBR 就绪 |
| `inverted_roughness` | bool | 粗糙度反转标记 |
| `isAuthor` / `isLiked` / `visible` / `featured` / `isSpecial` / `isPinned` / `isMarketSuitable` / `canDelete` / `canEdit` / `doubt` | 各类布尔标志 | 权限/状态位 |

#### 3.2.1 预览图的清晰度分级（2026-10-09 实测）

同一张预览图在 CDN 上是**按后缀分级的同一路径**，只是最后一级目录带不同后缀段：

| 路径形态 | 实测尺寸 | 实测体积 | 可用性 |
|---|---|---|---|
| `…/<hash>_lq/<文件名>` | 386×607 | 404 KB | ✅ 列表接口 **给出的就是这个** |
| `…/<hash>_mq/<文件名>` | 800×1258 | 1.5 MB | ✅ 存在 |
| `…/<hash>/<文件名>`（去掉 `_lq`） | 916×1440 | 619 KB | ✅ 即**原图** |
| `…/<hash>_hq/<文件名>` | — | — | ❌ 404 |

- 抽样 6 条帖子：`_lq` 与"去后缀原图"**全部可用**；原图体积约 `_lq` 的 2~8 倍（0.2~1.1 MB），
  宽高比与 `_lq` 一致（不是裁切，而是同图更高分辨率）。
- 体积因图而异（上表是同一张图的对比）：`_mq` 不一定比原图小，**不要按体积选档**，按分辨率选。
- 客户端按用户选的档位改写路径即可拿更清楚的图（实现见 `Services/WtLiveQualityCatalog.cs`，
  设置项「WT Live 卡片图片清晰度」默认低清）；改写后 404 / 解码失败时**回退列表给的低清 URL**。
- 详情接口（§13.1 的 `posts/get`）里 `images[].orig.src` 就是上面的"原图"、`images[].mq` 是 `_mq` 变体，
  但**列表页不需要多请求一次**——路径规则就够。

### 3.3 分页

- `page=0` 起，每页 25。
- 当某次返回 `list.length < 25`，即为最后一页，停止翻页。
- 实测 `vehicle=cn_m1a2t`：`page 0~2` 各 25，`page 3` 返回 24 → 总计 **99** 条。

### 3.4 鉴权

- **无需登录**：匿名请求返回数据与登录态完全一致（已验证）。
- 仅当访问**你自己的私人内容**（`get_subscribes_users` / `get_hidden`，见 §11/§12.9）或**实际下载文件**时才涉及会话（下载直链本身匿名可见，下载 zip 附件亦匿名可用，见 §13）。

### 3.5 变体：按作者浏览（`get_user`，**匿名可用**）

- **端点**：`POST /api/feed/get_user/`
- **机制**：与 `get_regular` 同一套 Feed，`type=profile`，必须带 `user=<作者 id>`；用于取**指定作者公开主页**的作品流。
- **重要更正（2026-10-09 实测）**：`get_user` **不是**登录私有接口。给定**有效作者 id** 时，**匿名即可返回该作者作品列表**（HTTP 200，25/页）；此前 §11 误记为"需登录 404"，是当时传了 `user=0`（无效）所致。真正需登录的是 `get_subscribes_users`（你的订阅）与 `get_hidden`（你的隐藏），见 §11 / §12.9。
- **请求参数**（同 `get_regular`，多一个 `user`）：
  - `user=<作者 id>`：取自作者主页 URL `/user/<id>/` 路径。
  - `content=camouflage`：限定只取涂装（不加则含该作者全部类型内容）。
  - `sort=created`（时间倒序）/ `sort=rating`（榜序）、`page=0` 起翻页、`subtype=all`、`period=0`、`searchString=`。
- **返回结构**：与 `get_regular` **逐字段一致**（含 `file.link` 下载直链、`images` 预览、`author.id` 等），下游解析/下载逻辑可直接复用。
- **实测**（`user=132424191`，匿名）：`list=25`；`page=1` 内容不同（分页有效）；`author.id=132424191` 命中；`file.link` 形如 `https://live.warthunder.com/dl/<hash>/`。
- **产品意义**：这是**免登录的公开能力**，可作为"按作者筛选/浏览"维度加入"应用内涂装浏览器"，与 §14（放弃登录）范围完全兼容。

### 3.6 标签搜索（`searchString`，2026-10-10 实测）

站点**只做标签搜索，没有全文搜索**：`searchString` 整个被当作标签查询处理，
它同时就是页面 URL 里的 `q` 参数（`?q=%23anime` ↔ `searchString=#anime`，`%23` 即 `#`）。

| 请求 | 结果 | 结论 |
|---|---|---|
| `searchString=#anime` | 25 条（`link` = `/feed/camouflages/?q=%23anime`） | 标签搜索的正面用例 |
| `searchString=anime`（裸词） | **0 条** | 不带 `#` 什么都搜不到 |
| `searchString=#anime #skin` | 25 条（`link` 回写 `q=%23anime+%23skin`） | 多标签：**空格分隔**，服务端原样回写 |
| `searchString=#anime #不存在的标签` | 25 条（与只搜 `#anime` 相同） | 多标签是**并集**（命中任一），**不是交集** |
| `searchString=#ANIME` / `#Anime` | 25 条 | **大小写不敏感** |
| `searchString=#anime  #skin`（两个空格） | 25 条（`q=%23anime++%23skin`） | 服务端**不归一空格**；站点前端会并成一个 |
| `searchString=#f-15` | **0 条** | 连字符不是标签字符 → 标签字符集是 `[A-Za-z0-9_]` |

> 站点搜索框的行为（前端）：用户输入什么都会被**自动补上 `#`**（输入 `anime` 时页面里就变成 `#anime`），
> 连续空格也会被并成一个。所以网页上永远不会发出裸词查询——这与"裸词 0 条"是同一件事的两面。
> 应用侧发请求时也应按**单个空格**拼接（见 `Services/Online/WtLiveTag.cs`）。

#### 3.6.1 描述里的标签（`description` 字段）

`description` 是**服务端渲染好的 HTML**，其中的标签已经被包成了锚点：

```html
<a href="//live.warthunder.com/?q=%23anime" target="_blank" class="WTL-Embed-Hashtag">#anime</a>
```

- 多个标签是**连续拼接、中间没有空格**（实测）：`...#anime</a><a ...>#girls_frontline</a><a ...>#cm11</a>...`
  → 剥掉 HTML 转纯文本后就是 `#anime#girls_frontline#cm11`（作者怎么写的都不影响，服务端已经分好词）。
- 因此"从描述里认标签"最可靠的办法是**认 `?q=%23` 锚点**；认不到（站点改版 / 描述没被渲染）
  再退回对纯文本做 `#` + `[A-Za-z0-9_]` 匹配——此时 `#` 天然就是分隔符，连写也切得开。
- 应用侧据此把详情里的标签做成可点链接，点一下 = 按该标签搜索（查询形态见本节表格）。

---

## 4. 载具字段名（vehicle）的来源与映射

### 4.1 `units.csv` 结构

- 路径：`WarThunderSkinManager\Assets\units.csv`
- 首列（键）形如：
  - 带类别前缀：`ships/uss_cv_immortal_0`、`tracked_vehicles/ussr_t34_85_increased_pitch_1`
  - 无前缀：`cn_m1a2t_0`、`cn_m1a2t_1`、`cn_m1a2t_2`、`cn_m1a2t_shop`
- 后缀含义：`_0`=全名，`_1`=短名，`_2`=类别（MBT 等）；`_shop`=商城版。
- 其余列为多语言名称（en/ru/zh 等）。

### 4.2 裸 id 提取规则

WT Live 的 `vehicle` 参数使用**去前缀、去 `_N` 后缀**的裸 id：

```
"ships/uss_cv_immortal_0"  → "uss_cv_immortal"
"tracked_vehicles/ussr_t34_85_increased_pitch_1" → "ussr_t34_85_increased_pitch"
"cn_m1a2t_0"               → "cn_m1a2t"
```

即：**取首列键 → 去掉 `/` 之前类别前缀 → 去掉末尾 `_数字` / `_shop` 等后缀**。

### 4.3 验证

- `cn_m1a2t` 在 `units.csv` 中存在（行：`cn_m1a2t_shop` / `cn_m1a2t_0` / `cn_m1a2t_1` / `cn_m1a2t_2`）。
- 以 `vehicle=cn_m1a2t` 调用 `get_regular`，成功返回 **99** 条该载具涂装，与登录用户所见一致。

---

## 5. 排序与时间序

- 可选 `sort`：`created`（最近）｜`rating`（热门）｜`comments`（评论）｜`downloads`（下载）。
- `sort=created` 时返回**发布时间倒序**：列表第一个 = 最新，越往后越早——与用户"随滚动逐步加载、越往后越早"的描述一致。
- 因此"应用内浏览器"要呈现时间线时，直接 `sort=created` 逐页拉取即可，无需额外排序。

---

## 6. 帖子详情与下载

### 6.1 帖子详情端点

- `POST /api/posts/get_post/`
- 参数：`id`（= `lang_group`）、`language`、`content`、`sort`、`user`
- 返回 HTML 片段（_lightbox 内容)。一般列表浏览**不需要**该端点，仅点开详情/下载时用到。

### 6.2 下载直链（关键利好）

`file.link` **已直接内嵌在 `get_regular` 的每条记录中**，示例：

```
https://live.warthunder.com/dl/d6eee2eadd3b943b7f4de841ceda651f79010b31/
```

字段：`file = {id, name:"...zip", link:"...", type:"application/zip", size:111263}`。

- **预览图**：`images.src`（`https://cdn-live.warthunder.com/uploads/...`）。
- **已确认（见 §13.2）**：下载直链 URL 匿名可见，且**实际下载 zip 附件同样无需登录会话 Cookie**。

---

## 7. 其它 feed 端点（`main.js` 中 `l` 映射）

| type | 端点 |
|---|---|
| `regular` | `/api/feed/get_regular/` |
| `featured` | `/api/feed/get_featured/` |
| `profile`(user) | `/api/feed/get_user/` |
| `head` | `/api/feed/get_head/` |

`get_user` 用于**指定作者的公开主页作品流**；给定有效 `user=<作者 id>` 时**匿名即可返回**（HTTP 200，25/页，结构同 `get_regular`），可作"按作者浏览"（见 §3.5）。`get_head` 为列表头部推荐位，未实测。

---

## 8. 对"应用内涂装浏览器"的落地建议

1. **列表/筛选/预览全部免登录**：直接用 `get_regular`，无需用户授权即可浏览、搜索、按载具筛。
2. **载具筛选**：本地内置 `units.csv`，按第 4.2 规则提取裸 id → 作为 `vehicle` 参数，向用户展示可读名称（多语言列）。
   - 搜索下拉对载具名做**模糊匹配**（`Services/Catalog/VehicleSearchIndex`）：先把裸 id 与显示名**归一化**
     （转小写 + 去掉所有非字母数字字符，分隔符 / 国旗占位符 / 多语言写法一视同仁），再按
     「完全相同 → 前缀 → 包含 → 子序列」**分档**打分——因此 `su30` 能命中 `su_30` / `Su-30`，
     `苏30` 也能命中中文译名，跳字输入（`su30venezuela`）也认。
   - **刻意不做编辑距离（Levenshtein）级容错**：载具 id 里"同族不同型号"遍地都是
     （`m1a1`/`m1a2`、`t-72a`/`t-72b`、`f-15c`/`f-15e`），允许打错一个字符会让它们互相误认——误报比漏报更糟。
3. **时间线浏览**：`sort=created` + 逐页 `page++` 直到 `<25`，实现无限滚动。
4. **预览**：用 `images.src`（CDN 缩略图），`file.name`/`file.size` 展示文件信息。
5. **下载**：用 `file.link` 作为下载地址；**但下载动作需先验证匿名是否可取到文件**，若被限登录，则下载器需携带会话 Cookie（引导用户登录一次或复用浏览器 Cookie）。
   - **重复下载提醒**（`Services/Packages/PackageLinkService`）：按帖子 id 比对（URL 里的 `<id>` 就是 `lang_group`；`api/posts/get` 的 `id` 是**语言版本内部 id**，拿它当 `lang_group` 会 `status=ERR`），
     语言段 / 尾斜杠 / 大小写差异视为同一帖。两个来源：① 包自己的 `meta.sourceUrl`；② **老包补认**——
     早期版本下载的包没写过链接，但导入清单 `imports/import_*.json` 的 `sourcePath` 指向
     `<资源目录>/imports/extract/wtlive/<帖子id>-<随机8位>-<原文件名>.zip`，帖子 id 就在包名第一段里。
   - **提醒时机**：帖子 id 从链接就能拿到，所以卡片 / 详情浮窗的下载按钮走**开确认窗之前**先问
     （已下过的帖子不必再走一遍确认窗）；链接要等窗口读出来才知道的空白入口（涂装管理页）则在
     窗内点「开始下载」之后问 —— 两处**只问一次**（见 `SkinsViewModel.OpenWtLiveImport`）。
6. **去重/缓存**：以 `lang_group` 为主键，跨 `sort`/翻页去重；可做本地缓存减少请求。
7. **限流**：实测翻页间加 ~1s 间隔、断连重试即可稳定；建议客户端做简单节流，避免触发风控。
8. **成败判定与类型过滤**：一律先看响应体 `status`（HTTP 恒 200，参数错误也返回 200，**不可靠**）；
   只处理 `type == "camouflage"`，其余类型（瞄具 / 任务等）明确拒绝。
9. **显示名来源**：优先 `file.name` 去扩展名（涂装模板包常带载具前缀，与游戏内命名习惯一致），其次帖子标题；
   `description` 首行多为宣传语，**不建议**用来命名（正文还需剥 HTML 标签，见 §3.6.1）。
10. **不做轮询、不带 Cookie**：浏览级使用单次交互单请求即可满足；不要携带用户 Cookie（隐私 + 规避站点风控）。

### 8.1 C# 调用示例（HttpClient）

```csharp
using var client = new HttpClient();
client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
client.DefaultRequestHeaders.Referer = new Uri("https://live.warthunder.com/feed/camouflages/");
client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 ...");

var form = new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["content"] = "camouflage",
    ["sort"]    = "created",
    ["page"]    = "0",
    ["period"]  = "0",
    ["subtype"] = "all",
    ["searchString"] = "",
    ["user"]    = "0",
    ["vehicle"] = "cn_m1a2t"   // = units.csv 裸 id
});

var resp = await client.PostAsync("https://live.warthunder.com/api/feed/get_regular/", form);
var json = await resp.Content.ReadAsStringAsync();
// 解析 data.list[]：lang_group / images.src / file.link / created ...
```

---

## 9. 实测数据示例（vehicle = cn_m1a2t）

- **总数**：**99** 条涂装
- **首个（最新，sort=created 列表首条）**：`https://live.warthunder.com/post/1190518/en/`
- **最后一个（最早）**：`https://live.warthunder.com/post/1084764/en/`

---

## 10. 待补充 / 风险提示

| 事项 | 状态 |
|---|---|
| 下载直链匿名能否实际取文件（zip） | **已确认：匿名可用**（见 §13.2） |
| `get_user` 是否需登录 | **已验证：匿名可用**（给定有效 `user=<作者id>` 即返回 200；见 §3.5） |
| `period` 完整取值枚举 | 待验证（已知 0/1/7/30/365 等） |
| 站点限流/风控阈值 | 待观察（实测每页间隔 1s 稳定） |
| 多语言 `language` 对 `vehicle` 筛选的影响 | 列表返回多语言混合，`lang_group` 唯一；按 `lang_group` 去重即可 |
| 站点前置 **Cloudflare** | 带常规浏览器 UA 的请求可过；若触发挑战页（返回 HTML 而非 JSON），应**提示用户**而不是重试轰炸 |
| 接口无官方文档、可能改版 | 解析失效要有清晰的失败提示路径，且**失败不影响工具的其他功能**；字段形态以实测为准 |
| 下载文件的来源安全 | 附件来自外部站点，zip 条目路径校验等**来源安全检查**不能省（见 §13.2） |

---

## 11. 订阅作者页（subscribes/users）考察（2026-10-09 补充）

### 11.1 页面与端点

- 页面：`https://live.warthunder.com/subscribes/users/`（关注作者的作品）/ `…/subscribes/tags/`（关注标签）
- 对应后端端点（取自 `main.js` 的 `l` 映射）：
  - `subscribesUsers → /api/feed/get_subscribes_users/`
  - `subscribesTags  → /api/feed/get_subscribes_tags/`
- 相关端点（取自 `main.js` 的 `l` 映射）：`get_user`（**指定作者的公开主页流，匿名可用，见 §3.5**）、`get_hidden`（隐藏内容）。其中 `get_user` 易与"需登录"混淆，特此厘清：**`get_user` 取的是某个公开作者主页的作品，给定有效作者 id 即匿名 200；真正需登录的是"你自己的"订阅/隐藏**（见下）。

### 11.2 鉴权结论（**订阅/隐藏需登录；指定作者主页免登录**）

- 匿名直接访问 `/subscribes/users/`：服务端**回退渲染为普通 `regular` feed**（`Feed.type='regular'`、`content='all'`、无订阅视图），即未登录看不到"你关注的作者"的内容。
- 匿名直接调用 `POST /api/feed/get_subscribes_users/`：返回 **HTTP 404**（服务器对未认证的"私人订阅 feed"一律判定为不存在）。
- **对照验证（同一时刻，2026-10-09 实测）**：

| 端点 | 说明 | 匿名（有效参数）结果 |
|---|---|---|
| `get_regular` | 公开总览 | 200，list=25 |
| `get_user` | 指定作者公开主页（`user=<有效id>`） | **200，list=25（匿名可用）** |
| `get_user` | 无效 `user=0` | 404（参数错误，非鉴权） |
| `get_subscribes_users` | 你关注的作者（私人） | 404 |
| `get_subscribes_tags` | 你关注的标签（私人） | 404 |
| `get_hidden` | 你的隐藏内容（私人） | 404 |

> **结论（修正，2026-10-09 实测）**：
> - **`get_user`（指定作者公开主页）= 公开、匿名可用**，仅要求 `user` 为有效作者 id（之前记为"需登录 404"是传了 `user=0` 所致）。可作为"按作者浏览"公开能力（§3.5）。
> - **`get_subscribes_users` / `get_subscribes_tags` / `get_hidden`（你自己的订阅/隐藏）= 真正的私有 feed**，不能靠"匿名"或"裸 Cookie 复刻"调用——两者直接请求均 **404**。关键不是"有没有 `token` Cookie"，而是是否走完了 **Gaijin gsea SSO 客户端兑换**（见 §12.9）：`main.js` 在收到 gsea 返回的 JWT 后建立 `live.warthunder.com` 会话上下文，私有 feed 才放行。纯 `HttpClient` 即使带有效 `token` + `identity_sid` + gsea `Bearer` JWT，仍 404（headless 实测已验证）。
> - 与"公开浏览免登录"的 `get_regular` / `get_user` 形成对照——**公开总览/指定作者免登录；你自己的订阅/隐藏必须走完整浏览器 SSO（WebView）**。

### 11.3 请求形态（针对私有订阅/隐藏 feed）

- 方法/编码/Headers 同 `get_regular`（`POST` + `application/x-www-form-urlencoded` + `X-Requested-With`）。
- 参数与 `get_regular` 同形（`content` / `page` / `period` 等）；但**排序与搜索 UI 在该视图下被前端禁用**（`subscribesUsers`/`subscribesTags` 类型时 `FloatingMenu` 的 `$sorting`/`$search` 加 `hidden` 类），实质为纯时间线。
- **不能**仅凭"携带会话 Cookie 的 `HttpClient`"调用：裸 Cookie 复刻实测 404（见 §12.9）。私有 feed 只能在**已完成 gsea SSO 的浏览器/WebView 页面上下文**内发起（如 `WebView2.ExecuteScriptAsync` 在已登录页面里跑 `fetch`），由页面既有的会话上下文放行。
- （注：同为 `get_user` 但用于"指定作者公开浏览"时**无需**上述条件，按 §3.5 匿名调用即可。）

### 11.4 对"应用内涂装浏览器"的影响

- **"你关注的作者"订阅流**无法匿名实现，且其鉴权依赖 Gaijin gsea SSO 客户端兑换（§12.9），纯 `HttpClient` + Cookie 复刻已被证伪。
- 但**"按指定作者 id 浏览其公开作品"（`get_user`）是匿名的**，可作为公开的"按作者筛选"能力纳入（§3.5），与放弃登录的范围决策不冲突。
- **最终决策（见 §14）：放弃登录与"我的订阅/隐藏"展示功能**——产品范围保留公开能力（列表浏览、按载具筛选、按作者浏览、预览、下载）。订阅/隐藏等私有 feed **不纳入实现**。
- 本节与第 12 节仅作技术调研存档；若未来需扩展"我的订阅"，再按 §12.4 + §12.9 的 WebView2 方案立项。

---

## 12. 登录流程考察（2026-10-09 补充）

### 12.1 触发点与真实地址

- 页面 Sign In 元素：
  `<a data-test-id="Sign In" href="https://login.gaijin.net" class="GCM-Menu-Item GCM-Menu-Item__sign-in">…</a>`
  该链接由 **Gaijin 通用菜单（GCM）脚本**渲染，`main.js` 中无 `gaijin.net` 引用（即登录跳转不归 `main.js` 管）。
- 实际跳转地址（GCM 在点击时补 `ret` 回跳参数）：
  `https://login.gaijin.net/en/?ret=<URL-encoded 回跳地址>`
- 裸域 `https://login.gaijin.net` 返回 **302 → `/en`**；带 `ret` 时透传为 `/en/?ret=https%3A%2F%2Flive.warthunder.com%2F`。

### 12.2 登录表单（Gaijin 账号 SSO）

- 登录页：`https://login.gaijin.net/en/`
- **提交端点**：`POST https://login.gaijin.net/en/sso/login/procedure/`
- 表单字段（`form#js-form`）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `login` | email | 邮箱账号（name=`login`） |
| `password` | password | 密码（name=`password`） |
| `action` | hidden | 通常为空 |
| `referer` | hidden | 空，可能由 JS 填充 |
| `fingerprint` | hidden | **JS 生成的设备指纹**（反爬，必须前端执行） |
| `app_id` | hidden | Gaijin 应用 id（由 JS / referrer 确定） |

- 另有注册表单 `GET /en/profile/register`。

### 12.3 登录后如何建立 live.warthunder.com 会话

- SSO 成功后，`login.gaijin.net` 下发 **Gaijin 账号会话 Cookie**（`.login.gaijin.net` 域：`identity_sid` / `identity_id` / `identity_token` / `uuid` / `slc`，实测 gsea 流程会写入），并按 `ret` 重定向回 `live.warthunder.com`。
- 随后 **Gaijin gsea 客户端**（`login.gaijin.net/api/gsea/fetch`）回传 JWT，`live.warthunder.com` 的 `main.js` 消费该 JWT 完成**站点会话兑换**（详见 §12.9）。兑换后页面内 `live_wt.user_id` 变为非零、`Auth.user.id` 被赋值（匿名态分别为 `0` / `null`）。
- 该**兑换后的站点会话上下文**是 `get_subscribes_users` / `get_hidden` 等**私人 feed 放行**的前提（注意：`get_user` 取指定作者公开主页是**匿名**的，不在此列，见 §3.5）——但**光有 `token` Cookie 不够**（裸 Cookie 直接 fetch 仍 404，见 §12.9 实测）。
- 佐证：匿名访问 feed 页 `Set-Cookie` 为空；即使注入有效 `token`+`identity_sid`，headless 下因 `main.js` 未跑完 gsea 兑换，私有 feed 仍 404。

### 12.4 对"应用内涂装浏览器"的实现建议（重要修正，并已放弃）

> **范围决策见 §14：本产品放弃用户登录与订阅展示功能。** 以下 WebView2 方案**仅作技术调研存档**，当前不纳入实现；若未来需"我的订阅"再启用。

- **不要直接脚本化提交账号密码**：存在 `fingerprint` 设备指纹、可能的验证码/2FA、CSRF 与风控，极易失败且涉及用户凭证安全。
- **技术可行路径（WebView2 页面内脚本方案，存档备用）**：
  1. 应用内嵌 **WebView2**（Edge WebView2），导航到 `https://live.warthunder.com/feed/camouflages/`（或任意 wtlive 页）。
  2. 用户在页面内点 Sign In → **浮窗登录**（Gaijin SSO，含 2FA），由 gsea 客户端 + `main.js` 自然完成站点会话兑换。
  3. 之后用 `WebView2.ExecuteScriptAsync` **在已登录页面上下文里**执行 `fetch('/api/feed/get_subscribes_users/', {method:'POST', headers:{'X-Requested-With':'XMLHttpRequest','Content-Type':'application/x-www-form-urlencoded'}, body:'content=camouflage&sort=created&page=0&period=0&subtype=all&searchString=&user=0', credentials:'same-origin'})`，返回 JSON 即订阅作者作品。
  - 这一方式的本质：让请求"继承"页面的 gsea 会话上下文，绕开裸 Cookie 复刻 404 的问题。
  - **备选**：在 WebView2 中挂 `WebResourceResponseReceived` / 网络拦截，直接捕获页面自己发出的 `/api/feed/get_subscribes_users/` 响应（无需自己构造请求）。
- **公开 feed（`get_regular` 等）仍可用匿名 `HttpClient`**，保持免登录浏览体验；**私有 feed 必须用上面的 WebView2 页面内脚本/拦截**（纯 `HttpClient` + Cookie 不可行，见 §12.9）。

### 12.5 登录流程实测发现（2026-10-09 实测）

> 以真实账号走了一遍 SSO（账密不落盘），结论如下：

- **密码前端 base64 编码**：提交时密码并非明文，前端先 base64 后再以 `password_hidden` 字段 POST。纯脚本复刻需先对密码做 base64 编码。
- **两步验证（2FA）**：开启 2FA 的账号，首次提交账密后进入第二步——`/en/sso/login/procedure/` 返回「Enter the verification code from the app」页，需继续提交 `code`（TOTP，来自 Gaijin Pass / Google Authenticator / War Thunder Assistant）+ `request_id`（关联本次会话）。无验证码则无法完成登录。
- **结论强化**：账密 + 可能的 TOTP 使纯 HTTP 脚本登录极难稳定实现；**嵌入式浏览器（WebView）方案成为唯一务实路径**——真实用户在熟悉的 Gaijin 登录页完成密码与验证码输入，应用仅负责收割回跳后的 `live.warthunder.com` 会话 Cookie。

---

### 12.6 登录步骤（实测流程，2026-10-09）

> 以真实账号走通 SSO 并抓取 Cookie，步骤如下：

1. **打开登录页**：`https://login.gaijin.net/en/?ret=<wtlive 页面>`。
   - `ret` 建议用真实 wtlive 页面（如 `https://live.warthunder.com/feed/camouflages/`）；若只填根域，Gaijin 可能把回跳重定向到**商店个人主页**而非 wtlive。
2. **输入邮箱/密码**：前端把密码 base64 后，以 `password_hidden` 字段 POST 到 `https://login.gaijin.net/en/sso/login/procedure/`（同表单还含 `action`/`referer`/`fingerprint`/`app_id`）。
3. **两步验证（2FA）**：开启 2FA 的账号进入第二步，页面含隐藏域 `request_id` 与 `code` 输入框；提交 `code`（TOTP）+ `request_id`（关联本次会话）完成。
4. **建立 wtlive 会话**：SSO 回跳 wtlive 后，wtlive 后端据此设置**本站会话 Cookie**（使 `live_wt.user_id > 0`）。
   - ⚠️ 若回跳被重定向到商店主页，wtlive 会话**未建立**——需再显式访问一次 wtlive 以完成 SSO 静默回调，才会下发真正可用的会话 Cookie。
5. **收割 Cookie**：取 `live.warthunder.com` 域的全部 Cookie（会话 Cookie + `token` 这个 CSRF 令牌），供私有 feed 使用。

**实测坑**：仅拿到 `token` Cookie（此时 `live_wt.user_id = 0`）不足以通过 `get_subscribes_users`（仍 404）；必须拿到 `user_id > 0` 的真实会话 Cookie。CSRF 方面，wtlive 使用 `<meta name="csrf-token">` / `csrf-param`，但 `get_regular` 这类匿名 GET 型 POST 无需它；改版后写操作是否强校验 CSRF 待观察。

### 12.7 能否脱离无头浏览器（纯 HTTP 登录）考察

**结论：技术上可行，但脆弱。**

纯 HTTP 复刻 SSO 的合同（已在报告中验证）：

```
GET  https://login.gaijin.net/en/?ret=<wtlive页面>     # 取 gaijin.net Cookie
POST https://login.gaijin.net/en/sso/login/procedure/  # login=<邮箱>, password_hidden=<base64(密码)>, fingerprint, action, referer, app_id
  → 凭证正确则返回含 request_id 隐藏域的 2FA 页
POST https://login.gaijin.net/en/sso/login/procedure/  # code=<TOTP>, request_id=<上一步取得>, (携带 gaijin.net 会话 Cookie)
  → 跟随回跳到 wtlive，CookieJar 捕获 wtlive 会话 Cookie
```

**支撑证据**（无凭证 dummy 测试）：向 step-1 提交 base64 假密码 + 伪造 `fingerprint`，服务器返回 `200` + "invalid"（凭证错误页），**并未因 `fingerprint` 伪造/缺失而拒绝** → `fingerprint` 服务端大概率不严格校验，纯 HTTP 复刻无障碍。

**风险与边界**：
- `fingerprint` 行为可能随时改为强校验；
- `ret` 必须真正落回 wtlive 才能拿到 wtlive 会话（见 12.6 第 4 步的商店重定向坑）；
- 每次登录仍需用户输入 2FA `code`（无法自动化）；
- Gaijin 改版易使该复刻失效。

**对应用的建议**：纯 HTTP 登录"能做"，但若只是为避免打包浏览器，**内嵌 WebView（用户在熟悉的 Gaijin 页输密码+验证码）仍是最稳方案**——登录后由应用收割 `live.warthunder.com` Cookie 复用即可，无需逐请求复刻 SSO。若坚持纯 HTTP，则需实现上述四步并妥善保存 `request_id` 与会话 Cookie。

### 12.8 登录 UI 的两种形态（实测补充，2026-10-09）

- **整页 SSO**：点击顶栏 Sign In（`<a href="https://login.gaijin.net">`，GCM 补 `ret`）→ 全屏打开 `login.gaijin.net/en/?ret=…`，是"跳出站点"的方式。
- **wtlive 内嵌浮窗登录**：当已在 `live.warthunder.com` 上时再点 Sign In，弹出的是**浮窗/弹层**（背后仍可见 wtlive，非整页跳转），内部嵌入 Gaijin SSO；在此完成密码 + 2FA 后，wtlive **原位**登录成功。这是更顺、且**必然落到 wtlive 会话**的方式。
- **关键现象**：整页 SSO 若在 2FA 未完成时就按 `ret` 回跳 wtlive，页面会显示"未登录"（`user_id=0`）；只有**完整走完 SSO（含 2FA）**才建立 wtlive 会话（也解释了早期 store 重定向那次只拿到 `user_id=0` 的 `token`）。
- **对脚本/应用的修正建议**：与其打开 `login.gaijin.net` 整页（可能回跳到商店导致拿不到 wtlive 会话），不如**直接打开 `live.warthunder.com` 页面**，由用户点 Sign In 触发浮窗登录——登录完即处于 wtlive 会话，收割到的 Cookie 才是 `user_id>0` 的真会话（见 `cookie_capture/capture_wt_cookies.js` v4）。

### 12.9 Gaijin gsea SSO 与会话兑换机制（实测，2026-10-09）

> 本节解释"为什么私有 feed 不能靠裸 Cookie 复刻"，是 §11 / §12.3 / §12.4 修正结论的根因。

#### 12.9.1 gsea 流程观察到的现象

- 页面加载后，Gaijin gsea 客户端会自动请求：
  `GET https://login.gaijin.net/api/gsea/fetch?j=<JWT>&q=<return_url>&$reqID=...&origin=https://live.warthunder.com`
  响应为 `window.postMessage({"gsea":true,"env":"gaijin","status":"OK","payload":{"status":"ok","secondary":true,"uname":"<昵称>","uid":"<用户ID>","jwt":"<长JWT>"}})`。
- 该流程在 **`.login.gaijin.net`** 写入身份 Cookie：`identity_sid` / `identity_id` / `identity_token` / `uuid` / `slc`（实测 headless 注入 `identity_sid` 后可见这些被补写）。
- `main.js` 监听该 `postMessage`，**消费 JWT 完成 `live.warthunder.com` 的站点会话兑换**——这一步是私有 feed 放行的前置条件。

#### 12.9.2 关键实测：裸 Cookie / JWT 复刻均 404

在 headless Chromium 中注入真实导出 Cookie（`token` + `identity_sid` 等全部 `live.warthunder.com` / `.warthunder.com` Cookie），并：

1. 确认页面 `live_wt.user_id = 120138327`（**已识别为登录态**）；
2. 用页面内 `fetch`（`credentials:'same-origin'`）调用各端点（注意：此处 `get_user` 传的是 **`user=0`（无效）**，故 404 属参数错误，详见 §3.5——`get_user` 给定有效作者 id 时匿名即 200；下方仅保留真正的私人 feed 作对照）：

| 端点 | 仅 Cookie | Cookie + gsea Bearer JWT |
|---|---|---|
| `get_regular`（对照，公开） | 200，list=25 | 200，list=25 |
| `get_subscribes_users`（私人） | **404** | **404** |
| `get_hidden`（私人） | **404** | **404** |

- 对照组 `get_regular` 正常 200，排除"Cookie 没带上 / 网络不通"；私人 feed 的差异**只在端点权限**。
- 即便补上 gsea `Bearer <jwt>`，私人端点仍 404——说明 **鉴权不靠 `Authorization` 头，而靠 `main.js` 兑换后建立的站点会话上下文**。
- headless 下 `main.js` 因 jQuery（CDN）资源超时未能完整执行 → gsea→wtlive 兑换没跑成 → 私人 feed 404。这反向证明：**兑换步骤是绕不开的**。

#### 12.9.3 结论与落地

- **私有 feed 鉴权模型**：`live.warthunder.com` 私有接口**拒绝"裸 Cookie 复刻"与"Bearer JWT"**，只认由浏览器完整 SSO（gsea 客户端 + `main.js` 兑换）建立的会话上下文。
- **因此**：应用必须让请求"发生在已登录的 wtlive 页面上下文内"——要么 WebView2 内 `ExecuteScriptAsync` 跑 `fetch`（§12.4），要么 WebView2 网络拦截直接抓页面发出的响应。纯 `HttpClient` + Cookie 方案对私有 feed **不可行**（已证伪）。
- **公开 feed**（§3 `get_regular`）不受影响，仍匿名可用。
- **验证边界（诚实说明）**：本 headless 环境因 `main.js` 依赖（jQuery CDN）超时未能完整执行，gsea→wtlive 兑换没跑成，**未能在本环境中正向观测到私有 feed 返回 200**。上述"WebView2 页面内 `fetch`/拦截"方案是从失败模式（裸 Cookie/JWT 均 404、只有浏览器完整 SSO 才放行）反推出的唯一可行路径；**建议应用时用 WebView2 登录后实测一次 `get_subscribes_users` 是否返回 200 数据**，以闭环确认。

---

## 13. 下载端点（已确认匿名可用）

> 以下端点**匿名可用**，无需登录（前期调查记录 + 实测确认，已统一并入本节与 §15）。

### 13.1 帖子详情（获取下载直链）

```
POST https://live.warthunder.com/api/posts/get/
Content-Type: application/x-www-form-urlencoded; charset=UTF-8
X-Requested-With: XMLHttpRequest
User-Agent: <浏览器 UA，必须>
Referer: https://live.warthunder.com/post/<lang_group>/en/

lang_group=<帖子id>&language=en
```

- `lang_group` = 帖子定位 id（即 `get_regular` 列表里的 `lang_group`）；`language` = `en`/`zh`…
- **必须**带 `User-Agent` 与 `X-Requested-With`，否则响应 `{"status":"ERR"}`。
- **匿名可正常返回**；HTTP 恒为 200，成败以响应体 `status=="ERR"` 判定（参数错误也返回 200）。
- 关键返回字段：`status`(OK/ERR)、`type`(camouflage)、`file.name`、`file.link`(`/dl/<hash>/`)、`file.size`、`images[].orig.src`（预览原图）、
  `author.nickname`（作者昵称）、`downloads` / `views` / `likes`（统计）。
- `description` 是**含 HTML 标签的正文**（`<p>` / `<br>` / `#话题` 锚点），要自行剥成纯文本；标签的识别方式见 §3.6.1。
- `gameItemApproved`：是否「游戏内上传的成品涂装」。此类帖子的字段形态可能与模板包不同，遇到时**优先引导浏览器手动下载**更稳。
- `file == null` 的帖子（作者用外部网盘 mega / Google Drive 等）无法程序内下载，只能引导浏览器手动下。

### 13.2 附件下载（直链）

```
GET https://live.warthunder.com/dl/<hash>/
Referer: https://live.warthunder.com/
User-Agent: <浏览器 UA>
```

- `<hash>` 与帖子 id 无关，是附件自身哈希（`file.link` 中那段）。
- 返回 `application/octet-stream`，**无需登录**；附件是 zip（涂装模板包），可直接解压。
- 下载同样建议做 zip 条目路径校验（来源安全检查）。

### 13.3 与第 6 节的关系

- 列表 `get_regular` 已内嵌 `file.link`，与上述直链一致；无需额外请求即可拿到下载地址。
- `main.js` 中还存在 `get_post`（返回 HTML lightbox 片段），与本文档的 `get`（返回 JSON）并存；**下载流程建议用 JSON 版 `get`**，字段稳定、便于解析。

---

## 14. 范围决策（2026-10-09）：**放弃用户登录与订阅展示功能**

> **明确结论**：`WarThunderSkinManager` 的"应用内涂装浏览器"**不实现用户登录模块**，也**不展示"订阅作者的作品 / 个人内容流 / 隐藏内容"**。本决策为产品范围的最终裁定，后续开发以本节为准。

### 14.1 决策依据

- **技术上不可轻量实现**：订阅/个人等私有 feed 的鉴权依赖 Gaijin gsea SSO 客户端兑换（§12.9），**纯 `HttpClient` + Cookie 复刻已被实测证伪（均 404）**；唯一可行路径是引入 **WebView2** 并让请求发生在已登录页面上下文内（§12.4）。
- **成本与风险不匹配**：引入 WebView2 + 登录态管理会显著增加复杂度，且涉及用户账号凭证安全、会话维护、2FA 处理（§12.5）等长期负担，与"轻量涂装浏览 / 按载具筛选 / 下载"的工具定位不符。
- **公开能力已足够覆盖核心场景**：涂装浏览、按载具筛选、按作者浏览、预览、下载直链均为**匿名可用**（§3、§6、§14.2），无需登录即可服务绝大多数用户。

### 14.2 最终产品范围（仅公开能力）

| 能力 | 是否纳入 | 说明 |
|---|---|---|
| 涂装列表浏览（`get_regular`） | ✅ 纳入 | 匿名 `HttpClient`，无需登录 |
| 按载具筛选（`vehicle=<裸 id>`） | ✅ 纳入 | 同列表，匿名 |
| 预览图 / 详情 | ✅ 纳入 | `images.src` / 帖子详情，匿名 |
| 下载直链（`file.link`） | ✅ 纳入 | 列表内嵌，匿名可取（见 §13.2） |
| **用户登录** | ❌ **放弃** | 不提供登录入口、不维护会话 |
| **按作者浏览**（`get_user`，指定作者公开主页） | ✅ 纳入 | 匿名可用，URL `/user/<id>/` 取 id，见 §3.5 |
| **订阅作者作品流**（`get_subscribes_users`，你关注的） | ❌ **放弃** | 私有 feed，需 SSO，不在范围 |
| **隐藏内容**（`get_hidden`，你的） | ❌ **放弃** | 私有 feed，需 SSO，不在范围 |

### 14.3 对 §11 / §12 的定位说明

- 第 11、12 节（订阅页考察、登录流程、gsea 机制）作为**技术调研存档**保留，用于将来若需扩展"我的订阅"时参考；**不作为当前产品的实现范围**。
- 若未来业务确需"我的订阅"，再按 §12.4 + §12.9 的 WebView2 方案立项，届时重新评估登录模块。

## 15. 下载流程（本程序实现口径）

> 本节是**应用侧**的实现口径（原独立文档 `WTLive下载集成.md` 的 §7，已并入本文）。端点见 §13，
> 包名规则见 `软件功能设计.md` §3.1。

### 15.1 一次下载 = 压缩包 + 预览图

- **预览图是下载的一部分**：压缩包与预览原图（`images[0].orig.src`）**并行**下载；
- **进度 = 压缩包 80% + 预览图 20%**（`WTLiveService.CombinedProgress`；无预览图时压缩包即全部），
  任一路有进展都刷新同一根进度条；压缩包下完而预览图未完时状态显示「下载预览图 x%」；
- **两者都下载完成才进入安装流程**（扫描 → 预览 → 解构 → 导入）。任一失败即本次下载失败——
  预览图失败会给出单独文案（压缩包已下好但未安装），点列表右侧「重试」重新下载；
- **建议包名**：帖子显示名（没有就用压缩包名去扩展名）+ **包内嵌套文件夹段**，
  与「导入压缩包」完全相同（见 `软件功能设计.md` §3.1）——同一个压缩包里的多个涂装包因此可区分
  （`Skin/ver1/type1/car1.blk` ⇒ `Skin.ver1.type1`）。

### 15.2 不静默卡死

| 机制 | 值 | 说明 |
|---|---|---|
| 自动重试 | **5 次**（含首次） | `WTLiveService.DownloadAttempts` |
| 重试间隔 | **固定 1 秒**（**不退避**） | `RetryDelay`；5 次总等待 ≈ 4 秒，节奏可预期 |
| 读取停滞超时 | **30 秒无字节即掐断本次尝试** | `StallTimeout`；由**每次尝试独立**的计时器实现，收到字节即重置 |
| 重试可见 | 状态文字显示「第 N/5 次尝试…」 | `onAttempt` 回调 → 界面，重试不静默 |
| 用户掐断 | 列表右侧**常驻「重试」图标按钮**（`F021`） | 下载中也能点 = 取消本轮 → 等收尾 → 整条重跑（压缩包与预览图都重下） |

- 下载客户端**禁用总超时**（`HttpClient.Timeout = InfiniteTimeSpan`）：大文件在慢网下必然超时被掐断，
  「卡死」由上面的**停滞保护**兜底，而不是总超时；
- 取消语义：程序退出取消 → 「已取消」；用户点「重试」掐断 → 同样即时反馈，随即由重跑覆盖状态；
- 暂存产物（zip / 解压目录 / 预览图）只在**本轮自己**的收尾里清理，且**等两路任务都收尾**再删
  （任一路失败时另一路可能仍在写盘），不做整区清扫——多个下载可以并行。

### 15.3 列表右侧的两个按钮（重试 / 取消）

两个按钮都是**紧凑图标按钮**（`IconButton`，24×24，悬停为主题表面色圆角底），在下载列表每一行右侧、**重试在左、取消/移除在右**：

| 状态 | 重试（`F021`） | 取消 / 移除（`F00D`） |
|---|---|---|
| 下载中 / 导入中 | 可见（掐断重跑） | **取消**：条目**留在列表**、提示「已取消」、**进度条清零**，可用重试重新开始 |
| 已取消 | 可见（重新下载） | **从列表移除**（已取消状态下再按一次即此语义） |
| 失败 | 可见（重新下载） | 从列表移除 |
| 已完成 | 不可见（没有可重试的下载） | 从列表移除 |

- 状态由 `WtLiveDownloadItem.IsCancelable` / `CanRetry` 决定（`WtLiveDownloadState` 增加 `Canceled`）；
  取消 / 移除都走 `SkinsViewModel.CancelOrRemoveWtLiveDownload`（移除前先等本轮收尾，避免留孤儿暂存）；
- 有活动任务时「下载列表」按钮的图标变主题主色（见 `界面设计规范.md` §2.6）；
- **仅图标的按钮必须给 `ToolTip`**，且长提示会在气泡内**自动换行**（主题里的 ToolTip 样式带
  `ContentTemplate` + `TextWrapping`——默认字符串内容不换行，会横向溢出气泡）。

---

> 注：本报告基于 2026-10-09 抓取的站点 `main.js`（版本 `6d10936f52464bdb1038961bf56bc10f`）与实时接口响应。若站点改版，端点/参数可能变化，建议以 `main.js` 中 `l`/`c` 映射与实际响应为准复核。
