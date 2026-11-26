using System.Runtime.InteropServices;
using System.Text;

namespace VoxStride.Windows;

internal static partial class Native
{
    public const uint WM_SETTEXT = 0x000C, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_COMMAND = 0x0111;
    public const uint SMTO_ABORTIFHUNG = 0x0002, SMTO_BLOCK = 0x0001;
    public const int GW_OWNER = 4;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc cb, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumChildWindows(nint parent, EnumWindowsProc cb, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hwnd, [Out] char[] buf, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(nint hwnd, [Out] char[] buf, int max);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static partial nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageTimeoutString(nint hwnd, uint msg, nint wParam, string lParam, uint flags, uint timeoutMs, out nint result);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static unsafe partial nint SendMessageTimeoutBuffer(nint hwnd, uint msg, nint wParam, char* lParam, uint flags, uint timeoutMs, out nint result);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, nint token, out nint path);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint p);

    // ---- Credential Manager ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    public const uint CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredWrite(ref CREDENTIAL cred, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredRead(string target, uint type, uint flags, out nint cred);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    public static extern void CredFree(nint cred);

    // ---- Shell file operation (Recycle Bin) ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }
    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_NOCONFIRMMKDIR = 0x200;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    // ---- Sound ----
    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PlaySound(byte[]? sound, nint hmod, uint flags);
    public const uint SND_ASYNC = 0x1, SND_MEMORY = 0x4, SND_NODEFAULT = 0x2, SND_PURGE = 0x40;

    public static string ClassName(nint hwnd)
    {
        var b = new char[256];
        var n = GetClassName(hwnd, b, b.Length);
        return n > 0 ? new string(b, 0, n) : "";
    }

    public static string Title(nint hwnd)
    {
        var b = new char[512];
        var n = GetWindowText(hwnd, b, b.Length);
        return n > 0 ? new string(b, 0, n) : "";
    }

    public static List<nint> TopLevelWindows(Func<nint, bool>? filter = null)
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { if (filter is null || filter(h)) list.Add(h); return true; }, 0);
        return list;
    }

    public static List<nint> ChildWindows(nint parent)
    {
        var list = new List<nint>();
        EnumChildWindows(parent, (h, _) => { list.Add(h); return true; }, 0);
        return list;
    }

    public static uint ProcessIdOf(nint hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid; }

    /// <summary>Reads window text via WM_GETTEXT (marshaled cross-process by Windows). Null if the window is hung/inaccessible.</summary>
    public static unsafe string? ReadText(nint hwnd, uint timeoutMs = 3000)
    {
        if (SendMessageTimeout(hwnd, WM_GETTEXTLENGTH, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out var len) == 0) return null;
        var n = (int)len;
        if (n < 0 || n > 4_000_000) return null;
        var buf = new char[n + 1];
        fixed (char* p = buf)
        {
            if (SendMessageTimeoutBuffer(hwnd, WM_GETTEXT, n + 1, p, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out var copied) == 0) return null;
            return new string(buf, 0, (int)Math.Min(copied, n));
        }
    }

    public static bool SetText(nint hwnd, string text, uint timeoutMs = 5000) =>
        SendMessageTimeoutString(hwnd, WM_SETTEXT, 0, text, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out var r) != 0 && r != 0;

    public static bool PostCommand(nint hwnd, int id, uint timeoutMs = 3000) =>
        SendMessageTimeout(hwnd, WM_COMMAND, id, 0, SMTO_ABORTIFHUNG, timeoutMs, out _) != 0;
}
