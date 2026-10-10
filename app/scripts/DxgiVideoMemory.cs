using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace SLNG.App;

/// <summary>
/// Probes Windows DXGI adapter for process video memory budget and usage.
/// Uses COM vtable method calls via Marshal (no unsafe blocks, no new NuGet packages).
/// </summary>
public sealed class DxgiVideoMemory : IDisposable
{
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0;

    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr thisPtr, uint index, out IntPtr adapter1);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr thisPtr, out DXGI_ADAPTER_DESC1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfoDelegate(IntPtr thisPtr, uint nodeIndex, int memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO videoMemoryInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    private IntPtr _adapter3Ptr;
    private readonly QueryVideoMemoryInfoDelegate _queryVideoMemoryInfo;
    private readonly ReleaseDelegate _release;
    private bool _disposed;
    private bool _loggedError;

    private DxgiVideoMemory(IntPtr adapter3Ptr, QueryVideoMemoryInfoDelegate queryDelegate, ReleaseDelegate releaseDelegate)
    {
        _adapter3Ptr = adapter3Ptr;
        _queryVideoMemoryInfo = queryDelegate;
        _release = releaseDelegate;
    }

    private static uint ReleaseCom(IntPtr ptr, ReleaseDelegate? release = null)
    {
        if (ptr == IntPtr.Zero) return 0;
        try
        {
            if (release != null) return release(ptr);
            IntPtr vtbl = Marshal.ReadIntPtr(ptr);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size);
            var del = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(fn);
            return del(ptr);
        }
        catch
        {
            return 0;
        }
    }

    public static DxgiVideoMemory? TryCreate(string godotAdapterName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr factoryPtr = IntPtr.Zero;
        var candidates = new List<(IntPtr ptr, DXGI_ADAPTER_DESC1 desc)>();

        try
        {
            Guid factoryGuid = Guid.Parse("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            int hr = CreateDXGIFactory1(ref factoryGuid, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                GD.PrintErr($"[VramBudget] CreateDXGIFactory1 failed: hr=0x{hr:X8}");
                return null;
            }

            IntPtr factoryVtbl = Marshal.ReadIntPtr(factoryPtr);
            var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(
                Marshal.ReadIntPtr(factoryVtbl, 12 * IntPtr.Size));
            var releaseFactory = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(factoryVtbl, 2 * IntPtr.Size));

            uint index = 0;
            while (true)
            {
                int enumHr = enumAdapters(factoryPtr, index, out IntPtr adapterPtr);
                if (enumHr == DXGI_ERROR_NOT_FOUND)
                {
                    break;
                }
                if (enumHr < 0 || adapterPtr == IntPtr.Zero)
                {
                    break;
                }

                index++;

                IntPtr adapterVtbl = Marshal.ReadIntPtr(adapterPtr);
                var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(
                    Marshal.ReadIntPtr(adapterVtbl, 10 * IntPtr.Size));
                int descHr = getDesc(adapterPtr, out DXGI_ADAPTER_DESC1 desc);
                if (descHr < 0 || (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                {
                    ReleaseCom(adapterPtr);
                    continue;
                }

                candidates.Add((adapterPtr, desc));
            }

            releaseFactory(factoryPtr);
            factoryPtr = IntPtr.Zero;

            if (candidates.Count == 0)
            {
                GD.PrintErr("[VramBudget] No hardware DXGI adapters found.");
                return null;
            }

            // Adapter choice:
            // 2. Godot renders through Vulkan, so match by name: take RenderingServer.GetVideoAdapterName().
            // Pick adapter whose Description equals case-insensitively, or contains or is contained in it.
            // 3. No match -> adapter with largest DedicatedVideoMemory.
            int chosenIndex = -1;
            string matchType = "name";

            if (!string.IsNullOrWhiteSpace(godotAdapterName))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    string descName = candidates[i].desc.Description ?? "";
                    if (descName.Equals(godotAdapterName, StringComparison.OrdinalIgnoreCase))
                    {
                        chosenIndex = i;
                        break;
                    }
                }

                if (chosenIndex < 0)
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        string descName = candidates[i].desc.Description ?? "";
                        if (descName.IndexOf(godotAdapterName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            godotAdapterName.IndexOf(descName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            chosenIndex = i;
                            break;
                        }
                    }
                }
            }

            if (chosenIndex < 0)
            {
                matchType = "largest";
                chosenIndex = 0;
                ulong largestDedicated = (ulong)candidates[0].desc.DedicatedVideoMemory;
                for (int i = 1; i < candidates.Count; i++)
                {
                    ulong dedicated = (ulong)candidates[i].desc.DedicatedVideoMemory;
                    if (dedicated > largestDedicated)
                    {
                        largestDedicated = dedicated;
                        chosenIndex = i;
                    }
                }
            }

            var chosen = candidates[chosenIndex];

            // Release all non-chosen candidates
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i != chosenIndex)
                {
                    ReleaseCom(candidates[i].ptr);
                }
            }
            candidates.Clear();

            // 4. QueryInterface the chosen adapter to IDXGIAdapter3
            Guid adapter3Guid = Guid.Parse("645967a4-1392-4310-a798-8053ce3e93fd");
            IntPtr chosenVtbl = Marshal.ReadIntPtr(chosen.ptr);
            var queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                Marshal.ReadIntPtr(chosenVtbl, 0 * IntPtr.Size));

            int qiHr = queryInterface(chosen.ptr, ref adapter3Guid, out IntPtr adapter3Ptr);
            ReleaseCom(chosen.ptr);

            if (qiHr < 0 || adapter3Ptr == IntPtr.Zero)
            {
                GD.PrintErr($"[VramBudget] QueryInterface IDXGIAdapter3 failed: hr=0x{qiHr:X8}");
                return null;
            }

            IntPtr adapter3Vtbl = Marshal.ReadIntPtr(adapter3Ptr);
            var queryVideoMemoryInfo = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(
                Marshal.ReadIntPtr(adapter3Vtbl, 14 * IntPtr.Size));
            var releaseAdapter3 = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(adapter3Vtbl, 2 * IntPtr.Size));

            // Test query
            int qvHr = queryVideoMemoryInfo(adapter3Ptr, 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
            if (qvHr < 0)
            {
                releaseAdapter3(adapter3Ptr);
                GD.PrintErr($"[VramBudget] QueryVideoMemoryInfo failed: hr=0x{qvHr:X8}");
                return null;
            }

            ulong dedicatedMb = ((ulong)chosen.desc.DedicatedVideoMemory) >> 20;
            ulong osBudgetMb = info.Budget >> 20;
            ulong osUsageMb = info.CurrentUsage >> 20;

            GD.Print($"[VramBudget] adapter=\"{chosen.desc.Description}\" dedicated={dedicatedMb} MB matched={matchType} osBudget={osBudgetMb} osUsage={osUsageMb}");

            return new DxgiVideoMemory(adapter3Ptr, queryVideoMemoryInfo, releaseAdapter3);
        }
        catch (Exception ex)
        {
            if (factoryPtr != IntPtr.Zero)
            {
                ReleaseCom(factoryPtr);
            }
            foreach (var c in candidates)
            {
                ReleaseCom(c.ptr);
            }
            GD.PrintErr($"[VramBudget] DXGI initialization failed: {ex.Message}");
            return null;
        }
    }

    public bool TryQuery(out long budgetBytes, out long usageBytes)
    {
        budgetBytes = 0;
        usageBytes = 0;
        if (_disposed || _adapter3Ptr == IntPtr.Zero) return false;

        try
        {
            int hr = _queryVideoMemoryInfo(_adapter3Ptr, 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
            if (hr < 0)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    GD.PrintErr($"[VramBudget] QueryVideoMemoryInfo failed: hr=0x{hr:X8}");
                }
                return false;
            }
            budgetBytes = unchecked((long)info.Budget);
            usageBytes = unchecked((long)info.CurrentUsage);
            return true;
        }
        catch (Exception ex)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                GD.PrintErr($"[VramBudget] QueryVideoMemoryInfo exception: {ex.Message}");
            }
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_adapter3Ptr != IntPtr.Zero)
        {
            try
            {
                _release(_adapter3Ptr);
            }
            catch { }
            _adapter3Ptr = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}
