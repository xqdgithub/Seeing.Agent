using System.Collections.Generic;
using System.Threading.Tasks;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Multi-factor scorer for memories.
    /// </summary>
    public interface IMemoryScorer
    {
        Task<double> ScoreAsync(object memory, IDictionary<string, object> options);
    }
}
