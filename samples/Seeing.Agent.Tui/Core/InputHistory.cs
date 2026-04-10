namespace Seeing.Agent.Tui.Core;

/// <summary>
/// 输入历史管理
/// </summary>
public class InputHistory
{
    private readonly List<string> _history = new();
    private int _maxSize = 100;
    private int _currentIndex = -1;
    
    /// <summary>历史记录数量</summary>
    public int Count => _history.Count;
    
    /// <summary>添加输入到历史</summary>
    public void Add(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return;
        
        // 避免重复
        if (_history.Count > 0 && _history[^1] == input)
            return;
        
        _history.Add(input);
        
        // 限制大小
        if (_history.Count > _maxSize)
            _history.RemoveAt(0);
        
        // 重置索引
        _currentIndex = _history.Count;
    }
    
    /// <summary>获取上一条历史（向上箭头）</summary>
    public string? GetPrevious()
    {
        if (_history.Count == 0 || _currentIndex <= 0)
            return null;
        
        _currentIndex--;
        return _history[_currentIndex];
    }
    
    /// <summary>获取下一条历史（向下箭头）</summary>
    public string? GetNext()
    {
        if (_history.Count == 0 || _currentIndex >= _history.Count - 1)
        {
            _currentIndex = _history.Count;
            return null;
        }
        
        _currentIndex++;
        return _history[_currentIndex];
    }
    
    /// <summary>重置索引（回到最新）</summary>
    public void ResetIndex()
    {
        _currentIndex = _history.Count;
    }
    
    /// <summary>清空历史</summary>
    public void Clear()
    {
        _history.Clear();
        _currentIndex = -1;
    }
    
    /// <summary>获取所有历史记录</summary>
    public IReadOnlyList<string> GetAll() => _history.AsReadOnly();
}