using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

public static class InstallerNative
{
    private const string DesktopName = "Validation";
    private const uint WaitTimeout = 258;

    public static string StationName(string runName) => "LycorisInstaller_" + runName;

    private static void RequireHosted(string runName)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            !Regex.IsMatch(runName, @"\A[0-9a-f]{32}\z"))
            throw new InvalidOperationException("A disposable GitHub-hosted Windows runner is required.");
    }

    public static uint RunWorker(string runName, string user, string sid, SecureString password,
        string executable, string script, string scratch, int timeoutMilliseconds)
    {
        RequireHosted(runName);
        if (user != "lyci_" + runName.Substring(0, 12) || !Regex.IsMatch(sid, @"\AS-1-5-21-(?:[0-9]+-){3}[0-9]+\z"))
            throw new ArgumentException("Unexpected disposable account identity.");

        // The ACL is set at creation on fresh, private objects. The runner's desktop ACL stays intact.
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(
            "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + sid + ")", 1, out var descriptor, IntPtr.Zero), "Create desktop ACL");
        IntPtr station = IntPtr.Zero, desktop = IntPtr.Zero;
        try
        {
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var previousStation = GetProcessWindowStation();
            var previousDesktop = GetThreadDesktop(GetCurrentThreadId());
            Check(previousStation != IntPtr.Zero && previousDesktop != IntPtr.Zero, "Read runner window station and desktop");
            var stationName = StationName(runName);
            station = CreateWindowStationW(stationName, 0, 0x000F037F, ref security);
            Check(station != IntPtr.Zero, "Create private window station");
            var stationAlreadyExisted = Marshal.GetLastWin32Error() == 183;
            try
            {
                if (stationAlreadyExisted) throw new InvalidOperationException("Private window station already exists.");
                Check(SetProcessWindowStation(station), "Attach to private window station");
                desktop = CreateDesktopW(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, ref security);
                Check(desktop != IntPtr.Zero, "Create private desktop");
            }
            finally
            {
                Check(SetProcessWindowStation(previousStation), "Restore runner window station");
                Check(SetThreadDesktop(previousDesktop), "Restore runner thread desktop");
            }

            using var job = CreateJobObjectW(IntPtr.Zero, null);
            Check(!job.IsInvalid, "Create disposable process job");
            var limits = new JobLimits { Flags = 0x2000 }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()), "Set disposable process job limits");
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = stationName + "\\" + DesktopName,
            };
            var command = new StringBuilder(Quote(executable) + " -NoProfile -NonInteractive -File " + Quote(script)
                + " -RunName " + runName);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var system = Path.Combine(windows, "System32");
            var temp = Path.Combine(scratch, "temp");
            // Supply only the CI guard and OS essentials. Do not inherit runner credentials.
            var environmentText = "ComSpec=" + Path.Combine(system, "cmd.exe") + "\0GITHUB_ACTIONS=true\0"
                + "OS=Windows_NT\0Path=" + system + ";" + windows + "\0RUNNER_ENVIRONMENT=github-hosted\0"
                + "SystemDrive=" + Path.GetPathRoot(windows).TrimEnd('\\') + "\0SystemRoot=" + windows + "\0"
                + "TEMP=" + temp + "\0TMP=" + temp + "\0windir=" + windows + "\0";
            var environment = Marshal.StringToHGlobalUni(environmentText);
            var clearPassword = IntPtr.Zero;
            ProcessInformation worker;
            try
            {
                clearPassword = Marshal.SecureStringToGlobalAllocUnicode(password);
                // LOGON_WITH_PROFILE, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED.
                Check(CreateProcessWithLogonW(user, Environment.MachineName, clearPassword, 1, executable, command,
                    0x08000404, environment, scratch, ref startup, out worker), "Start standard worker on private desktop");
            }
            finally
            {
                if (clearPassword != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(clearPassword);
                Marshal.FreeHGlobal(environment);
            }
            using var process = new SafeFileHandle(worker.Process, true);
            using var thread = new SafeFileHandle(worker.Thread, true);
            var assigned = false;
            try
            {
                Check(AssignProcessToJobObject(job, process), "Confine standard worker to process job");
                assigned = true;
                Check(ResumeThread(thread) != uint.MaxValue, "Resume standard worker");
                var wait = WaitForSingleObject(process, (uint)timeoutMilliseconds);
                if (wait == WaitTimeout) throw new TimeoutException("Standard installer worker timed out.");
                Check(wait == 0, "Wait for standard installer worker");
                Check(GetExitCodeProcess(process, out var exitCode), "Read standard worker exit code");
                return exitCode;
            }
            finally
            {
                // This job is assigned only to the newly created suspended worker and its descendants.
                if (assigned) Check(TerminateJobObject(job, 1), "Stop remaining disposable worker processes");
                else Check(TerminateProcess(process, 1), "Stop unassigned suspended worker");
                Check(WaitForSingleObject(process, 5000) == 0, "Wait for worker teardown");
            }
        }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            if (station != IntPtr.Zero) CloseWindowStation(station);
            LocalFree(descriptor);
        }
    }

    public static void RequirePrivateDesktop(string runName)
    {
        RequireHosted(runName);
        if (ObjectName(GetProcessWindowStation()) != StationName(runName) ||
            ObjectName(GetThreadDesktop(GetCurrentThreadId())) != DesktopName)
            throw new InvalidOperationException("The installer worker is outside its private window station or desktop.");
    }

    public static int AnswerUninstallDialogs(string runName, uint rootPid, bool? deleteData, ISet<string> answered)
    {
        RequirePrivateDesktop(runName);
        var owners = new HashSet<uint> { rootPid };
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        Check(!snapshot.IsInvalid, "Snapshot owned process tree");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var parents = new Dictionary<uint, uint>();
        Check(Process32FirstW(snapshot, ref entry), "Read process tree");
        do { parents.Add(entry.ProcessId, entry.ParentProcessId); }
        while (Process32NextW(snapshot, ref entry));
        Check(Marshal.GetLastWin32Error() == 18, "Complete process tree snapshot");
        bool expanded;
        do
        {
            expanded = false;
            foreach (var pair in parents)
                if (owners.Contains(pair.Value)) expanded |= owners.Add(pair.Key);
        } while (expanded);

        var dialogs = new List<IntPtr>();
        Check(EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owners.Contains(owner) && IsWindowVisible(window) && ClassName(window) == "#32770") dialogs.Add(window);
            return true;
        }, IntPtr.Zero), "Enumerate owned dialogs");
        foreach (var dialog in dialogs)
        {
            GetWindowThreadProcessId(dialog, out var owner);
            if (!owners.Contains(owner)) continue;
            var labels = new List<string>();
            EnumChildWindows(dialog, (child, unused) =>
            {
                if (ClassName(child) == "Static")
                {
                    var label = new StringBuilder(1024);
                    // Cross-process GetWindowText does not read controls. Bound WM_GETTEXT on the private desktop.
                    if (SendMessageTimeoutW(child, 13, new UIntPtr(1024), label, 2, 500, out _) != IntPtr.Zero && label.Length > 0)
                        labels.Add(Regex.Replace(label.ToString(), @"\s+", " ").Trim());
                }
                return true;
            }, IntPtr.Zero);
            if (labels.Count == 0) continue;
            const string dataPrompt = "Удалить настройки, логи и кэш Lycoris из %LocalAppData%? Защищённый журнал в %ProgramData% останется. / Delete Lycoris settings, logs, and cache from %LocalAppData%? The protected journal in %ProgramData% will remain.";
            string kind;
            int button;
            if (deleteData.HasValue && labels.Contains(dataPrompt))
                (kind, button) = ("data", deleteData.Value ? 6 : 7); // IDYES, IDNO
            else if (deleteData.HasValue && labels.Contains("Are you sure you want to completely remove Lycoris and all of its components?"))
                (kind, button) = ("confirm", 6);
            else if (deleteData.HasValue && labels.Contains("Lycoris was successfully removed from your computer."))
                (kind, button) = ("complete", 1); // IDOK
            else
                throw new InvalidOperationException("Unexpected Lycoris uninstaller dialog: " + string.Join(" | ", labels));
            if (answered.Contains(kind)) continue; // Posted once. The dialog may still be processing it.
            var control = GetDlgItem(dialog, button);
            GetWindowThreadProcessId(control, out var controlOwner);
            if (control == IntPtr.Zero || controlOwner != owner || ClassName(control) != "Button" || !IsWindowEnabled(control))
                throw new InvalidOperationException("The expected uninstaller button could not be verified.");
            // WM_COMMAND/BN_CLICKED to the verified dialog works on the non-input desktop.
            Check(PostMessageW(dialog, 0x0111, new UIntPtr((uint)button), control), "Answer verified uninstaller dialog");
            answered.Add(kind);
        }
        int count = 0;
        foreach (var owner in owners)
            if (parents.ContainsKey(owner)) count++;
        return count;
    }

    private static string Quote(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || path.EndsWith("\\", StringComparison.Ordinal))
            throw new ArgumentException("Expected an absolute file path.");
        return "\"" + path + "\"";
    }

    private static string ObjectName(IntPtr handle)
    {
        var buffer = new StringBuilder(128);
        Check(GetUserObjectInformationW(handle, 2, buffer, 256, out _), "Read private GUI object name");
        return buffer.ToString();
    }

    private static string ClassName(IntPtr window)
    {
        var buffer = new StringBuilder(64);
        GetClassNameW(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static void Check(bool success, string operation)
    {
        if (success) return;
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, operation + " failed (Win32 " + error + ").");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId, ThreadCount, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, IntPtr size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateProcessWithLogonW(string user, string domain, IntPtr password, uint logonFlags,
        string application, StringBuilder commandLine, uint flags, IntPtr environment, string directory, ref StartupInfo startup,
        out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateWindowStationW(string station, uint flags, uint access, ref SecurityAttributes security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateDesktopW(string desktop, IntPtr device, IntPtr mode, uint flags, uint access,
        ref SecurityAttributes security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, uint bytes, out uint needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder buffer, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int button);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr word, StringBuilder text,
        uint flags, uint milliseconds, out UIntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, UIntPtr word, IntPtr parameter);
}
