using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **全局"处理中"指示**：界面侧 = 主窗口上的遮罩 + 旋转加载圈 + 文案（§2.6）。
/// </summary>
/// <remarks>
/// 用于"点了按钮但几秒内看不到任何变化"的长操作：复制 / 删除 / 导入 / 重建资源库。
/// 这些操作的耗时部分已经在后台线程（界面不冻结），但**用户侧没有任何反馈** →
/// 容易被当成"没反应"而连点、或在操作进行中切页面/退出。遮罩同时挡住交互，从根上避免重复触发。
/// <para>
/// 用法：<c>using var _ = BusyIndicator.Instance.Begin(Loc["busy.duplicate"]);</c>
/// —— 可嵌套（内部计数）；文案取**最外层**那次（外层才是用户理解的"这次操作"），
/// 计数归零才隐藏遮罩。调用与释放都在 UI 线程上（异步流程 <c>await</c> 后会回到 UI 线程）。
/// </para>
/// <para>
/// 遮罩出现/消失**不做延时**：这些操作本来就要 1 秒以上，立即可见比"防闪烁"更重要。
/// </para>
/// </remarks>
public sealed class BusyIndicator : INotifyPropertyChanged
{
    /// <summary>全局唯一实例（界面绑定 <c>MainViewModel.Busy</c>，各模块直接 <c>Begin</c>）。</summary>
    public static BusyIndicator Instance { get; } = new();

    private readonly object _gate = new();
    private int _count;
    private string _text = "";

    private BusyIndicator() { }

    /// <summary>是否有进行中的操作（遮罩可见性绑定它）。</summary>
    public bool IsBusy => _count > 0;

    /// <summary>当前显示的文案（最外层那次 <see cref="Begin"/> 传进来的）。</summary>
    public string Text => _text;

    /// <summary>
    /// 进入"处理中"状态；<c>Dispose</c> 时退出。可嵌套，成对使用（<c>using var</c> 最省心）。
    /// </summary>
    public IDisposable Begin(string text)
    {
        lock (_gate)
        {
            if (_count == 0) _text = text;
            _count++;
        }

        Raise(nameof(IsBusy));
        Raise(nameof(Text));

        return new Scope(this);
    }

    private void End()
    {
        lock (_gate)
        {
            if (_count == 0) return; // 防御：重复 Dispose / 未 Begin 就释放
            _count--;
        }

        Raise(nameof(IsBusy));
        Raise(nameof(Text));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>一次"处理中"的范围（<c>Dispose</c> 只生效一次）。</summary>
    private sealed class Scope(BusyIndicator owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;

            _done = true;
            owner.End();
        }
    }
}
