using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

sealed class Hardware : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Power
    {
        public byte AC, Flags, Percent, Saver;
        public uint Seconds, FullSeconds;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public uint Size, Load;
        public ulong Total, Available, Page, FreePage, Virtual, FreeVirtual, Extended;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInterface
    {
        public int Size;
        public Guid Class;
        public int Flags;
        public IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct CounterValue
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }
    public sealed class Battery
    {
        public double? RemainingWh, FullWh, DesignWh, Wear, Watts, ChargeWatts;
    }
    sealed class Pack
    {
        public SafeFileHandle Handle;
        public int Tag;
        public uint Design, Full;
        public bool Relative;
    }
    [DllImport("kernel32")] public static extern bool GetSystemPowerStatus(out Power status);
    [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref Memory memory);
    [DllImport("kernel32")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("powrprof")] static extern uint CallNtPowerInformation(int level, IntPtr input, int inputSize, byte[] output, int size);
    [DllImport("setupapi", CharSet=CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid id, string enumerator, IntPtr parent, int flags);
    [DllImport("setupapi")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid id, uint index, ref DeviceInterface data);
    [DllImport("setupapi", CharSet=CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface data, IntPtr detail, int size, out int required, IntPtr device);
    [DllImport("setupapi")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32")] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, int[] input, int inputSize, byte[] output, int outputSize, out int written, IntPtr overlapped);
    [DllImport("pdh", CharSet=CharSet.Unicode)] static extern uint PdhOpenQuery(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh", CharSet=CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh", CharSet=CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh")] static extern uint PdhCloseQuery(IntPtr query);

    readonly List<Pack> packs = new List<Pack>();
    readonly byte[] composite = new byte[32];
    readonly byte[] packStatus = new byte[16];
    long previousIdle, previousTotal;
    DateTime nextBatteryDiscovery, nextGpuAttempt;
    IntPtr gpuQuery, gpuCounter;
    bool gpuPrimed;

    public static Memory ReadMemory()
    {
        Memory memory = new Memory { Size = (uint)Marshal.SizeOf(typeof(Memory)) };
        GlobalMemoryStatusEx(ref memory);
        return memory;
    }
    public double? Cpu()
    {
        long idle, kernel, user;
        if (!GetSystemTimes(out idle, out kernel, out user)) return null;
        long total = kernel + user, elapsed = total - previousTotal;
        double? usage = previousTotal == 0 || elapsed <= 0 ? (double?)null
            : Math.Max(0, Math.Min(100, 100.0 * (elapsed - idle + previousIdle) / elapsed));
        previousIdle = idle;
        previousTotal = total;
        return usage;
    }
    public void StopGpu()
    {
        if (gpuQuery != IntPtr.Zero) PdhCloseQuery(gpuQuery);
        gpuQuery = gpuCounter = IntPtr.Zero;
        gpuPrimed = false;
        nextGpuAttempt = DateTime.MinValue;
    }
    public double? Gpu()
    {
        if (gpuCounter == IntPtr.Zero)
        {
            if (DateTime.UtcNow < nextGpuAttempt) return null;
            if (PdhOpenQuery(null, IntPtr.Zero, out gpuQuery) != 0 ||
                PdhAddEnglishCounter(gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuCounter) != 0)
            {
                StopGpu();
                nextGpuAttempt = DateTime.UtcNow.AddMinutes(1);
                return null;
            }
        }
        if (PdhCollectQueryData(gpuQuery) != 0) return null;
        if (!gpuPrimed) { gpuPrimed = true; return null; }
        uint bytes = 0, count;
        uint result = PdhGetFormattedCounterArray(gpuCounter, 0x200, ref bytes, out count, IntPtr.Zero);
        if (result != 0x800007D2 || bytes == 0 || bytes > 16 * 1024 * 1024) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(gpuCounter, 0x200, ref bytes, out count, buffer) != 0) return null;
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int size = Marshal.SizeOf(typeof(CounterValue));
            double busiest = 0;
            for (int i = 0; i < count; i++)
            {
                var item = (CounterValue)Marshal.PtrToStructure(IntPtr.Add(buffer, i * size), typeof(CounterValue));
                if (item.Status > 1 || double.IsNaN(item.Value)) continue;
                string name = Marshal.PtrToStringUni(item.Name);
                if (name == null) continue;
                int start = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                string engine = name.Substring(start);
                int duplicate = engine.IndexOf('#');
                if (duplicate >= 0) engine = engine.Substring(0, duplicate);
                double accumulated;
                engines.TryGetValue(engine, out accumulated);
                accumulated += Math.Max(0, item.Value);
                engines[engine] = accumulated;
                busiest = Math.Max(busiest, accumulated);
            }
            return engines.Count == 0 ? (double?)null : Math.Min(100, busiest);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    static bool Query(SafeFileHandle handle, uint code, int[] request, byte[] response)
    {
        int written;
        return DeviceIoControl(handle, code, request, request.Length * 4, response, response.Length, out written, IntPtr.Zero)
            && written >= response.Length;
    }
    void FindPacks()
    {
        foreach (var pack in packs) pack.Handle.Dispose();
        packs.Clear();
        nextBatteryDiscovery = DateTime.UtcNow.AddMinutes(5);
        Guid id = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
        IntPtr set = SetupDiGetClassDevs(ref id, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInterface { Size = Marshal.SizeOf(typeof(DeviceInterface)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref id, index, ref data)) break;
                int required;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required < 8) continue;
                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); continue; }
                    byte[] tag = new byte[4], info = new byte[36];
                    if (!Query(handle, 0x294040, new[] { 0 }, tag) || BitConverter.ToInt32(tag, 0) == 0 ||
                        !Query(handle, 0x294044, new[] { BitConverter.ToInt32(tag, 0), 0, 0 }, info) ||
                        (BitConverter.ToUInt32(info, 0) & 0x80000000) == 0)
                    { handle.Dispose(); continue; }
                    packs.Add(new Pack { Handle = handle, Tag = BitConverter.ToInt32(tag, 0),
                        Design = BitConverter.ToUInt32(info, 12), Full = BitConverter.ToUInt32(info, 16),
                        Relative = (BitConverter.ToUInt32(info, 0) & 0x40000000) != 0 });
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    static bool Capacity(uint value) { return value > 0 && value < int.MaxValue; }
    public Battery ReadBattery(bool hasBattery, bool discharging, bool plugged = false)
    {
        var battery = new Battery();
        if (!hasBattery) return battery;
        if (DateTime.UtcNow >= nextBatteryDiscovery) FindPacks();
        double design = 0, full = 0, remaining = 0, watts = 0;
        bool healthKnown = packs.Count > 0, absolute = healthKnown, capacityKnown = healthKnown, rateKnown = healthKnown;
        foreach (var pack in packs)
        {
            absolute &= !pack.Relative;
            healthKnown &= Capacity(pack.Design) && Capacity(pack.Full);
            design += pack.Design;
            full += pack.Full;
            if (!Query(pack.Handle, 0x29404C, new[] { pack.Tag, 0, 0, 0, -1 }, packStatus))
            {
                capacityKnown = rateKnown = false;
                if (nextBatteryDiscovery > DateTime.UtcNow.AddSeconds(30)) nextBatteryDiscovery = DateTime.UtcNow.AddSeconds(30);
                continue;
            }
            uint capacity = BitConverter.ToUInt32(packStatus, 4);
            int rate = BitConverter.ToInt32(packStatus, 12);
            capacityKnown &= capacity < int.MaxValue;
            rateKnown &= rate != int.MinValue;
            remaining += capacity;
            watts -= rate / 1000.0;
        }
        if (healthKnown && (absolute || packs.Count == 1)) battery.Wear = Math.Max(0, 100 * (1 - full / design));
        if (healthKnown && absolute) { battery.DesignWh = design / 1000; battery.FullWh = full / 1000; }
        if (capacityKnown && absolute) battery.RemainingWh = remaining / 1000;
        if (rateKnown && absolute && discharging && watts > 0) battery.Watts = watts;
        if (rateKnown && absolute && plugged && watts < 0) battery.ChargeWatts = -watts;
        if ((!battery.RemainingWh.HasValue || !battery.FullWh.HasValue ||
             (discharging && !battery.Watts.HasValue) || (plugged && !battery.ChargeWatts.HasValue)) && (packs.Count == 0 || absolute) &&
            CallNtPowerInformation(5, IntPtr.Zero, 0, composite, composite.Length) == 0 && composite[1] != 0)
        {
            uint max = BitConverter.ToUInt32(composite, 8), left = BitConverter.ToUInt32(composite, 12);
            int rate = BitConverter.ToInt32(composite, 16);
            if (Capacity(max) && left <= max)
            {
                if (!battery.FullWh.HasValue) battery.FullWh = max / 1000.0;
                if (!battery.RemainingWh.HasValue) battery.RemainingWh = left / 1000.0;
            }
            if (discharging && composite[3] != 0 && rate < 0 && rate != int.MinValue && !battery.Watts.HasValue)
                battery.Watts = -rate / 1000.0;
            if (plugged && composite[2] != 0 && rate > 0 && !battery.ChargeWatts.HasValue)
                battery.ChargeWatts = rate / 1000.0;
        }
        return battery;
    }
    public void Dispose()
    {
        StopGpu();
        foreach (var pack in packs) pack.Handle.Dispose();
    }
}
