using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EasyTerminal.Core.Transport;

public interface ITerminalTransport : IDisposable
{
    /// <summary>Raised on a background thread with a buffer that is only valid during the call.</summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;
    event Action<int?>? Exited;
    void Start(int cols, int rows);
    void Write(ReadOnlySpan<byte> data);
    void Resize(int cols, int rows);
    int? ProcessId { get; }
}

/// <summary>Runs a console program inside a Windows pseudo console.</summary>
public sealed unsafe class ConPtyTransport : ITerminalTransport
{
    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly Action? _onDispose;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _writer;
    private nint _console;
    private nint _process;
    private nint _job;
    private int _disposed;
    private readonly object _consoleLock = new();

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<int?>? Exited;
    public int? ProcessId { get; private set; }

    public ConPtyTransport(string commandLine, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Action? onDispose = null)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
        _environment = environment;
        _onDispose = onDispose;
    }

    public void Start(int cols, int rows)
    {
        if (!CreatePipe(out var inputRead, out _inputWrite, 0, 0) || !CreatePipe(out _outputRead, out var outputWrite, 0, 0))
            throw new Win32Exception();

        int hr = CreatePseudoConsole(new Coord((short)cols, (short)rows), inputRead, outputWrite, 0, out _console);
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        StartProcess();
        _writer = new FileStream(_inputWrite, FileAccess.Write, 1, false);

        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" };
        reader.Start();
        var waiter = new Thread(WaitLoop) { IsBackground = true, Name = "ConPTY waiter" };
        waiter.Start();
    }

    private void StartProcess()
    {
        nint attributeSize = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref attributeSize);
        nint attributes = Marshal.AllocHGlobal(attributeSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, _console, nint.Size, 0, 0)) throw new Win32Exception();

            // Explicit null handles stop a redirected parent's stdio from bypassing the pseudo console.
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = sizeof(StartupInfoEx), dwFlags = StartfUseStdHandles }, lpAttributeList = attributes };
            var commandLine = new StringBuilder(_commandLine);
            string? environment = BuildEnvironmentBlock();

            const uint flags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended;
            if (!CreateProcessW(null, commandLine, 0, 0, false, flags, environment, _workingDirectory, ref startup, out var info))
                throw new Win32Exception();

            // Children join a kill-on-close job, so nothing outlives the app even if it crashes.
            _job = CreateJobObjectW(0, null);
            if (_job != 0)
            {
                var limits = new JobObjectExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                SetInformationJobObject(_job, JobObjectExtendedLimitInformationClass, ref limits, sizeof(JobObjectExtendedLimitInformation));
                AssignProcessToJobObject(_job, info.hProcess);
            }
            ResumeThread(info.hThread);
            CloseHandle(info.hThread);
            _process = info.hProcess;
            ProcessId = info.dwProcessId;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private string? BuildEnvironmentBlock()
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            merged[(string)entry.Key] = (string?)entry.Value ?? "";
        merged["TERM"] = "xterm-256color";
        merged["COLORTERM"] = "truecolor";
        merged["TERM_PROGRAM"] = "EasyTerminal";
        if (_environment != null)
            foreach (var (key, value) in _environment) merged[key] = value;

        var block = new StringBuilder();
        foreach (var (key, value) in merged) block.Append(key).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return block.ToString();
    }

    private void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            using var stream = new FileStream(_outputRead!, FileAccess.Read, 1, false);
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                DataReceived?.Invoke(buffer.AsMemory(0, read));
            }
        }
        catch (IOException)
        {
            // The pipe breaks when the pseudo console closes.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, uint.MaxValue);
        int? code = GetExitCodeProcess(_process, out uint exit) ? (int)exit : null;
        // Closing the console flushes remaining output and ends the reader.
        CloseConsole();
        Exited?.Invoke(code);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var writer = _writer;
        if (writer == null) return;
        try
        {
            writer.Write(data);
            writer.Flush();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_consoleLock)
        {
            if (_console != 0) ResizePseudoConsole(_console, new Coord((short)cols, (short)rows));
        }
    }

    private void CloseConsole()
    {
        nint console;
        lock (_consoleLock)
        {
            console = _console;
            _console = 0;
        }
        if (console != 0) ClosePseudoConsole(console);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_process != 0) TerminateProcess(_process, 1);
        // ClosePseudoConsole can block until output drains, so never run it on the caller's thread.
        Task.Run(() =>
        {
            CloseConsole();
            _writer?.Dispose();
            if (_job != 0) CloseHandle(_job);
            _onDispose?.Invoke();
        });
    }

    // ---- Native -----------------------------------------------------------------------------

    private const int StartfUseStdHandles = 0x00000100;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(nint console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inherit, uint flags, string? environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformation info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(nint process, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint code);
}

public static class CommandLine
{
    /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        return sb.Append('"').ToString();
    }

    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));
}
