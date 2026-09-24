using System.IO;
using System.Text;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 关键小文件（meta.json / config.json / 排除清单等）的**原子写入**：
/// 先写同目录 <c>.tmp</c>，再 Move 覆盖——断电 / 进程被杀不会留下半截 JSON
/// （.tmp 残留由 blob 回收一并清理，见 BlobGc）。
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
        File.WriteAllText(tmp, contents, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
