using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **全局"处理中"指示**：界面侧 = 主窗口上的遮罩 + 旋转加载圈 + 文案（§2.6），
/// 由 <see cref="Views.BusyOverlay"/> 承载；按需附带副文案、进度条与取消入口。
/// </summary>
/// <remarks>
/// 用于"点了按钮但几秒内看不到任何变化"的长操作：复制 / 删除 / 导入 / 迁移 / 重建资源库。
/// 这些操作的耗时部分已经在后台线程（界面不冻结），但**用户侧没有任何反馈** →
/// 容易被当成"没反应"而连点、或在操作进行中切页面/退出。遮罩同时挡住交互，从根上避免重复触发。
/// <para>
/// 用法：<c>using var scope = BusyIndicator.Instance.Begin(Loc["busy.duplicate"]);</c>
/// —— 可嵌套（内部计数）；文案取**最外层**那次（外层才是用户理解的"这次操作"），
/// 计数归零才隐藏遮罩。调用与释放都在 UI 线程上（异步流程 <c>await</c> 后会回到 UI 线程）。
/// </para>
/// <para>
/// 副文案 / 进度 / 取消**都由最外层作用域决定**（内层的设置被忽略）：
/// 嵌套时若允许内层改这些，进度条与副文案会随内层起落跳变。
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
    private string _detail = "";
    private bool _barVisible;
    private double _progress;
    private string _progressText = "";
    private bool _canCancel;
    private bool _cancelRequested;
    private Action? _onCancel;

    private BusyIndicator() { }

    /// <summary>是否有进行中的操作（遮罩可见性绑定它）。</summary>
    public bool IsBusy => _count > 0;

    /// <summary>当前显示的文案（最外层那次 <see cref="Begin(string)"/> 传进来的）。</summary>
    public string Text => _text;

    /// <summary>副文案：正在处理的具体条目（如"正在解构 xxx.zip"）；为空时界面折叠该行。</summary>
    public string Detail => _detail;

    /// <summary>是否显示进度条（无总量时保持 false → 只有加载圈，即"不确定态"）。</summary>
    public bool BarVisible => _barVisible;

    /// <summary>进度条取值，<c>0..1</c>（仅 <see cref="BarVisible"/> 为 true 时有意义）。</summary>
    public double Progress => _progress;

    /// <summary>进度文案（<c>42%</c> / <c>3 / 8</c> / 自定义如 <c>12.3 MB / 4.5 GB</c>），无进度时为空。</summary>
    public string ProgressText => _progressText;

    /// <summary>是否显示「取消」按钮（<see cref="Begin(string, Action)"/> 传了回调才为 true）。</summary>
    public bool CanCancel => _canCancel;

    /// <summary>取消是否已被请求（界面据此把按钮置灰：请求已投递，等流程自己收尾）。</summary>
    public bool CancelRequested => _cancelRequested;

    /// <summary>进入"处理中"状态；<c>Dispose</c> 时退出。可嵌套，成对使用（<c>using var</c> 最省心）。</summary>
    public Scope Begin(string text) => Begin(text, null);

    /// <summary>
    /// 进入"处理中"状态，并可选地提供取消入口。
    /// </summary>
    /// <param name="text">显示文案（嵌套时取最外层）。</param>
    /// <param name="onCancel">
    /// 非空 → 遮罩上出现「取消」按钮，点击时执行它。**取消入口必须始终可点**：
    /// 全屏遮罩只应在"取消入口就在遮罩上"时才用于长任务（实测教训见
    /// <c>docs\界面设计规范.md</c> §2.6 与 <c>MainViewModel</c> 的应用更新下载注释）。
    /// 回调应只做"置位取消令牌"这类轻量动作 —— 它在 UI 线程上执行。
    /// </param>
    public Scope Begin(string text, Action? onCancel)
    {
        bool isOuter;
        lock (_gate)
        {
            // 最外层那次决定文案与进度/取消（内层只加计数，不改观感）
            isOuter = _count == 0;
            if (isOuter)
            {
                _text = text;
                _detail = "";
                _barVisible = false;
                _progress = 0;
                _progressText = "";
                _canCancel = onCancel != null;
                _cancelRequested = false;
                _onCancel = onCancel;
            }

            _count++;
        }

        RaiseAll();

        return new Scope(this, isOuter);
    }

    /// <summary>
    /// 点击遮罩上的「取消」：把请求转给最外层那次操作。
    /// 重复点击无副作用（第二次起直接返回）；按钮仍保持可见但置灰，由调用方决定后续反馈。
    /// </summary>
    public void RequestCancel()
    {
        Action? action;
        lock (_gate)
        {
            if (_cancelRequested) return; // 已请求过：别把取消请求重复投给同一流程
            _cancelRequested = true;
            action = _onCancel;
            _onCancel = null;
        }

        Raise(nameof(CancelRequested));
        action?.Invoke();
    }

    private void SetProgress(bool visible, double progress, string progressText)
    {
        lock (_gate)
        {
            _barVisible = visible;
            _progress = progress;
            _progressText = progressText;
        }

        Raise(nameof(BarVisible));
        Raise(nameof(Progress));
        Raise(nameof(ProgressText));
    }

    private void SetDetail(string detail)
    {
        lock (_gate)
        {
            _detail = detail;
        }

        Raise(nameof(Detail));
    }

    private void End()
    {
        bool cleared;
        lock (_gate)
        {
            if (_count == 0) return; // 防御：重复 Dispose / 未 Begin 就释放
            _count--;
            cleared = _count == 0;
            if (cleared)
            {
                _text = "";
                _detail = "";
                _barVisible = false;
                _progress = 0;
                _progressText = "";
                _canCancel = false;
                _cancelRequested = false;
                _onCancel = null;
            }
        }

        RaiseAll();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseAll()
    {
        Raise(nameof(IsBusy));
        Raise(nameof(Text));
        Raise(nameof(Detail));
        Raise(nameof(BarVisible));
        Raise(nameof(Progress));
        Raise(nameof(ProgressText));
        Raise(nameof(CanCancel));
        Raise(nameof(CancelRequested));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>一次"处理中"的范围（<c>Dispose</c> 只生效一次）。</summary>
    public sealed class Scope(BusyIndicator owner, bool isOuter) : IDisposable
    {
        private bool _done;

        /// <summary>
        /// 报告确定态进度（<c>0..1</c>）。<paramref name="text"/> 为空时文案自动取百分比（<c>42%</c>），
        /// 非空则原样显示（如按字节的 <c>12.3 MB / 4.5 GB</c>）。
        /// 仅**最外层**作用域生效；越界值夹到 <c>0..1</c>。
        /// </summary>
        public void Report(double fraction, string? text = null)
        {
            if (!isOuter) return;

            var clamped = Math.Clamp(fraction, 0, 1);
            owner.SetProgress(true, clamped,
                string.IsNullOrEmpty(text) ? $"{Math.Round(clamped * 100)}%" : text);
        }

        /// <summary>
        /// 报告确定态进度（按计数，文案显示为 <c>done / total</c>）。
        /// 仅**最外层**作用域生效；<paramref name="total"/> ≤ 0 时退化为不定态。
        /// </summary>
        public void Report(int done, int total)
        {
            if (!isOuter) return;
            if (total <= 0)
            {
                owner.SetProgress(false, 0, "");
                return;
            }

            var clamped = Math.Clamp((double)done / total, 0, 1);
            owner.SetProgress(true, clamped, $"{done} / {total}");
        }

        /// <summary>回到不定态（只转圈、不显示进度条）。仅**最外层**作用域生效。</summary>
        public void HideBar()
        {
            if (!isOuter) return;
            owner.SetProgress(false, 0, "");
        }

        /// <summary>设置副文案（正在处理的具体条目），传空串则界面折叠该行。仅**最外层**作用域生效。</summary>
        public void SetDetail(string detail)
        {
            if (!isOuter) return;
            owner.SetDetail(detail ?? "");
        }

        public void Dispose()
        {
            if (_done) return;

            _done = true;
            owner.End();
        }
    }
}
