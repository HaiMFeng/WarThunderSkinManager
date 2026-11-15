using System.IO;
using System.Text;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 关键小文件（meta.json / config.json / 排除清单 / 游戏输出 blk 等）的**原子写入**：
/// 先写同目录 <c>.tmp</c>，再 Move 覆盖——断电 / 进程被杀不会留下半截 JSON。
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);

        // 临时名带 Guid：同一文件被不同线程并发写时不会共用一个 .tmp（否则后写者的 Move 可能把别人的内容顶上目标）
        var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tmp, contents, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // 失败（占用 / 只读 / 磁盘满）也不留 Guid 临时文件——否则每次重试都会新增一个、无界累积
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        }
    }

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        }
    }
}
