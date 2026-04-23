using System.Threading.Tasks;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Evaluates the value/quality of a memory with given context.
    /// </summary>
    public interface IMemoryEvaluator
    {
        Task<double> EvaluateValueAsync(object memory, object context);
    }
}
