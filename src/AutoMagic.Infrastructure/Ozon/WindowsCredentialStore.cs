using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoMagic.Infrastructure.Ozon;

public interface IWindowsCredentialStore
{
    string? Read(string targetName);

    void Write(string targetName, string secret);

    void Delete(string targetName);
}

public sealed class WindowsCredentialStore : IWindowsCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumSecretCharacters = 2_048;

    public string? Read(string targetName)
    {
        EnsureWindows();
        ValidateTargetName(targetName);
        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "读取 Windows Credential Manager 失败。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string targetName, string secret)
    {
        EnsureWindows();
        ValidateTargetName(targetName);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length > MaximumSecretCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                $"凭证长度不能超过 {MaximumSecretCharacters} 个字符。");
        }

        if (secret.Length == 0)
        {
            Delete(targetName);
            return;
        }

        var secretPointer = Marshal.StringToHGlobalUni(secret);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)Encoding.Unicode.GetByteCount(secret)),
                CredentialBlob = secretPointer,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "保存 Windows Credential Manager 凭证失败。");
            }
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(secretPointer);
        }
    }

    public void Delete(string targetName)
    {
        EnsureWindows();
        ValidateTargetName(targetName);
        if (CredDelete(targetName, CredTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "删除 Windows Credential Manager 凭证失败。");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager 仅支持 Windows。");
        }
    }

    private static void ValidateTargetName(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            throw new ArgumentException("凭证目标名称不能为空。", nameof(targetName));
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        int reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
