using System;

namespace Seeing.Agent.Git
{
    /// <summary>
    /// Git 操作异常
    /// </summary>
    public class GitException : Exception
    {
        public int ExitCode { get; }
        public string StdOut { get; }
        public string StdErr { get; }
        public string? GitCommand { get; }

        public GitException(string message, int exitCode, string stdOut, string stdErr, string? command = null)
            : base(message)
        {
            ExitCode = exitCode;
            StdOut = stdOut;
            StdErr = stdErr;
            GitCommand = command;
        }

        public GitException(string message, Exception innerException)
            : base(message, innerException)
        {
            ExitCode = -1;
            StdOut = string.Empty;
            StdErr = string.Empty;
        }

        public static GitException FromResult(GitResult result, string command)
        {
            var message = !string.IsNullOrEmpty(result.StdErr)
                ? result.StdErr
                : $"Git command failed with exit code {result.ExitCode}";

            return new GitException(message, result.ExitCode, result.StdOut, result.StdErr, command);
        }
    }
}
