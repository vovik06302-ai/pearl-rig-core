using System.Runtime.InteropServices;

namespace PearlRigCore.Services;

public static class ComputerInfoClient
{
    public static string GetSummary()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var ram = GlobalMemoryStatusEx(ref memory) ? $"RAM {memory.TotalPhysical / (1024d * 1024 * 1024):0.#} ГБ" : "RAM —";
        var architecture = RuntimeInformation.OSArchitecture == Architecture.X64 ? "Windows x64" : $"Windows {RuntimeInformation.OSArchitecture}";
        return $"{Environment.MachineName} · {architecture} · CPU-потоков {Environment.ProcessorCount} · {ram}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
