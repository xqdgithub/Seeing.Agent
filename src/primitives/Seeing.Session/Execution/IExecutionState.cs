namespace Seeing.Session.Execution
{
    // Defines the execution state contract for a long-running operation
    public interface IExecutionState
    {
        // Indicates whether an execution is currently in progress
        bool IsExecuting { get; }

        // Indicates whether the execution is currently paused
        bool IsPaused { get; }

        // Optional last error message from the execution
        string? LastError { get; }

        // Asynchronously start the execution
        Task StartExecutionAsync();

        // Asynchronously pause the execution
        Task PauseExecutionAsync();

        // Asynchronously resume the execution
        Task ResumeExecutionAsync();

        // Asynchronously cancel the execution
        Task CancelExecutionAsync();

        // Get a cancellation token that can be observed by the running operation
        CancellationToken GetCancellationToken();
    }
}
