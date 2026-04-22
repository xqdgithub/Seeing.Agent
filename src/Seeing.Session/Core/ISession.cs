using System;
using System.Threading.Tasks;

namespace Seeing.Session.Core
{
    /// <summary>
    /// Minimal session interface to satisfy dependent factories/stores
    /// </summary>
    public interface ISession
    {
        // Core identity and metadata
        string Id { get; }
        string Title { get; set; }
        DateTime CreatedAt { get; }
        DateTime UpdatedAt { get; }
        string PartitionId { get; set; }

        // Lifecycle
        Task SaveAsync();
        Task LoadAsync();
        Task ResumeAsync();
        Task DestroyAsync();

        // State management
        TValue GetState<TValue>(string key);
        void SetState<TValue>(string key, TValue value);

        // Serialization
        string Serialize();
        Task DeserializeAsync(string payload);
    }
}
