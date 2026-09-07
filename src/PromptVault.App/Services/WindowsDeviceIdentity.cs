using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PromptVault.Licensing;

namespace PromptVault.App.Services;

internal static class WindowsDeviceIdentity
{
    public static string GetRequestCode()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        var machineGuid = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
            throw new InvalidOperationException("无法读取 Windows 设备标识，请使用管理员检查系统注册表后重试。");

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(systemRoot)
            || !GetVolumeInformation(
                systemRoot,
                null,
                0,
                out var serial,
                out _,
                out _,
                null,
                0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 系统卷标识。");

        return DeviceRequestCode.Create(machineGuid, serial);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}
