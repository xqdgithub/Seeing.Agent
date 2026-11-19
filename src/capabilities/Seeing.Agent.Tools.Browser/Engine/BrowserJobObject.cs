using System.Runtime.InteropServices;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// Windows Job 对象：带 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>，句柄一关，job 内进程全部被杀。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要 Job 兜底</b>：宿主进程被任务管理器强杀 / 崩溃时，没有任何机会执行
/// <c>DisposeAsync</c>。Default Job 的「最后一个句柄关闭 → 杀掉 job 内所有进程」是内核保证，
/// 不依赖我们的清理代码跑起来，因此是唯一能在「宿主被杀」这条路径上生效的机制。
/// Chromium 的 <c>--remote-debugging-port</c> 本身不会让浏览器随父进程退出（实测宿主被杀后浏览器仍存活）。
/// </para>
/// <para>
/// <b>为什么必须「宿主先把自己 assign 进去」</b>：Job 归属只对 assign 之后创建的子进程自动生效。
/// 宿主先入 job，再 spawn Chromium，Chromium 及其后续 renderer/GPU 子进程就全部自动在 job 内。
/// 若反过来只把 Chromium 的 PID assign 进去，已经创建好的 renderer 不在 job 里。
/// </para>
/// <para>
/// <b>非 Windows 一律返回 <see langword="null"/></b>：POSIX 没有对应语义（Job 是 Windows 内核对象），
/// 那些平台由宿主侧 watchdog 兜底，这里不假装支持。
/// </para>
/// <para>
/// <b>测试绝不能 <see cref="AssignCurrentProcess"/></b>：带 KILL_ON_JOB_CLOSE 的 job 一 dispose
/// 就会杀掉 job 内所有进程，把测试进程自己 assign 进去等于「跑完这个用例把 runner 带走」。
/// </para>
/// </remarks>
public sealed class BrowserJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    private IntPtr _handle;

    private BrowserJobObject(IntPtr handle) => _handle = handle;

    /// <summary>
    /// 创建 Job 并设置 <c>KILL_ON_JOB_CLOSE</c>；非 Windows 或创建失败时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 失败返回 <see langword="null"/> 而不是抛：Job 只是「宿主被杀」这一条路径的兜底，
    /// 拿不到不该让整个浏览器宿主起不来（例如受限容器里 <c>CreateJobObject</c> 被拒）。
    /// </remarks>
    public static BrowserJobObject? Create()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return null;

        var extendedLimits = new JobObjectExtendedLimitInformation();
        extendedLimits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(extendedLimits, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(
                    handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new BrowserJobObject(handle);
    }

    /// <summary>
    /// 把指定进程放进 job。成功返回 <see langword="true"/>；已释放 / 非 Windows / 进程不存在时返回 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 用 <c>OpenProcess</c> 而不是 <c>Process.GetProcessById().Handle</c>：后者在 .NET 里会顺手管理
    /// 进程句柄的生命周期，而这里只需要一次「传进内核调用」的临时句柄，用完立刻关掉最不容易泄漏。
    /// </remarks>
    public bool TryAssign(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
            return false;

        var jobHandle = Volatile.Read(ref _handle);
        if (jobHandle == IntPtr.Zero)
            return false;

        var processHandle = OpenProcess(
            ProcessSetQuota | ProcessTerminate, bInheritHandle: false, (uint)processId);
        if (processHandle == IntPtr.Zero)
            return false;

        try
        {
            return AssignProcessToJobObject(jobHandle, processHandle);
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    /// <summary>
    /// 创建 Job 并把<b>当前进程</b>放进去，供宿主进程在 spawn 浏览器之前调用。
    /// </summary>
    /// <returns>持有 Job 的对象（调用方必须活到进程退出）；失败返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>只给宿主进程用</b>：调用方就是那个「死了要带走浏览器」的进程。
    /// 测试 / 客户端的普通进程不得调用 —— 见类型注释。
    /// </para>
    /// <para>
    /// 分配失败（进程已在不允许 breakaway 的外层 job 里、权限不足）返回 <see langword="null"/>，
    /// 由调用方记 Warning；POSIX 由 watchdog 兜底。
    /// </para>
    /// </remarks>
    public static BrowserJobObject? AssignCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var job = Create();
        if (job is null)
            return null;

        if (job.TryAssign(Environment.ProcessId))
            return job;

        // 没放进去的 job 必须立刻关掉：留着只会让人以为「已经受约束」。
        // 空 job 关闭没有副作用（没有成员可杀）。
        job.Dispose();
        return null;
    }

    /// <summary>关闭句柄。若进程在 job 内，它们会被内核立刻终止。</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}
