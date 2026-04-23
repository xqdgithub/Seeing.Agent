using System.Collections.Generic;
using System.Threading.Tasks;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Repository abstraction for persisting and retrieving memory entries.
    /// </summary>
    public interface IMemoryRepository
    {
        Task SaveMemoryAsync(object memory);
        Task<object> GetMemoryAsync(string memoryId);
        Task<IEnumerable<object>> ListMemoriesAsync();
        Task DeleteMemoryAsync(string memoryId);
    }
}
