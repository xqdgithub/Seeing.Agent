using System;

namespace Seeing.Agent.Core.Background
{
    /// <summary>
    /// 后台任务进度报告接口
    /// </summary>
    public interface IBackgroundTaskProgress
    {
        /// <summary>报告进度 (0-100)</summary>
        void Report(int percent, string? message = null);

        /// <summary>报告输出行</summary>
        void ReportOutput(string line);

        /// <summary>报告错误</summary>
        void ReportError(string error);
    }
}