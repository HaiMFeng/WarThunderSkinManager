# WT Live 下载集成（§3.15）

从官方涂装分享站 [live.warthunder.com](https://live.warthunder.com)（WT Live）一键下载涂装并进入常规导入流程。

## 站点接口

站点本身是 JS 渲染的 SPA，**静态 HTML 无帖子数据**；但帖子详情与附件走同域 JSON 接口，匿名可用（无需登录 / Cookie）。

### 帖子详情

```
POST https://live.warthunder.com/api/posts/get/
Content-Type: application/x-www-form-urlencoded; charset=UTF-8
X-Requested-With: XMLHttpRequest
Referer: https://live.warthunder.com/post/<帖子id>/en/

lang_group=<帖子id>&language=en
```

- `lang_group` = 帖子 id（从页面 URL `/post/<id>/…` 提取）；`language` = 内容语言（`en` / `zh` 等）
- **必须**带浏览器 `User-Agent` 与 `X-Requested-With: XMLHttpRequest`，否则返回 `{"status":"ERR"}`
- 站点有 Cloudflare；实测匿名（无 Cookie）请求可正常返回，无需登录态
- 参数错误时 HTTP 仍 200，**靠响应体 `status == "ERR"` 判定失败**

### 响应字段（与 WTSM 的对应关系）

| 字段 | 样例 | 用途 |
|---|---|---|
| `type` | `"camouflage"` | 非涂装帖（sights / missions 等）直接拒绝 |
| `author.nickname` | `"锅盖头领域大神"` | 归属作者展示 |
| `description` | HTML（`<p>` / `<br>` / `#话题` 链接） | 剥标签 → 纯文本描述；**首个非空行 = 建议显示名**（截断 60 字符） |
| `images[].orig.src` | `https://cdn-live.warthunder.com/uploads/.../<原图>.png` | 首张原图下载后设为涂装包预览图（`PreviewStore.SaveFromFile`） |
| `file.name` | `"template_cn_hq_11.zip"` | 附件原始文件名（模板包常带**载具前缀**） |
| `file.link` | `https://live.warthunder.com/dl/<hash>/` | **站内下载直链**（匿名可下） |
| `file.size` | `4930419` | 下载进度基准（服务端也可能给 `Content-Length`，优先用响应头） |
| `downloads` / `views` / `likes` | 6 / 30 / 3 | 展示 |

`file == null` 的帖子（部分作者用外部网盘）→ 提示用户到浏览器页面下载，再用「导入压缩包」导入。

### 附件下载

```
GET https://live.warthunder.com/dl/<hash>/
Referer: https://live.warthunder.com/
```

直链返回 `application/octet-stream`，**无需登录**；带浏览器 UA。写入暂存区后走常规导入。

## 程序流程（§3.15）

```
「从 WT Live 下载」按钮
  → WTLiveImportWindow：输入链接（范例提示 + 正则校验 live.warthunder.com/post/<id>）
  → 「读取」→ POST api/posts/get → 展示信息（预览图 / 作者 / 文件名 / 大小 / 描述 / 显示名可改）
  → 「开始下载」→ 加入下载列表 → 后台下载（按字节报进度）
  → 下载完成 → 解压到暂存区 → ImportService.Scan → 常规导入流程（预览窗可再改名 → 解构落盘）
  → 导入成功 → 下载首张原图 → PreviewStore 设为该包预览图
  → 下载列表条目标记「已导入」
```

- 下载列表在涂装管理页「一键导入 UserSkins」右侧（图标：向下箭头 + 底部一横，Segoe MDL2 `E896`），
  **悬停 ToolTip** 逐行显示每个下载的状态（下载中 x% / 导入中 / 已导入 / 失败原因）
- 单候选导入时自动把建议包名改为网页解析的显示名（预览窗可再改）；多候选保持扫描结果
- 暂存区（zip + 解压产物 + 预览图临时文件）在导入完成后统一清理

## 实现位置

| 组件 | 位置 |
|---|---|
| 接口 / 下载 / HTML 剥离 | `Services/WTLiveService.cs`（静态 `HttpClient`，浏览器 UA） |
| 下载列表条目 | `ViewModels/WtLiveDownloadItem.cs` |
| 下载管理 + 导入编排 | `ViewModels/SkinsViewModel.cs`（`StartWtLiveDownload` / `ApplyWtLivePreviewAsync`） |
| 网址输入与信息确认窗 | `Views/WTLiveImportWindow.xaml(.cs)` |
| 文案 | `Assets/lang/*.json` 的 `wtlive.*` 键 |

## 边界与风险

- **接口无文档且可能变更**：站点改版会导致解析失效——失败路径统一提示「读取失败」，不影响其他功能
- 外链网盘（mega / Google Drive 等）的帖子无法程序内下载，走引导
- 「游戏内上传的成品涂装」（`gameItemApproved: true`）字段形态可能不同，当前按模板包处理；遇到时提示并回退浏览器下载
- 请求频率：单次交互单请求，无轮询，无频率问题；不携带用户 Cookie（隐私 + 规避封禁）
