using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 图片解码（卡片缩略图 / 详情原图 / 作者头像共用）：
/// <c>OnLoad + Freeze</c> —— 不占文件句柄、可跨线程传递。
/// <para>
/// **只解到调用方需要的宽度**：位图内存 ≈ 解码宽 × 高 × 4 字节，按原图尺寸解会把内存放大几倍
/// （卡片 240 DIP 宽却解 1500px 的原图，白白多占十几 MB）。
/// </para>
/// </summary>
internal static class WtLiveImages
{
    /// <summary>按 <paramref name="decodeWidth"/> 解码；<c>0</c> = 按原尺寸。</summary>
    public static ImageSource Decode(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.None;
        bitmap.EndInit();
        bitmap.Freeze();

        return bitmap;
    }
}
