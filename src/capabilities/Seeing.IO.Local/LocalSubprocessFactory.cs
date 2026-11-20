using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seeing.IO.Local;

/// <summary>
/// 基于 <see cref="Process.Start"/> 的本地子进程工厂。
/// </summary>
/// <remarks>
/// <para>
/// <b>生命周期绑定</b>：<see cref="SubprocessLifecycle.HostBound"/>（默认）在 Windows 上为
/// <b>每个进程单独</b>创建一个 Job Object 并设置 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>。
/// 宿主进程退出的那一刻，内核关闭 Job 句柄并杀掉 job 内整棵进程树 —— 宿主崩溃时
/// <c>bash -c "… &amp;"</c> 留下的后台子进程不会再占住 bin 目录。
/// </para>
/// <para>
/// <b>句柄归工厂持有、不做「启动器退出即关闭」</b>：<c>bash -c "npm run dev &amp;"</c> 的后台子进程
/// 必须在启动器 shell 退出后继续存活，只有<b>宿主</b>退出才该回收；过早关闭句柄会把后台任务一并杀掉。
/// 因此句柄保留到宿主退出（OS 关闭，天然触发回收）；「进程树已经跑完」的 job 在后续
/// <see cref="Start"/> 调用时惰性回收，避免长期运行累积句柄。
/// </para>
/// <para>
/// <b>禁止共享 Job</b>：共享 job 会在任意一个进程退出/关句柄时误杀其它进程。
/// </para>
/// <para>
/// 绑定失败（受限容器、进程已在无法嵌套的 job 里、<c>CreateJobObject</c> 被拒等）只记日志并降级为
/// 「无生命周期绑定」，绝不影响启动本身。
/// </para>
/// </remarks>
public sealed class LocalSubprocessFactory : ISubprocessFactory
{
    /// <summary>累积到该数量后惰性回收「进程树已空」的 Job 句柄。</summary>
    private const int JobSweepThreshold = 32;

    private readonly ILogger? _logger;
    private readonly Lock _jobsGate = new();
    private readonly List<nint> _retainedJobs = [];

