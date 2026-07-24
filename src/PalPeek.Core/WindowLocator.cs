using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PalPeek.Core;

public sealed record WindowCandidate(int ProcessId, nint Handle, string Title, long Area);

public interface IWindowLocator
{
    IReadOnlyList<WindowCandidate> FindForProcesses(IReadOnlySet<int> processIds);
    int? ForegroundProcessId();
}

public sealed class WindowLocator : IWindowLocator
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;

    public IReadOnlyList<WindowCandidate> FindForProcesses(IReadOnlySet<int> processIds)
    {
        var windows = new List<WindowCandidate>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;
            GetWindowThreadProcessId(handle, out var pid);
            if (!processIds.Contains((int)pid) || (GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
                return true;
            if (!GetClientRect(handle, out var rect))
                return true;
            var area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
            if (area < 320L * 200L)
                return true;
            var title = GetTitle(handle);
            windows.Add(new WindowCandidate((int)pid, handle, title, area));
            return true;
        }, nint.Zero);
        return windows.OrderByDescending(x => x.Area).ToArray();
    }

    public int? ForegroundProcessId()
    {
        var handle = GetForegroundWindow();
        if (handle == nint.Zero)
            return null;
        GetWindowThreadProcessId(handle, out var pid);
        return (int)pid;
    }

    private static string GetTitle(nint handle)
    {
        var length = GetWindowTextLength(handle);
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern nint GetWindowLongPtr32(nint hWnd, int index);
    private static nint GetWindowLongPtr(nint hWnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, index) : GetWindowLongPtr32(hWnd, index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
}

public static class ProcessTree
{
    public static IReadOnlySet<int> DescendantsOf(int rootPid)
    {
        var parents = SnapshotParents();
        var result = new HashSet<int> { rootPid };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in parents)
            {
                if (result.Contains(pair.Value) && result.Add(pair.Key))
                    changed = true;
            }
        }
        return result;
    }

    private static Dictionary<int, int> SnapshotParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new nint(-1))
            return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return result;
            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
