# WT Live（live.warthunder.com）解析说明书

> **性质**：对官方涂装分享站 [live.warthunder.com](https://live.warthunder.com)（WT Live）
> 的接口调查记录，**与本项目代码无关**——本文只描述站点行为与解析方法，供任何需要
> 程序化访问该站的工具参考。
>
> 调查时间：2025 年前后；站点无官方 API 且可能改版，字段以实测为准。

## 1. 站点概况

站点是 **JS 渲染的 SPA**：直接 GET 帖子页拿到的静态 HTML 里**没有帖子数据**，数据由前端
调同域 JSON 接口获取。接口**匿名可用**（无需登录 / Cookie），但站点前置 Cloudflare。

帖子 URL 形如 `https://live.warthunder.com/post/<帖子id>/en/`，`<帖子id>` 是各接口的定位参数。

## 2. 帖子详情接口

```
POST https://live.warthunder.com/api/posts/get/
Content-Type: application/x-www-form-urlencoded; charset=UTF-8
X-Requested-With: XMLHttpRequest
User-Agent: <浏览器 UA，必须>
Referer: https://live.warthunder.com/post/<帖子id>/en/

lang_group=<帖子id>&language=en
```

要点：

- `lang_group` = 帖子 id；`language` = 内容语言（`en` / `zh` 等）；
- **必须**带浏览器 `User-Agent` 与 `X-Requested-With: XMLHttpRequest`，否则返回 `{"status":"ERR"}`；
- 匿名（无 Cookie）请求可正常返回；
- 参数错误时 **HTTP 仍是 200**，失败要靠响应体 `status == "ERR"` 判定。

## 3. 响应字段

| 字段 | 样例 | 说明 |
|---|---|---|
| `status` | `"OK"` / `"ERR"` | 成败判定（HTTP 状态码不可靠） |
| `type` | `"camouflage"` | 帖子类型：camouflage（涂装）/ sights（瞄具）/ missions（任务）等 |
| `author.nickname` | `"锅盖头领域大神"` | 作者昵称 |
| `description` | HTML | 帖子正文：含 `<p>` / `<br>` / `#话题` 链接等标签，需自行剥离为纯文本 |
| `images[].orig.src` | `https://cdn-live.warthunder.com/uploads/.../<原图>.png` | 预览图列表，`orig` 为原图尺寸 |
| `file.name` | `"template_cn_hq_11.zip"` | 附件原始文件名（涂装模板包常带**载具前缀**，去扩展名可作显示名来源） |
| `file.link` | `https://live.warthunder.com/dl/<hash>/` | **站内下载直链**（匿名可下） |
| `file.size` | `4930419` | 附件字节数（可作下载进度基准；若响应头有 `Content-Length` 优先用响应头） |
| `downloads` / `views` / `likes` | 6 / 30 / 3 | 统计数据 |
| `gameItemApproved` | bool | 是否「游戏内上传的成品涂装」；此类帖子字段形态可能与模板包不同，解析时需单独验证 |

`file == null` 的帖子（部分作者用外部网盘 mega / Google Drive 等）→ 无法程序内下载，只能引导用户到浏览器页面手动下载。

## 4. 附件下载接口

```
GET https://live.warthunder.com/dl/<hash>/
Referer: https://live.warthunder.com/
User-Agent: <浏览器 UA>
```

- `file.link` 中的 `<hash>` 与帖子 id 无关，是附件自身的哈希；
- 返回 `application/octet-stream`，**无需登录**；
- 附件是 zip（涂装模板包），可直接交给常规解压流程。

## 5. 解析建议

1. **成败判定**：先看响应体 `status`，不看 HTTP 状态码；
2. **正文剥标签**：`description` 是 HTML 片段，按 `</p>` / `<br>` 转换行、去其余标签、
   解 HTML 实体后得到纯文本；正文首行常是宣传语，**不建议**用正文首行做文件命名；
3. **类型过滤**：只处理 `type == "camouflage"`，其余类型（瞄具 / 任务等）明确拒绝；
4. **显示名来源**：优先 `file.name` 去扩展名（带载具前缀时与游戏 id 命名习惯一致），
   其次帖子标题，正文仅作展示；
5. **请求纪律**：单次交互单请求即可满足浏览级使用，无需轮询；不建议携带用户 Cookie
   （隐私 + 规避站点风控）。

## 6. 风险与边界

- **接口无文档且可能变更**：站点改版会导致解析失效，调用方应有清晰的失败提示路径，
  且失败不影响工具的其他功能；
- **Cloudflare**：普通带 UA 的请求可过；若触发挑战页（返回 HTML 而非 JSON），应提示用户
  而非重试轰炸；
- **成品涂装帖**（`gameItemApproved`）字段形态未完全验证，遇到时提示回退浏览器下载更稳；
- 下载文件同样应做**来源安全检查**（zip 条目路径校验等），不能因为是官方站就放松。
