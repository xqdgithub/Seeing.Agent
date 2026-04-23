using System.Threading.Tasks;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Hook for indexing memories (vector/index based indexing is reserved).
    /// </summary>
    public interface IMemoryIndexer
    {
        Task<object> IndexAsync(object memory);
        Task<object> IndexAsync(string memoryId, object memory);
    }
}
