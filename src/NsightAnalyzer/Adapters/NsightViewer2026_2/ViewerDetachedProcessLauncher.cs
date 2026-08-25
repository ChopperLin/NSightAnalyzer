using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal static class ViewerDetachedProcessLauncher
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static Process Start(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Process.Start(startInfo) ??
                throw new InvalidOperationException("The Viewer process did not start.");
        }

        var commandLine = new StringBuilder(Quote(startInfo.FileName));
        foreach (var argument in startInfo.ArgumentList)
        {
            commandLine.Append(' ').Append(Quote(argument));
        }

        var environment = new StringBuilder();
        foreach (var entry in startInfo.Environment.OrderBy(
                     pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.Key.Contains('\0') || entry.Value?.Contains('\0') == true)
            {
                throw new ArgumentException("The Viewer environment is invalid.");
            }
            environment.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
        }
        environment.Append('\0');

        var environmentPointer = Marshal.StringToHGlobalUni(environment.ToString());
        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        var nullHandle = CreateFileW(
            "NUL",
            GenericRead | GenericWrite,
            ShareRead | ShareWrite,
            ref securityAttributes,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (nullHandle == InvalidHandle)
        {
            Marshal.FreeHGlobal(environmentPointer);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var attributeListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(
                IntPtr.Zero, 1, 0, ref attributeListSize);
            var attributeList = Marshal.AllocHGlobal(attributeListSize);
            var handleList = Marshal.AllocHGlobal(IntPtr.Size);
            var attributeListInitialized = false;
            try
            {
                if (!InitializeProcThreadAttributeList(
                        attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                attributeListInitialized = true;
                Marshal.WriteIntPtr(handleList, nullHandle);
                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        new IntPtr(ProcThreadAttributeHandleList),
                        handleList,
                        new IntPtr(IntPtr.Size),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseShowWindow | StartfUseStdHandles,
                        ShowWindow = startInfo.WindowStyle == ProcessWindowStyle.Minimized
                            ? (short)2
                            : (short)1,
                        StandardInput = nullHandle,
                        StandardOutput = nullHandle,
                        StandardError = nullHandle,
                    },
                    AttributeList = attributeList,
                };
                var flags = CreateBreakawayFromJob |
                    CreateNewProcessGroup |
                    CreateUnicodeEnvironment |
                    ExtendedStartupInfoPresent;
                if (!CreateProcessW(
                        startInfo.FileName,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        flags,
                        environmentPointer,
                        startInfo.WorkingDirectory,
                        ref startup,
                        out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                try
                {
                    return Process.GetProcessById(processInformation.ProcessId);
                }
                finally
                {
                    CloseHandle(processInformation.ThreadHandle);
                    CloseHandle(processInformation.ProcessHandle);
                }
            }
            finally
            {
                if (attributeListInitialized)
                {
                    DeleteProcThreadAttributeList(attributeList);
                }
                Marshal.FreeHGlobal(handleList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
        finally
        {
            CloseHandle(nullHandle);
            Marshal.FreeHGlobal(environmentPointer);
        }
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                ++backslashes;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
