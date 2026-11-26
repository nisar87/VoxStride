using System.Runtime.InteropServices;
using System.Text;
using VoxStride.Core.Files;

namespace VoxStride.Windows;

/// <summary>Known folders via SHGetKnownFolderPath (handles OneDrive/redirected Desktop and Documents).</summary>
public sealed class WindowsKnownFolders : IKnownFolders
{
    private static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    private static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    public string? Resolve(KnownFolder folder) => folder switch
    {
        KnownFolder.Desktop => Get(Desktop),
        KnownFolder.Documents => Get(Documents),
        KnownFolder.Downloads => Get(Downloads),
        KnownFolder.Pictures => Get(Pictures),
        KnownFolder.Music => Get(Music),
        KnownFolder.Videos => Get(Videos),
        KnownFolder.Temp => Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'),
        _ => null,
    };

    private static string? Get(Guid id)
    {
        var hr = Native.SHGetKnownFolderPath(id, 0, 0, out var p);
        try { return hr == 0 ? Marshal.PtrToStringUni(p) : null; }
        finally { if (p != 0) Native.CoTaskMemFree(p); }
    }
}

/// <summary>API keys in Windows Credential Manager (per-user, DPAPI-protected by Windows). Never logged.</summary>
public sealed class CredentialSecretStore(string prefix = "VoxStride/")
{
    public void Save(string name, string secret)
    {
        var target = prefix + name;
        var blob = Encoding.Unicode.GetBytes(secret);
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(Environment.UserName);
        var blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new Native.CREDENTIAL
            {
                Type = Native.CRED_TYPE_GENERIC, TargetName = targetPtr, CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr, Persist = Native.CRED_PERSIST_LOCAL_MACHINE, UserName = userPtr,
            };
            if (!Native.CredWrite(ref cred, 0)) throw new InvalidOperationException("Could not save the key to Windows Credential Manager (error " + Marshal.GetLastWin32Error() + ").");
        }
        finally
        {
            Array.Clear(blob);
            for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
            Marshal.FreeCoTaskMem(blobPtr); Marshal.FreeCoTaskMem(targetPtr); Marshal.FreeCoTaskMem(userPtr);
        }
    }

    public string? Load(string name)
    {
        if (!Native.CredRead(prefix + name, Native.CRED_TYPE_GENERIC, 0, out var p)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Native.CREDENTIAL>(p);
            if (cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var s = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return s;
        }
        finally { Native.CredFree(p); }
    }

    public bool Delete(string name) => Native.CredDelete(prefix + name, Native.CRED_TYPE_GENERIC, 0);
}

/// <summary>Removes files permanently or via the Recycle Bin (SHFileOperation with FOF_ALLOWUNDO), silently and without UI.</summary>
public sealed class WindowsFileRemover : IFileRemover
{
    public bool SupportsRecycle => true;

    public void Remove(string path, bool recycle)
    {
        if (!recycle) { File.Delete(path); return; }
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI),
        };
        var rc = Native.SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted) throw new IOException($"Recycle Bin operation failed (0x{rc:X}).");
    }
}
