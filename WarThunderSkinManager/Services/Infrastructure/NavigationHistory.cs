using System;
using System.Collections.Generic;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **浏览器式**的前进 / 后退栈：一条线性条目表 + 一个游标。
/// 后退 / 前进只是移动游标（不搬元素）；在中间位置产生新条目时，丢弃它之后的所有前进项。
/// </summary>
/// <remarks>
/// <para>
/// 选型依据：WPF 自带的 <c>Frame.NavigationService</c> 日志绑定 Page / URI，适合"整页切换"；
/// ReactiveUI 的 <c>RoutingState</c> 与 Prism 的 <c>NavigationJournal</c> 也是同一套"栈 + 游标"模型，
/// 但都以"视图模型实例"为条目、需要引入框架。本项目是单页内换筛选条件（同一视图模型、同一可视树），
/// 条目应当是**可还原的状态快照**，因此这里只取那套成熟模型的最小实现，不新增依赖。
/// </para>
/// <para>
/// <see cref="Capacity"/> 限制条目数：每个条目可能持有已加载卡片的引用，不设上限会白占内存。
/// </para>
/// </remarks>
public sealed class NavigationHistory<T> where T : class
{
    private readonly List<T> _entries = new();
    private int _index = -1;

    /// <param name="capacity">最多保留多少条（超出时从头丢，游标跟着左移）。</param>
    public NavigationHistory(int capacity = 20)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    /// <summary>最多保留的条目数。</summary>
    public int Capacity { get; }

    /// <summary>当前条目数（含可前进的那些）。</summary>
    public int Count => _entries.Count;

    /// <summary>当前游标（0 起；空历史为 -1）。</summary>
    public int Index => _index;

    /// <summary>当前条目（空历史为 null）。</summary>
    public T? Current => _index >= 0 && _index < _entries.Count ? _entries[_index] : null;

    /// <summary>能否后退。</summary>
    public bool CanGoBack => _index > 0;

    /// <summary>能否前进（游标后面还有条目）。</summary>
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    /// <summary>压入一条新历史并把它设为当前位置；丢弃原有的前进项，超出容量时丢弃最旧的。</summary>
    public void Push(T entry)
    {
        if (_index < _entries.Count - 1)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

        _entries.Add(entry);
        _index = _entries.Count - 1;

        while (_entries.Count > Capacity)
        {
            _entries.RemoveAt(0);
            _index--;
        }

        if (_index < 0) _index = 0; // 容量为 1 时只剩最后一条
    }

    /// <summary>
    /// 原地更新当前条目。用户在"同一页"里继续滚动 / 翻页后，离开前把最新状态写回，
    /// 这样后退回来看到的是离开时的样子，而不是最初进入时的样子。
    /// </summary>
    public void ReplaceCurrent(T entry)
    {
        if (_index >= 0 && _index < _entries.Count) _entries[_index] = entry;
    }

    /// <summary>后退一格并取回该条目；不能后退时返回 false。</summary>
    public bool GoBack(out T entry)
    {
        if (!CanGoBack)
        {
            entry = null!;
            return false;
        }

        _index--;
        entry = _entries[_index];
        return true;
    }

    /// <summary>前进一格并取回该条目；不能前进时返回 false。</summary>
    public bool GoForward(out T entry)
    {
        if (!CanGoForward)
        {
            entry = null!;
            return false;
        }

        _index++;
        entry = _entries[_index];
        return true;
    }

    /// <summary>清空（切页 / 退出时用；当前浏览页一般不需要）。</summary>
    public void Clear()
    {
        _entries.Clear();
        _index = -1;
    }
}
