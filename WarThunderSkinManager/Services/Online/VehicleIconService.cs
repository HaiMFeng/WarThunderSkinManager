using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 载具列表左侧的小图标：来自官方百科 CDN
/// <c>https://static.encyclopedia.warthunder.com/slots/&lt;id&gt;.png</c>。
/// </summary>
/// <remarks>
/// <para>
/// **文件名规则**（2026-10 实测）：就是 <see cref="Vehicle.Id"/> 原样小写、**连字符保留** ——
/// <c>a-26c</c> 要请求 <c>a-26c.png</c>，把 <c>-</c> 换成 <c>_</c> 会 404（<c>f4f-3</c>、<c>pe-3</c> 同理）。
/// 抽样 60 个真实载具命中 57 个，缺失的只有活动/退役条目。
/// </para>
/// <para>
/// **缓存策略**：内存（已解码、<c>Freeze</c> 后跨线程安全）→ 磁盘（<c>&lt;配置目录&gt;/vehicle-icons</c>）→ 网络。
/// 404 会写一个 <c>&lt;id&gt;.missing</c> 标记做**负缓存**（默认 30 天，过期后自动重试：将来补了图也能拿到）；
/// 网络失败**不写**负缓存，下次滚到还会再试。全部失败只是这一行显示空槽，绝不影响列表与其它功能。
/// </para>
/// <para>
/// **只在需要时请求**：由 <c>Controls/VehicleIconLoader</c> 挂在列表行的 <c>Loaded</c> / <c>DataContextChanged</c> 上，
/// 配合列表虚拟化 —— 没滚到的载具不会发请求。
/// </para>
/// </remarks>
public static class VehicleIconService
{
    /// <summary>官方百科的图标目录（后面直接拼 <c>&lt;id&gt;.png</c>）。</summary>
    public const string BaseUrl = "https://static.encyclopedia.warthunder.com/slots/";

    /// <summary>负缓存标记的后缀（与图片同目录）。</summary>
    public const string MissingSuffix = ".missing";

    /// <summary>负缓存有效期：过期后重新问一次（CDN 后来补了图标就能自动出现）。</summary>
    public static readonly TimeSpan MissingTtl = TimeSpan.FromDays(30);

    /// <summary>同时下载的图标数上限（首屏会一次排进几十个，串行会明显拖慢）。</summary>
    private const int MaxConcurrency = 6;

    /// <summary>解码宽度：槽位 72 DIP 宽，按 200% 缩放取 144 设备像素（再大只是白吃内存）。</summary>
    private const int DecodePixelWidth = 144;

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(MaxConcurrency, MaxConcurrency);

    /// <summary>正在下载的：同一 id 多行（换页 / 重建列表）只发一次请求。</summary>
    private static readonly ConcurrentDictionary<string, Task<IconLoad>> Inflight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已解码的内存缓存（null = 确认没有图标）。</summary>
    private static readonly ConcurrentDictionary<string, ImageSource?> Memory =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>进程内的"没有图标"集合（磁盘负缓存的内存副本，省掉每次 stat 文件）。</summary>
    private static readonly ConcurrentDictionary<string, byte> NoIcon =
        new(StringComparer.OrdinalIgnoreCase);

    private static string _configDir = "";

    /// <summary>注入配置目录（启动时与设置页换目录时调用）；目录变了就丢弃内存缓存。</summary>
    public static void Configure(string? configDir)
    {
        var dir = configDir ?? "";
        if (string.Equals(dir, _configDir, StringComparison.OrdinalIgnoreCase)) return;

        _configDir = dir;
        Memory.Clear();
        NoIcon.Clear();
    }

    /// <summary>缓存目录：<c>&lt;配置目录&gt;/vehicle-icons</c>（未配置时返回空串 = 只用内存）。</summary>
    public static string Directory()
        => _configDir.Length == 0 ? "" : Path.Combine(_configDir, "vehicle-icons");

