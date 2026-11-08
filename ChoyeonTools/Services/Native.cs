using System.Runtime.InteropServices;

namespace ChoyeonTools.Services;

/// <summary>Win32 采集所需的 P/Invoke 声明。</summary>
internal static class Native
{
    // ---- CPU / 内存 ----

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MEMORYSTATUSEX Current()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref status);
            return status;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryKilobytes);

    /// <summary>0 未知 / 1 Legacy BIOS / 2 UEFI。Windows 8 起提供。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFirmwareType(out uint firmwareType);

    /// <summary>
    /// 读原始固件表，签名 'RSMB' 取 SMBIOS。返回缓冲区头 8 字节之后才是结构区。
    /// Win32_PhysicalMemory 会丢掉 Type 17 的数字厂商 ID，只能从这张原始表补。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetSystemFirmwareTable(uint providerSignature, uint tableId, IntPtr buffer, uint bufferSize);

    // ---- 显示器 ----

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>EnumDisplayMonitors 回调收集的显示器几何信息。</summary>
    public sealed class MonitorGeometry
    {
        public required string DeviceName { get; init; }
        public required Rect Bounds { get; init; }
        public required Rect WorkArea { get; init; }
        public bool IsPrimary { get; init; }
        public uint DpiX { get; set; } = 96;
        public uint DpiY { get; set; } = 96;
    }

    /// <summary>枚举所有显示器的边界、工作区与缩放比例。</summary>
    public static List<MonitorGeometry> EnumerateMonitors()
    {
        var result = new List<MonitorGeometry>();

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref Rect rect, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                if (GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out uint dpiY) != 0)
                {
                    dpiX = 96;
                    dpiY = 96;
                }

                result.Add(new MonitorGeometry
                {
                    DeviceName = info.szDevice,
                    Bounds = rect,
                    WorkArea = info.rcWork,
                    IsPrimary = (info.dwFlags & 1) != 0,
                    DpiX = dpiX,
                    DpiY = dpiY,
                });
            }

            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result;
    }
}
