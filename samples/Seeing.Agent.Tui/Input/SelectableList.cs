namespace Seeing.Agent.Tui.Input;

/// <summary>
/// 候选「可选择列表」纯逻辑内核（单选高亮游标 / 多选勾选集 + 页窗）。零外部依赖，供内联下拉与模态控件共用。
/// </summary>
public sealed class SelectableList
{
    private readonly List<string> _labels;
    private readonly HashSet<int> _checked = [];
    private readonly int _pageSize;
    private readonly bool _allowPaging;

    public SelectableList(
        IReadOnlyList<string> labels,
        int pageSize,
        bool multiSelect = false,
        bool allowPaging = true)
    {
        _labels = [.. labels ?? []];
        MultiSelect = multiSelect;
        _pageSize = Math.Max(1, pageSize);
        _allowPaging = allowPaging;
    }

    public bool MultiSelect { get; }

    public int Count => _labels.Count;

    public int SelectedIndex { get; private set; }

    public string? SelectedLabel =>
        SelectedIndex >= 0 && SelectedIndex < _labels.Count ? _labels[SelectedIndex] : null;

    public IReadOnlyList<string> Labels => _labels;

    /// <summary>当前页窗首个可见下标。</summary>
    public int PageFirstIndex { get; private set; }

    /// <summary>当前页窗可见下标序列（<c>allowPaging=false</c> 时即全部，且窗不滚动）。</summary>
    public IReadOnlyList<int> PageIndices()
    {
        if (_allowPaging)
            EnsureWindow();

        var first = _allowPaging ? PageFirstIndex : 0;
        var last = _allowPaging ? Math.Min(_labels.Count, first + _pageSize) : _labels.Count;
        var list = new List<int>(Math.Max(0, last - first));
        for (var i = first; i < last; i++)
            list.Add(i);
        return list;
    }

    public void MoveUp() => SetSelected(SelectedIndex - 1);

    public void MoveDown() => SetSelected(SelectedIndex + 1);

    /// <summary>命中/点击置游标；越界或**游标未实际改变**均返回 false（不改态，避免 hover 同项反复重绘）。</summary>
    public bool SetByHit(int index)
    {
        if (index < 0 || index >= _labels.Count || index == SelectedIndex)
            return false;

        SetSelected(index);
        return true;
    }

    public void Toggle(int index)
    {
        if (index < 0 || index >= _labels.Count)
            return;

        if (!_checked.Add(index))
            _checked.Remove(index);

        SetSelected(index);
    }

    public bool IsChecked(int index) => _checked.Contains(index);

    public IReadOnlyList<int> CheckedIndices() => _checked.Where(i => i >= 0 && i < _labels.Count).OrderBy(i => i).ToList();

    /// <summary>集合/候选变化后复位：游标回到 0（或指定值，越界归 0），页窗回顶。</summary>
    public void Reset(int selectedIndex = 0)
    {
        SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, _labels.Count - 1));
        PageFirstIndex = 0;
        EnsureWindow();
    }

    /// <summary>换候选（补全实时过滤）：游标复位 0、页窗回顶，越界的勾选剔除。</summary>
    public void UpdateItems(IReadOnlyList<string> labels)
    {
        _labels.Clear();
        _labels.AddRange(labels ?? []);

        SelectedIndex = 0;
        _checked.RemoveWhere(i => i >= _labels.Count);
        PageFirstIndex = 0;
        EnsureWindow();
    }

    /// <summary>单选确认 → 当前游标下标（空集返回 -1）。</summary>
    public int ConfirmIndex() => _labels.Count == 0 ? -1 : SelectedIndex;

    /// <summary>多选确认 → 勾选下标集（升序）。</summary>
    public IReadOnlyList<int> ConfirmChecked() => CheckedIndices();

    private void SetSelected(int index)
    {
        if (_labels.Count == 0)
        {
            SelectedIndex = 0;
            return;
        }

        var clamped = Math.Clamp(index, 0, _labels.Count - 1);
        SelectedIndex = clamped;
        EnsureWindow();
    }

    /// <summary>页窗滚动：把游标纳入可见区（仅 <c>allowPaging</c> 时改 PageFirstIndex）。</summary>
    private void EnsureWindow()
    {
        if (!_allowPaging)
        {
            PageFirstIndex = 0;
            return;
        }

        if (SelectedIndex < PageFirstIndex)
            PageFirstIndex = SelectedIndex;
        else if (SelectedIndex >= PageFirstIndex + _pageSize)
            PageFirstIndex = SelectedIndex - _pageSize + 1;

        if (PageFirstIndex < 0)
            PageFirstIndex = 0;
    }
}