    /// <summary>某载具的缓存图片路径（可能不存在）。</summary>
    public static string FilePathFor(string id) => Path.Combine(Directory(), Normalize(id) + ".png");

    /// <summary>某载具的负缓存标记路径。</summary>
    public static string MissingPathFor(string id) => Path.Combine(Directory(), Normalize(id) + MissingSuffix);

    /// <summary>某载具的图标 URL（规则见类型注释：原样小写、连字符保留）。</summary>
    public static string UrlFor(string id) => BaseUrl + Normalize(id) + ".png";

    /// <summary>id 归一化：去空白 + 小写（**不做**连字符替换）。</summary>
    public static string Normalize(string? id) => (id ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// 这个状态码是否代表"**文件确实不存在**"——只有它才写 30 天负缓存。
    /// 5xx / 429 / 403 是线路或限流问题，当成"没有图标"会把这一行锁死（§3.18）。
    /// </summary>
    public static bool IsDefinitelyMissing(HttpStatusCode status)
        => status is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>取字节开头的 PNG 魔数（<c>89 50 4E 47</c>）；CDN 的 404 是 XML 错误页，光看 Content-Type 不够稳。</summary>
    public static bool LooksLikePng(byte[]? bytes)
        => bytes is { Length: >= 8 }
           && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    /// <summary>
    /// 为一行载具补图标（幂等：已有结果或正在下载时直接返回）。
    /// 必须在 UI 线程调用（要写 <see cref="Vehicle"/> 的绑定属性），下载与解码在后台。
    /// </summary>
    public static void Request(Vehicle? vehicle)
    {
        if (vehicle == null) return;

        var id = Normalize(vehicle.Id);
        if (id.Length == 0)
        {
            Apply(vehicle, null, missing: true);
            return;
        }

        // 回收复用的行：已经拿到结果就别重复排
        if (vehicle.Icon != null || vehicle.IconMissing) return;

        if (Memory.TryGetValue(id, out var cached))
        {
            Apply(vehicle, cached, cached == null);
            return;
        }

        if (NoIcon.ContainsKey(id))
        {
            Apply(vehicle, null, missing: true);
            return;
        }

        // **Task.Run**：磁盘命中时的"读文件 + 解码"不能压在这次调用（UI 线程）上
        var task = Inflight.GetOrAdd(id, key => Task.Run<IconLoad>(() => LoadAsync(key)));
        _ = task.ContinueWith((Task<IconLoad> t) =>
        {
            // 只有"还是自己这一轮"才摘牌：失败后下一轮会重新登记，按 key 盲删会把**新**任务摘掉。
            // ConcurrentDictionary 没有公开的 TryRemove(KeyValuePair) 重载，原子"按值删除"走 ICollection.Remove
            _ = ((ICollection<KeyValuePair<string, Task<IconLoad>>>)Inflight)
                .Remove(new KeyValuePair<string, Task<IconLoad>>(id, t));

            // 任务若 Faulted，异常没人读会被 UnobservedTaskException 记进崩溃日志（噪音）→ 这里读掉
            if (t.IsFaulted) _ = t.Exception;

            var ok = t.Status == TaskStatus.RanToCompletion;
            Apply(vehicle, ok ? t.Result.Image : null, ok && t.Result.Missing);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 一次加载的结论。<see cref="Missing"/> 只在"确认没有这张图"时置位（404 / 非 PNG）；
    /// **网络失败是 <c>(null, false)</c>** —— 不能把抖动当成"该载具没有图标"，否则这一行会锁死到下次启动。
    /// </summary>
    private readonly record struct IconLoad(ImageSource? Image, bool Missing);

    private static async Task<IconLoad> LoadAsync(string id)
    {
        var cached = TryLoadFromDisk(id);
        if (cached != null)
        {
            Memory[id] = cached;
            return new IconLoad(cached, false);
        }

        if (IsMissingMarkerFresh(id))
        {
            NoIcon[id] = 0;
            Memory[id] = null;
            return new IconLoad(null, true);
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await Http.GetAsync(UrlFor(id)).ConfigureAwait(false);

            // 只有"这个文件确实存在不了"才写负缓存：5xx / 429 / 403 是线路或限流问题，
            // 记成"没有图标"会把这一行锁死 30 天，也违背"网络失败下次再试"的口径
            if (IsDefinitelyMissing(response.StatusCode))
            {
                SaveMissingMarker(id);
                NoIcon[id] = 0;
                Memory[id] = null;
                return new IconLoad(null, true);
            }

            if (!response.IsSuccessStatusCode) return new IconLoad(null, false);

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            // **先解码、成功才落盘**：坏字节进了缓存，下次从磁盘读还会坏一遍（且删都删不掉）
            var image = LooksLikePng(bytes) ? Decode(bytes) : null;
            if (image == null)
            {
                // 200 但不是有效 PNG：强制门户页 / WAF 拦截页 / CDN 错误页都长这样。
                // **按线路问题处理**（不写任何标记）：真没有图 CDN 会给 404，走上面的分支；
                // 这里若记成"没有图标"，一次穿越就能把整列锁 30 天、重启也不恢复
                return new IconLoad(null, false);
            }

            TrySaveToDisk(id, bytes);
            Memory[id] = image;
            return new IconLoad(image, false);
        }
        catch (Exception)
        {
            // 网络抖动 / 超时 / 解析异常：不写负缓存，下次滚到再试（Missing=false 才不会把行锁死）
            return new IconLoad(null, false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ImageSource? TryLoadFromDisk(string id)
    {
        var dir = Directory();
        if (dir.Length == 0) return null;

        var path = FilePathFor(id);

        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            // **读不动不等于文件坏了**（别的线程正在 File.Move 覆盖它 / 权限瞬时问题）：
            // 直接返回，绝不能在这里删文件 —— 否则会把刚写好的好缓存删掉
            return null;
        }

        var image = Decode(bytes);
        if (image != null) return image;

        // 读到字节但解不出来 = 缓存确实坏了（半截写入 / 手工改过）→ 删掉重下，
        // 不删的话每次滚到都会从同一个坏文件失败一遍
        try { File.Delete(path); } catch { /* 删不掉也无所谓，下次写入会覆盖 */ }

        return null;
    }

    private static void TrySaveToDisk(string id, byte[] bytes)
    {
        if (Directory().Length == 0) return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory());
            // 先写临时文件再替换：避免写一半的文件被下次当成有效缓存
            var path = FilePathFor(id);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不下就算了：本次已解码进内存，下次再下
        }
    }

    private static bool IsMissingMarkerFresh(string id)
    {
        if (Directory().Length == 0) return false;

        try
        {
            var path = MissingPathFor(id);
            if (!File.Exists(path)) return false;

            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MissingTtl;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SaveMissingMarker(string id)
    {
        if (Directory().Length == 0) return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory());
            File.WriteAllBytes(MissingPathFor(id), Array.Empty<byte>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 负缓存写不下去最多下次再问一遍
        }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = DecodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.None;
            bitmap.EndInit();
            bitmap.Freeze(); // 冻结后才能跨线程交给绑定

            return bitmap;
        }
        catch (Exception)
        {
            // 任何解码失败（FileFormatException / NotSupportedException / OOM 之外的解析问题）
            // 都只影响这一张图：返回 null，调用方会把它当成"没有图标"并（磁盘路径）删掉坏文件
            return null;
        }
    }

    /// <summary>回填到模型；跨线程时切回 UI 线程（绑定属性只能在 UI 线程改）。</summary>
    private static void Apply(Vehicle vehicle, ImageSource? icon, bool missing)
    {
        var dispatcher = Application.Current?.Dispatcher;

        void Do()
        {
            vehicle.Icon = icon;
            vehicle.IconMissing = missing;
        }

        if (dispatcher == null || dispatcher.CheckAccess()) Do();
        else dispatcher.BeginInvoke(Do);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WarThunderSkinManager/1.0 (+vehicle icon)");
        return client;
    }
}
