namespace Seeing.Agent.Memory.Core
{
    // Future Work: Evaluates the memory snippet/content quality/value using LLM or heuristic later.
    /// <summary>
    /// Memory evaluation interface (placeholder for future value judgement).
    /// </summary>
    public interface IEvaluateMemoryAsync
    {
        /// <summary>
        /// Evaluate the given memory content and return a memory value judgement.
        /// Note: Implementation is not provided; this is a placeholder for future work.
        /// </summary>
        /// <param name="content">The memory content to evaluate.</param>
        /// <returns>A <see cref="MemoryEvaluationResult"/> describing the judgement.</returns>
        Task<MemoryEvaluationResult> EvaluateMemoryAsync(string content);
    }

    /// <summary>
    /// Placeholder result type for memory evaluation.
    /// </summary>
    public class MemoryEvaluationResult
    {
        // Placeholder properties for future value judgement data.
        public double Score { get; set; } = 0.0;
        public string Verdict { get; set; } = "NotImplemented";
        public string? Details { get; set; } = null;
    }

    /// <summary>
    /// Empty implementation of the memory evaluator.
    /// <para>Future Work: wire to LLM or heuristic evaluator.</para>
    /// </summary>
    public class MemoryEvaluator : IEvaluateMemoryAsync
    {
        public Task<MemoryEvaluationResult> EvaluateMemoryAsync(string content)
        {
            // Not implemented yet. Return default result to satisfy interface.
            MemoryEvaluationResult result = new MemoryEvaluationResult();
            // Indicate not implemented through status in result.
            return Task.FromResult(result);
        }
    }
}