    /// <summary>创建工厂。</summary>
    /// <param name="logger">可选日志；绑定失败只记录并降级，不影响启动。</param>
    public LocalSubprocessFactory(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ISubprocess Start(SubprocessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(spec.FileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = spec.RedirectStandardOutput,
            RedirectStandardError = spec.RedirectStandardError,
            StandardOutputEncoding = spec.Encoding,
            StandardErrorEncoding = spec.Encoding,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (spec.ArgumentList is { Count: > 0 })
        {
            foreach (var argument in spec.ArgumentList)
                startInfo.ArgumentList.Add(argument);
        }
        else
        {
            startInfo.Arguments = spec.Arguments;
        }

        foreach (var (key, value) in spec.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{spec.FileName}'.");

        BindLifecycle(process, spec.Lifecycle);

        return new LocalSubprocess(process);
    }

    /// <summary>
    /// 按生命周期归属建立内核级绑定；失败只记日志并降级。
    /// </summary>
    private void BindLifecycle(Process process, SubprocessLifecycle lifecycle)
    {
        if (lifecycle != SubprocessLifecycle.HostBound)
            return;

        if (!OperatingSystem.IsWindows())
        {
            // TODO(POSIX)：HostBound 需要 setsid() 或 setpgid() 建立独立进程组，
            // 宿主退出时对进程组发信号；本期只保证 Windows 的 Job Object，
            // POSIX 上 HostBound 暂时与 Detached 等效（不回收）。
            return;
        }

        // 进程极短命（如 where/which）：Start 返回时已退出，assign 必然失败且没有可回收的子进程。
        if (process.HasExited)
        {
            _logger?.LogDebug("子进程 {Pid} 在绑定 HostBound Job 之前已退出，跳过绑定。", process.Id);
            return;
        }

        var job = WindowsJobApi.CreateKillOnCloseJob();
        if (job == 0)
        {
            _logger?.LogWarning(
                "创建 HostBound Job Object 失败（Win32 错误 {Win32Error}），子进程 {Pid} 降级为不受宿主生命周期约束。",
                Marshal.GetLastWin32Error(),
                process.Id);
            return;
        }

        // 进程已在别的 job 里且不允许嵌套时 assign 返回 ACCESS_DENIED：只降级，不影响启动。
        if (!WindowsJobApi.TryAssign(job, process))
        {
            var error = Marshal.GetLastWin32Error();
            WindowsJobApi.CloseHandle(job);
            _logger?.LogWarning(
                "子进程 {Pid} 无法归入 HostBound Job Object（Win32 错误 {Win32Error}），降级为不受宿主生命周期约束。",
                process.Id,
                error);
            return;
        }

        RetainJob(job);
    }

    /// <summary>工厂持有 Job 句柄到宿主退出；顺带惰性回收已经空了的 job。</summary>
    private void RetainJob(nint job)
    {
        lock (_jobsGate)
        {
            _retainedJobs.Add(job);
            if (_retainedJobs.Count < JobSweepThreshold)
                return;

            SweepEmptyJobsLocked();
        }
    }

    /// <summary>关闭「进程树已空」的 Job 句柄；仍有存活进程的保留到宿主退出。</summary>
    private void SweepEmptyJobsLocked()
    {
        for (var i = _retainedJobs.Count - 1; i >= 0; i--)
        {
            if (!WindowsJobApi.TryGetActiveProcessCount(_retainedJobs[i], out var active) || active != 0)
                continue;

            WindowsJobApi.CloseHandle(_retainedJobs[i]);
            _retainedJobs.RemoveAt(i);
        }

        if (_retainedJobs.Count >= JobSweepThreshold)
        {
            // 真正有后台子进程存活的 job：必须留到宿主退出，OS 关句柄即回收整棵树。
            _logger?.LogDebug(
                "仍持有 {JobCount} 个 HostBound Job 句柄（其中仍有存活进程），将在宿主退出时由操作系统回收。",
                _retainedJobs.Count);
        }
    }

    /// <summary>
    /// Windows Job Object 最小封装。仅本工厂内部使用：不共享 job、不对外暴露句柄，
    /// 因此不做成公共类型（<see cref="ISubprocess"/> 不需要知道这一层）。
    /// </summary>
    private static class WindowsJobApi
    {
        private const int JobObjectBasicAccountingInformationClass = 1;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const nint InvalidHandle = 0;

        internal static nint CreateKillOnCloseJob()
        {
            var job = CreateJobObjectW(InvalidHandle, null);
            if (job == InvalidHandle)
                return InvalidHandle;

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref info,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                CloseHandle(job);
                return InvalidHandle;
            }

            return job;
        }

        internal static bool TryAssign(nint job, Process process) =>
            AssignProcessToJobObject(job, process.Handle);

        /// <summary>查询 job 内活跃进程数；查询失败时返回 <see langword="false"/>（保守：保留句柄）。</summary>
        internal static bool TryGetActiveProcessCount(nint job, out uint activeProcesses)
        {
            var info = default(JobObjectBasicAccountingInformation);
            if (!QueryInformationJobObject(
                    job,
                    JobObjectBasicAccountingInformationClass,
                    ref info,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                    InvalidHandle))
            {
                activeProcesses = 0;
                return false;
            }

            activeProcesses = info.ActiveProcesses;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            nint hJob,
            int jobObjectInformationClass,
            ref JobObjectExtendedLimitInformation lpJobObjectInformation,
            uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(
            nint hJob,
            int jobObjectInformationClass,
            ref JobObjectBasicAccountingInformation lpJobObjectInformation,
            uint cbJobObjectInformationLength,
            nint lpReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint hObject);
    }
}
