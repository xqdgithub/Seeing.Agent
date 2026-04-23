namespace Seeing.Agent.Memory.Core
{
    /// <summary>
    /// Hook points for memory-related events.
    /// </summary>
    public static class MemoryHookPoints
    {
        public const string Created = "memory.created";
        public const string Searched = "memory.searched";
        public const string Retrieved = "memory.retrieved";
        public const string Updated = "memory.updated";
        public const string Deleted = "memory.deleted";
    }
}
