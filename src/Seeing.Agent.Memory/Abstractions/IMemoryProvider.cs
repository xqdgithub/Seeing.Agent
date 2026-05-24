namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Core memory provider interface that exposes sub-services for repository, manager, indexer, evaluator, scorer, filter and retriever.
    /// </summary>
    public interface IMemoryProvider
    {
        Task InitializeAsync();

        IMemoryRepository MemoryRepository { get; }
        IMemoryManager MemoryManager { get; }
        IMemoryIndexer MemoryIndexer { get; }
        IMemoryEvaluator MemoryEvaluator { get; }
        IMemoryScorer MemoryScorer { get; }
        IMemoryFilter MemoryFilter { get; }
        IMemoryRetriever MemoryRetriever { get; }
    }
}
