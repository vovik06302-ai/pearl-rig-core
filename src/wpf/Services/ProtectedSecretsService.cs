using System.Runtime.InteropServices;
using System.Text;

namespace PearlRigCore.Services;

/// <summary>Encrypts and decrypts local user secrets using native Windows DPAPI (crypt32.dll, CurrentUser scope).</summary>
public static class ProtectedSecretsService
{
    private const string Prefix = "dpapi:v1:";
    private const uint CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return string.Empty;
        if (IsProtected(plainText))
            return plainText;
        if (!OperatingSystem.IsWindows())
            return plainText;

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        var pinnedHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = (uint)bytes.Length;
            inBlob.pbData = pinnedHandle.AddrOfPinnedObject();
            if (!CryptProtectData(ref inBlob, "PearlRigControl", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out outBlob))
                throw new InvalidOperationException($"DPAPI CryptProtectData failed with error {Marshal.GetLastWin32Error()}.");

            var protectedBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, protectedBytes, 0, protectedBytes.Length);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            if (pinnedHandle.IsAllocated) pinnedHandle.Free();
            if (outBlob.pbData != IntPtr.Zero) _ = LocalFree(outBlob.pbData);
        }
    }

    public static string Unprotect(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
            return string.Empty;
        if (!IsProtected(storedValue))
            return storedValue;
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(storedValue[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        var pinnedHandle = GCHandle.Alloc(encryptedBytes, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = (uint)encryptedBytes.Length;
            inBlob.pbData = pinnedHandle.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out outBlob))
                return string.Empty;

            var plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, plainBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            if (pinnedHandle.IsAllocated) pinnedHandle.Free();
            if (outBlob.pbData != IntPtr.Zero) _ = LocalFree(outBlob.pbData);
        }
    }
}
