using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PromptVault.App.Services;

public static class WindowsCredentialStore
{
    public const string OnlineAiTarget = "FR_Imageprompt/OnlineAi";

    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;

    public static bool HasOnlineAiKey() => TryReadOnlineAiKey(out _);

    public static bool TryReadOnlineAiKey(out string key)
    {
        key = "";
        if (!CredRead(OnlineAiTarget, GenericCredential, 0, out var pointer)) return false;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return false;
            key = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char))) ?? "";
            return !string.IsNullOrWhiteSpace(key);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void SaveOnlineAiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("在线 AI 密钥不能为空。", nameof(key));

        var bytes = Encoding.Unicode.GetBytes(key.Trim());
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = OnlineAiTarget,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "FR_Imageprompt"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将在线 AI 密钥保存到 Windows 凭据管理器。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(blob);
        }
    }

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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
