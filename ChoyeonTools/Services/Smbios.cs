using System.Runtime.InteropServices;
using System.Text;

namespace ChoyeonTools.Services;

/// <summary>
/// 直接读原始 SMBIOS 表。Win32_PhysicalMemory 只给 Type 17 的字符串字段，会把数字的
/// Module Manufacturer ID 丢掉；而新主板（尤其国产内存条）经常只往 SPD 里写这个数字，
/// Manufacturer 字符串填 "Unknown"，不补这个数字就永远认不出厂商。
/// Type 4 同理：WMI 的 MaxClockSpeed 只是基础频率，加速频率只存在于固件的 Max Speed 字段里。
/// </summary>
internal static class Smbios
{
    /// <summary>'RSMB' —— GetSystemFirmwareTable 的厂商签名。</summary>
    private const uint ManufacturerSignature = 0x52534D42;

    /// <summary>Type 17 里 Module Manufacturer ID（偏移 2Ch，两个字节）与它的定位字段。</summary>
    private const int PhysicalMemoryType = 17;

    private const int ModuleManufacturerIdOffset = 0x2C;
    private const int DeviceLocatorOffset = 0x10;
    private const int BankLocatorOffset = 0x11;

    /// <summary>Type 4 里的标称频率与"插槽是否装了 CPU"。</summary>
    private const int ProcessorType = 4;

    private const int MaxSpeedOffset = 0x14;
    private const int CurrentSpeedOffset = 0x16;
    private const int StatusOffset = 0x18;
    private const int PopulatedBit = 0x40;

    private static readonly Lazy<Tables> Loaded = new(Collect);

    /// <summary>固件标称的一对频率，单位 MHz：基础频率取 Current Speed，加速频率取 Max Speed，没写就是 null。</summary>
    public sealed record ProcessorSpeeds(int? BaseMHz, int? MaxMHz);

    /// <summary>按 Type 4 出现顺序排列的标称频率，空插槽不占位。</summary>
    public static IReadOnlyList<ProcessorSpeeds> Processors => Loaded.Value.Processors;

    /// <summary>
    /// 按 "插槽|银行" 取条子的 JEDEC 数字厂商 ID，形如 "0B92"（偏移顺序的两个字节，可直接交给 JEP106 解码）。
    /// 主板没写、或写成 0000 时返回 null。
    /// </summary>
    public static string? ModuleManufacturerId(string deviceLocator, string bankLocator) =>
        Loaded.Value.ModuleIds.GetValueOrDefault($"{deviceLocator.Trim()}|{bankLocator.Trim()}");

    private sealed class Tables
    {
        public Dictionary<string, string> ModuleIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ProcessorSpeeds> Processors { get; } = [];
    }

    private static Tables Collect()
    {
        var tables = new Tables();
        byte[] data;
        try
        {
            data = ReadRawTable();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException)
        {
            return tables;
        }

        // RawSMBIOSData 头部占 8 字节：1 字节调用方式 + 3 字节保留 + 4 字节表长度，结构区从偏移 8 开始。
        for (int offset = 8; offset + 4 < data.Length; offset = NextRecord(data, offset))
        {
            int type = data[offset];
            int formattedLength = data[offset + 1];
            if (formattedLength < 4)
            {
                break;
            }

            if (type == PhysicalMemoryType && formattedLength > ModuleManufacturerIdOffset + 1)
            {
                byte continuation = data[offset + ModuleManufacturerIdOffset];
                byte code = data[offset + ModuleManufacturerIdOffset + 1];
                if (continuation != 0 || code != 0)
                {
                    string key = $"{ReadString(data, offset, formattedLength, DeviceLocatorOffset)}|{ReadString(data, offset, formattedLength, BankLocatorOffset)}";
                    tables.ModuleIds[key] = $"{continuation:X2}{code:X2}";
                }
            }
            else if (type == ProcessorType && formattedLength > StatusOffset
                     && (data[offset + StatusOffset] & PopulatedBit) != 0)
            {
                // 空插槽的 Status 没有装配位，跳过才不会把 0 频率当成一颗处理器。
                tables.Processors.Add(new ProcessorSpeeds(
                    ReadMHz(data, offset, CurrentSpeedOffset),
                    ReadMHz(data, offset, MaxSpeedOffset)));
            }

            if (type == 127)
            {
                break;
            }
        }

        return tables;
    }

    /// <summary>记录尾部的定位：无字符串时格式化区后紧跟两个 NUL，有字符串时最后一条之后还剩一个 NUL。</summary>
    private static int NextRecord(byte[] data, int offset)
    {
        int position = offset + data[offset + 1];
        if (data[position] == 0)
        {
            return position + 2;
        }

        while (position < data.Length && data[position] != 0)
        {
            position = EndOfString(data, position) + 1;
        }

        return position + 1;
    }

    private static string ReadString(byte[] data, int offset, int formattedLength, int fieldOffset)
    {
        int index = formattedLength > fieldOffset ? data[offset + fieldOffset] : 0;
        int position = offset + formattedLength;
        for (int found = 1; index >= 1 && position < data.Length && data[position] != 0; found++)
        {
            int end = EndOfString(data, position);
            if (found == index)
            {
                return Encoding.ASCII.GetString(data, position, end - position);
            }

            position = end + 1;
        }

        return string.Empty;
    }

    private static int EndOfString(byte[] data, int start)
    {
        int end = start;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return end;
    }

    private static int? ReadMHz(byte[] data, int offset, int fieldOffset)
    {
        int position = offset + fieldOffset;
        if (position + 1 >= data.Length)
        {
            return null;
        }

        ushort megahertz = BitConverter.ToUInt16(data, position);
        return megahertz > 0 ? (int?)megahertz : null;
    }

    private static byte[] ReadRawTable()
    {
        uint size = Native.GetSystemFirmwareTable(ManufacturerSignature, 0, IntPtr.Zero, 0);
        if (size < 9)
        {
            return Array.Empty<byte>();
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetSystemFirmwareTable(ManufacturerSignature, 0, buffer, size) != size)
            {
                return Array.Empty<byte>();
            }

            var data = new byte[size];
            Marshal.Copy(buffer, data, 0, (int)size);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
