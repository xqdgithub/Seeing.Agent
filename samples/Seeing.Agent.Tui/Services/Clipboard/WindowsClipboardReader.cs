using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Seeing.Agent.Tui.Services;

/// <summary>Windows 剪贴板读取：文件列表 / 文本 / PNG。</summary>
public sealed class WindowsClipboardReader : IClipboardReader
{
    private const uint CfHDrop = 15;
    private const uint CfUnicodeText = 13;

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public IReadOnlyList<ClipboardFile> ReadFiles()
    {
        if (!OperatingSystem.IsWindows() || !OpenClipboard(IntPtr.Zero))
            return Array.Empty<ClipboardFile>();

        try
        {
            var handle = GetClipboardData(CfHDrop);
            if (handle == IntPtr.Zero)
                return Array.Empty<ClipboardFile>();

            var count = DragQueryFileW(handle, uint.MaxValue, null, 0);
            var files = new List<ClipboardFile>((int)count);
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFileW(handle, index, null, 0);
                var buffer = new StringBuilder((int)length + 1);
                if (DragQueryFileW(handle, index, buffer, (uint)buffer.Capacity) == 0)
                    continue;

                var path = buffer.ToString();
                var mime = TuiAttachmentResolver.ResolveMimeType(path);
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                files.Add(new ClipboardFile(path, mime, TuiAttachmentResolver.IsImagePath(path), size));
            }

            return files;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <inheritdoc />
    public string? ReadText()
    {
        if (!OperatingSystem.IsWindows() || !OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <inheritdoc />
    public async Task<ClipboardImage?> ReadImageAsync(CancellationToken ct = default)
    {
        var png = TryReadPng();
        if (png is { Length: > 0 })
            return new ClipboardImage(png, "screenshot.png", png.Length);

        return await ReadImageWithPowerShellAsync(ct).ConfigureAwait(false);
    }

    private static byte[]? TryReadPng()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var format = RegisterClipboardFormatW("PNG");
        if (format == 0 || !OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
                return null;

            var size = (long)GlobalSize(handle);
            if (size <= 0 || size > int.MaxValue)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static async Task<ClipboardImage?> ReadImageWithPowerShellAsync(CancellationToken ct)
    {
        const string script = """
            Add-Type -AssemblyName System.Windows.Forms
            $img = [System.Windows.Forms.Clipboard]::GetImage()
            if ($img) {
                $path = Join-Path $env:TEMP ('seeing-paste-' + [guid]::NewGuid().ToString('N') + '.png')
                $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
                $img.Dispose()
                Write-Output $path
            }
            """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -STA -NonInteractive -EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return null;
            }

            if (process.ExitCode != 0)
                return null;

            var path = (await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(path))
                return null;

            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                return new ClipboardImage(bytes, "screenshot.png", bytes.Length);
            }
            finally
            {
                try { File.Delete(path); } catch { /* 临时文件清理失败不阻断 */ }
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}
