using System.Collections.Generic;
using System.Threading.Tasks;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Heuristic filter for candidate memories.
    /// </summary>
    public interface IMemoryFilter
    {
        Task<IEnumerable<object>> FilterAsync(IEnumerable<object> candidates);
    }
}
