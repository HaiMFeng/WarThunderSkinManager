using System.Collections.Concurrent;
using System.IO;
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
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Inflight =
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

        var task = Inflight.GetOrAdd(id, key => LoadAsync(key));
        _ = task.ContinueWith(t =>
        {
            // 完成后才摘牌：**失败也要摘**（网络抖动时用户再滚回来还能重试），
            // 摘晚了没关系，摘早了会让同一 id 反复下载
            Inflight.TryRemove(id, out _);

            var ok = t.Status == TaskStatus.RanToCompletion;
            Apply(vehicle, ok ? t.Result : null, ok && t.Result == null);
        }, TaskScheduler.Default);
    }

    private static async Task<ImageSource?> LoadAsync(string id)
    {
        var cached = TryLoadFromDisk(id);
        if (cached != null)
        {
            Memory[id] = cached;
            return cached;
        }

        if (IsMissingMarkerFresh(id))
        {
            NoIcon[id] = 0;
            Memory[id] = null;
            return null;
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await Http.GetAsync(UrlFor(id)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SaveMissingMarker(id);
                NoIcon[id] = 0;
                Memory[id] = null;
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (!LooksLikePng(bytes))
            {
                // 200 但不是 PNG（错误页 / 劫持页）：按"没有图标"处理，别把垃圾写进缓存
                SaveMissingMarker(id);
                NoIcon[id] = 0;
                Memory[id] = null;
                return null;
            }

            TrySaveToDisk(id, bytes);

            var image = Decode(bytes);
            Memory[id] = image;
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // 网络抖动：不写负缓存，下次滚到重试
            return null;
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

        try
        {
            var path = FilePathFor(id);
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            var image = Decode(bytes);
            if (image != null) return image;

            // 缓存文件坏了（半截写入 / 手工改过）→ 删掉重下
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
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
