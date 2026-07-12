namespace Seeing.Agent.App.Execution;

/// <summary>
/// A circular buffer that holds a fixed number of items.
/// When full, new items overwrite the oldest items.
/// Thread-safe for single-writer, multiple-reader scenarios.
/// </summary>
/// <typeparam name="T">The type of items in the buffer.</typeparam>
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new circular buffer with the specified capacity.
    /// </summary>
    /// <param name="capacity">Maximum number of items the buffer can hold.</param>
    public CircularBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");

        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Gets the capacity of the buffer.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the current number of items in the buffer.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Adds an item to the buffer. If the buffer is full, overwrites the oldest item.
    /// </summary>
    /// <param name="item">The item to add.</param>
    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }
    }

    /// <summary>
    /// Gets all items in the buffer in chronological order (oldest first).
    /// </summary>
    /// <returns>A list of all items in the buffer.</returns>
    public IReadOnlyList<T> GetAll()
    {
        lock (_lock)
        {
            var result = new List<T>(_count);
            
            if (_count < _buffer.Length)
            {
                // Buffer not full yet, read from start
                for (int i = 0; i < _count; i++)
                {
                    result.Add(_buffer[i]!);
                }
            }
            else
            {
                // Buffer is full, start from oldest (current head position)
                for (int i = 0; i < _buffer.Length; i++)
                {
                    var index = (_head + i) % _buffer.Length;
                    result.Add(_buffer[index]!);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Gets the most recent N items from the buffer.
    /// </summary>
    /// <param name="count">Maximum number of items to retrieve.</param>
    /// <returns>A list of the most recent items.</returns>
    public IReadOnlyList<T> GetMostRecent(int count)
    {
        lock (_lock)
        {
            var result = new List<T>(Math.Min(count, _count));
            
            var start = _count < _buffer.Length 
                ? Math.Max(0, _count - count) 
                : Math.Max(0, _buffer.Length - count);

            var actualCount = Math.Min(count, _count);

            if (_count < _buffer.Length)
            {
                for (int i = start; i < _count; i++)
                {
                    result.Add(_buffer[i]!);
                }
            }
            else
            {
                // Buffer is full
                var startIndex = (_head + _buffer.Length - actualCount) % _buffer.Length;
                for (int i = 0; i < actualCount; i++)
                {
                    var index = (startIndex + i) % _buffer.Length;
                    result.Add(_buffer[index]!);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Clears all items from the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }
    }
}