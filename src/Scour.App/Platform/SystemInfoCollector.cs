using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Scour.Core.SystemInfo;
using H = Scour.Core.SystemInfo.HardwareCodes;

namespace Scour.App.Platform;

/// <summary>Builds the system information pages from WMI, the registry and .NET.</summary>
public static class SystemInfoCollector
{
    public static List<InfoSection> Collect(IProgress<string>? progress = null)
    {
        var sections = new List<InfoSection>();
        void Run(string title, string glyph, Action<InfoSection> fill)
        {
            progress?.Report(title);
            var s = new InfoSection(title, glyph);
            try { fill(s); }
            catch (Exception e) { s.Error = e.Message; }
            sections.Add(s);
        }
        Run("Overview", "", Overview);
        Run("Processor", "", Processor);
        Run("Memory", "", Memory);
        Run("Graphics", "", Graphics);
        Run("Motherboard & BIOS", "", Motherboard);
        Run("Storage", "", Storage);
        Run("Displays", "", Displays);
        Run("Network", "", Network);
        Run("Audio", "", Audio);
        Run("Battery", "", Battery);
        Run("Operating system", "", OperatingSystem);
        return sections;
    }

    // ---------------------------------------------------------------- WMI helpers

    private static List<ManagementObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            return searcher.Get().Cast<ManagementObject>().ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string S(ManagementBaseObject o, string p)
    {
        try { return o[p]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static long L(ManagementBaseObject o, string p)
    {
        try { return o[p] is { } v ? Convert.ToInt64(v) : 0; }
        catch { return 0; }
    }

    private static string Yes(bool? b) => b is null ? "" : b.Value ? "Yes" : "No";

    private static bool? B(ManagementBaseObject o, string p)
    {
        try { return o[p] is bool b ? b : null; }
        catch { return null; }
    }

    // ---------------------------------------------------------------- sections

    private static void Overview(InfoSection s)
    {
        var cs = Query("SELECT Manufacturer, Model, TotalPhysicalMemory, SystemType FROM Win32_ComputerSystem").FirstOrDefault();
        var cpu = Query("SELECT Name FROM Win32_Processor").FirstOrDefault();
        var gpus = Query("SELECT Name FROM Win32_VideoController").Select(g => S(g, "Name")).Where(n => n.Length > 0).ToList();
        var board = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var disks = Query("SELECT FriendlyName, Size FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        var g = s.Group("This PC");
        g.Add("Computer name", Environment.MachineName);
        if (cs is not null) g.Add("Manufacturer", S(cs, "Manufacturer")).Add("Model", S(cs, "Model"));
        if (cpu is not null) g.Add("Processor", S(cpu, "Name"));
        if (cs is not null) g.Add("Memory", H.Bytes(L(cs, "TotalPhysicalMemory")));
        g.Add("Graphics", string.Join("\n", gpus));
        if (board is not null) g.Add("Motherboard", $"{S(board, "Manufacturer")} {S(board, "Product")}");
        g.Add("Storage", string.Join("\n", disks.Select(d => $"{S(d, "FriendlyName")} ({H.DecimalSize(L(d, "Size"))})")));
        g.Add("Windows", OsName());
        s.Summary = cpu is null ? "" : S(cpu, "Name");
    }

    private static void Processor(InfoSection s)
    {
        foreach (var (p, i) in Query("SELECT * FROM Win32_Processor").Select((p, i) => (p, i)))
        {
            var g = s.Group(S(p, "Name"));
            g.Add("Manufacturer", S(p, "Manufacturer"))
             .Add("Cores", S(p, "NumberOfCores"))
             .Add("Logical processors (threads)", S(p, "NumberOfLogicalProcessors"))
             .Add("Base clock", H.Mhz(L(p, "MaxClockSpeed")))
             .Add("Current clock", H.Mhz(L(p, "CurrentClockSpeed")))
             .Add("L2 cache", L(p, "L2CacheSize") > 0 ? H.Bytes(L(p, "L2CacheSize") * 1024) : "")
             .Add("L3 cache", L(p, "L3CacheSize") > 0 ? H.Bytes(L(p, "L3CacheSize") * 1024) : "")
             .Add("Socket", S(p, "SocketDesignation"))
             .Add("Architecture", H.CpuArchitecture((int)L(p, "Architecture")))
             .Add("Family / model / stepping", S(p, "Caption"))
             .Add("Processor ID", S(p, "ProcessorId"))
             .Add("Virtualization enabled in firmware", Yes(B(p, "VirtualizationFirmwareEnabled")))
             .Add("Current load", L(p, "LoadPercentage") > 0 ? $"{L(p, "LoadPercentage")}%" : "");
            if (i == 0) s.Summary = $"{S(p, "NumberOfCores")} cores · {S(p, "NumberOfLogicalProcessors")} threads · {H.Mhz(L(p, "MaxClockSpeed"))}";
        }
        var cs = Query("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();
        if (cs is not null) s.Group("Virtualization").Add("Hypervisor running", Yes(B(cs, "HypervisorPresent")));
    }

    private static void Memory(InfoSection s)
    {
        var sticks = Query("SELECT * FROM Win32_PhysicalMemory");
        var array = Query("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray").FirstOrDefault();
        long total = sticks.Sum(m => L(m, "Capacity"));
        var type = sticks.Select(m => H.MemoryType((int)L(m, "SMBIOSMemoryType"))).FirstOrDefault(t => t.Length > 0) ?? "";
        var speed = sticks.Select(m => L(m, "ConfiguredClockSpeed") > 0 ? L(m, "ConfiguredClockSpeed") : L(m, "Speed")).DefaultIfEmpty(0).Max();

        var mem = new ProcessNative.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<ProcessNative.MEMORYSTATUSEX>() };
        ProcessNative.GlobalMemoryStatusEx(ref mem);
        var summary = s.Group("Summary")
            .Add("Installed", H.Bytes(total))
            .Add("Usable", H.Bytes((long)mem.ullTotalPhys))
            .Add("In use", $"{H.Bytes((long)(mem.ullTotalPhys - mem.ullAvailPhys))} ({mem.dwMemoryLoad}%)")
            .Add("Type", type)
            .Add("Speed", speed > 0 ? $"{speed} MT/s" : "");
        if (array is not null)
            summary.Add("Slots used", $"{sticks.Count} of {L(array, "MemoryDevices")}")
                   .Add("Maximum supported", L(array, "MaxCapacityEx") > 0 ? H.Bytes(L(array, "MaxCapacityEx") * 1024) : "");
        s.Summary = $"{H.Bytes(total)} {type}" + (speed > 0 ? $" @ {speed} MT/s" : "") + $" · {sticks.Count} module(s)";

        foreach (var m in sticks)
        {
            var slot = S(m, "DeviceLocator");
            s.Group(string.IsNullOrEmpty(slot) ? "Module" : slot)
             .Add("Capacity", H.Bytes(L(m, "Capacity")))
             .Add("Type", H.MemoryType((int)L(m, "SMBIOSMemoryType")))
             .Add("Form factor", H.MemoryFormFactor((int)L(m, "FormFactor")))
             .Add("Rated speed", L(m, "Speed") > 0 ? $"{L(m, "Speed")} MT/s" : "")
             .Add("Running speed", L(m, "ConfiguredClockSpeed") > 0 ? $"{L(m, "ConfiguredClockSpeed")} MT/s" : "")
             .Add("Voltage", L(m, "ConfiguredVoltage") > 0 ? $"{L(m, "ConfiguredVoltage") / 1000.0:0.00} V" : "")
             .Add("Manufacturer", S(m, "Manufacturer"))
             .Add("Part number", S(m, "PartNumber"))
             .Add("Serial number", S(m, "SerialNumber"))
             .Add("Bank", S(m, "BankLabel"));
        }
    }

    private static void Graphics(InfoSection s)
    {
        var vram = DedicatedVideoMemory();
        foreach (var v in Query("SELECT * FROM Win32_VideoController"))
        {
            var name = S(v, "Name");
            long mem = vram.TryGetValue(name, out var q) ? q : L(v, "AdapterRAM");
            var driverDate = H.WmiDate(S(v, "DriverDate"));
            var g = s.Group(name)
                .Add("Manufacturer", S(v, "AdapterCompatibility"))
                .Add("Video memory", H.Bytes(mem))
                .Add("Processor", S(v, "VideoProcessor"))
                .Add("Driver version", S(v, "DriverVersion"))
                .Add("Driver date", driverDate?.ToString("d"))
                .Add("Current mode", L(v, "CurrentHorizontalResolution") > 0
                    ? $"{L(v, "CurrentHorizontalResolution")} × {L(v, "CurrentVerticalResolution")} @ {L(v, "CurrentRefreshRate")} Hz" : "")
                .Add("Status", S(v, "Status"))
                .Add("Device ID", S(v, "PNPDeviceID"));
            if (s.Summary.Length == 0 && !name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) s.Summary = $"{name} · {H.Bytes(mem)}";
        }
    }

    /// <summary>Win32_VideoController.AdapterRAM is 32-bit (caps at 4 GB); the driver's registry key has the real size.</summary>
    private static Dictionary<string, long> DedicatedVideoMemory()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return result;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                var size = k?.GetValue("HardwareInformation.qwMemorySize");
                long bytes = size switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => k?.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    },
                };
                if (desc is not null && bytes > 0) result[desc] = bytes;
            }
        }
        catch { /* not critical */ }
        return result;
    }

    private static void Motherboard(InfoSection s)
    {
        var board = Query("SELECT * FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Query("SELECT * FROM Win32_BIOS").FirstOrDefault();
        var cs = Query("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem").FirstOrDefault();
        var chassis = Query("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();

        if (board is not null)
        {
            s.Group("Motherboard")
             .Add("Manufacturer", S(board, "Manufacturer"))
             .Add("Model", S(board, "Product"))
             .Add("Revision", S(board, "Version"))
             .Add("Serial number", S(board, "SerialNumber"));
            s.Summary = $"{S(board, "Manufacturer")} {S(board, "Product")}";
        }
        if (bios is not null)
        {
            var g = s.Group("BIOS / UEFI firmware")
                .Add("Vendor", S(bios, "Manufacturer"))
                .Add("Version", S(bios, "SMBIOSBIOSVersion"))
                .Add("Release date", H.WmiDate(S(bios, "ReleaseDate"))?.ToString("d"))
                .Add("SMBIOS version", $"{S(bios, "SMBIOSMajorVersion")}.{S(bios, "SMBIOSMinorVersion")}".Trim('.'));
            g.Add("Firmware mode", FirmwareType());
            g.Add("Secure Boot", SecureBoot());
        }
        if (cs is not null)
        {
            string type = "";
            try
            {
                if (chassis?["ChassisTypes"] is ushort[] types && types.Length > 0) type = H.Chassis(types[0]);
            }
            catch { /* optional */ }
            s.Group("System")
             .Add("Manufacturer", S(cs, "Manufacturer"))
             .Add("Model", S(cs, "Model"))
             .Add("Family", S(cs, "SystemFamily"))
             .Add("SKU", S(cs, "SystemSKUNumber"))
             .Add("Form factor", type);
        }
        var tpm = Query("SELECT * FROM Win32_Tpm", @"root\CIMV2\Security\MicrosoftTpm").FirstOrDefault();
        var tpmGroup = s.Group("Security chip (TPM)");
        if (tpm is null) tpmGroup.Add("Status", "Not found or disabled");
        else tpmGroup.Add("Version", S(tpm, "SpecVersion").Split(',')[0]).Add("Manufacturer", S(tpm, "ManufacturerIdTxt"))
                     .Add("Enabled", Yes(B(tpm, "IsEnabled_InitialValue")));
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int type);

    private static string FirmwareType()
    {
        try { return GetFirmwareType(out var t) ? t switch { 1 => "Legacy BIOS", 2 => "UEFI", _ => "" } : ""; }
        catch { return ""; }
    }

    private static string SecureBoot()
    {
        var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
        return v is int i ? (i == 1 ? "On" : "Off") : "Not supported";
    }

    private static void Storage(InfoSection s)
    {
        const string scope = @"root\Microsoft\Windows\Storage";
        var reliability = Query("SELECT * FROM MSFT_StorageReliabilityCounter", scope).ToDictionary(r => S(r, "DeviceId"), r => r);
        var disks = Query("SELECT * FROM MSFT_PhysicalDisk", scope);
        foreach (var d in disks)
        {
            var g = s.Group(S(d, "FriendlyName"))
                .Add("Capacity", H.DecimalSize(L(d, "Size")))
                .Add("Type", H.DiskMedia((int)L(d, "MediaType")))
                .Add("Interface", H.DiskBus((int)L(d, "BusType")))
                .Add("Health", H.DiskHealth((int)L(d, "HealthStatus")))
                .Add("Spindle speed", L(d, "SpindleSpeed") is > 0 and < uint.MaxValue ? $"{L(d, "SpindleSpeed")} RPM" : "")
                .Add("Firmware", S(d, "FirmwareVersion"))
                .Add("Serial number", S(d, "SerialNumber"));
            if (reliability.TryGetValue(S(d, "DeviceId"), out var r))
            {
                g.Add("Temperature", L(r, "Temperature") > 0 ? $"{L(r, "Temperature")} °C" + (L(r, "TemperatureMax") > 0 ? $" (max {L(r, "TemperatureMax")} °C)" : "") : "")
                 .Add("Wear (life used)", r["Wear"] is not null ? $"{L(r, "Wear")}%" : "")
                 .Add("Power-on hours", L(r, "PowerOnHours") > 0 ? $"{L(r, "PowerOnHours"):N0} h" : "")
                 .Add("Power cycles", L(r, "StartStopCycleCount") > 0 ? $"{L(r, "StartStopCycleCount"):N0}" : "")
                 .Add("Read errors", r["ReadErrorsTotal"] is not null ? $"{L(r, "ReadErrorsTotal")}" : "");
            }
        }
        s.Summary = string.Join(" · ", disks.Select(d => $"{H.DiskMedia((int)L(d, "MediaType"))} {H.DecimalSize(L(d, "Size"))}".Trim()));

        var vols = s.Group("Volumes");
        foreach (var v in Query("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3 OR DriveType = 2"))
        {
            var label = S(v, "VolumeName");
            vols.Add($"{S(v, "DeviceID")} {label}".Trim(),
                     $"{S(v, "FileSystem")} · {H.Bytes(L(v, "FreeSpace"))} free of {H.Bytes(L(v, "Size"))}");
        }
    }

    private static void Displays(InfoSection s)
    {
        var ids = Query("SELECT * FROM WmiMonitorID", @"root\wmi");
        var sizes = Query("SELECT * FROM WmiMonitorBasicDisplayParams", @"root\wmi").ToDictionary(p => S(p, "InstanceName"), p => p);
        foreach (var m in ids)
        {
            string Units(string prop)
            {
                try { return m[prop] is ushort[] u ? H.FromCodeUnits(u) : ""; }
                catch { return ""; }
            }
            var name = Units("UserFriendlyName");
            var g = s.Group(string.IsNullOrEmpty(name) ? "Display" : name)
                .Add("Manufacturer code", Units("ManufacturerName"))
                .Add("Product code", Units("ProductCodeID"))
                .Add("Serial number", Units("SerialNumberID"))
                .Add("Manufactured", L(m, "YearOfManufacture") > 0 ? $"Week {L(m, "WeekOfManufacture")}, {L(m, "YearOfManufacture")}" : "");
            if (sizes.TryGetValue(S(m, "InstanceName"), out var p) && L(p, "MaxHorizontalImageSize") > 0)
                g.Add("Size", $"{H.DiagonalInches(L(p, "MaxHorizontalImageSize"), L(p, "MaxVerticalImageSize"))}\" ({L(p, "MaxHorizontalImageSize")} × {L(p, "MaxVerticalImageSize")} cm)");
        }
        foreach (var v in Query("SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, CurrentBitsPerPixel FROM Win32_VideoController"))
            if (L(v, "CurrentHorizontalResolution") > 0)
                s.Group($"Output on {S(v, "Name")}")
                 .Add("Resolution", $"{L(v, "CurrentHorizontalResolution")} × {L(v, "CurrentVerticalResolution")}")
                 .Add("Refresh rate", $"{L(v, "CurrentRefreshRate")} Hz")
                 .Add("Colour depth", $"{L(v, "CurrentBitsPerPixel")}-bit");
        s.Summary = Format(ids.Count, "display");
    }

    private static void Network(InfoSection s)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up).ToList();
        foreach (var n in nics)
        {
            var props = n.GetIPProperties();
            var mac = n.GetPhysicalAddress().ToString();
            s.Group(n.Name)
             .Add("Adapter", n.Description)
             .Add("Type", n.NetworkInterfaceType.ToString())
             .Add("Status", n.OperationalStatus.ToString())
             .Add("Link speed", n.Speed > 0 && n.OperationalStatus == OperationalStatus.Up ? (n.Speed >= 1_000_000_000 ? $"{n.Speed / 1e9:0.#} Gbps" : $"{n.Speed / 1e6:0} Mbps") : "")
             .Add("MAC address", mac.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))) : mac)
             .Add("IPv4", string.Join(", ", props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address)))
             .Add("IPv6", string.Join("\n", props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.Address)))
             .Add("Gateway", string.Join(", ", props.GatewayAddresses.Select(a => a.Address)))
             .Add("DNS servers", string.Join(", ", props.DnsAddresses.Select(a => a)));
        }
        s.Summary = Format(nics.Count(n => n.OperationalStatus == OperationalStatus.Up), "connected adapter");
    }

    private static void Audio(InfoSection s)
    {
        var devices = Query("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice");
        foreach (var d in devices)
            s.Group(S(d, "Name")).Add("Manufacturer", S(d, "Manufacturer")).Add("Status", S(d, "Status"));
        s.Summary = Format(devices.Count, "audio device");
    }

    private static void Battery(InfoSection s)
    {
        var batteries = Query("SELECT * FROM Win32_Battery");
        if (batteries.Count == 0)
        {
            s.Summary = "No battery (desktop PC)";
            s.Group("Battery").Add("Status", "No battery installed");
            return;
        }
        var design = Query("SELECT DesignedCapacity FROM BatteryStaticData", @"root\wmi").Select(b => L(b, "DesignedCapacity")).FirstOrDefault();
        var full = Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi").Select(b => L(b, "FullChargedCapacity")).FirstOrDefault();
        foreach (var b in batteries)
        {
            var g = s.Group(S(b, "Name"))
                .Add("Charge", $"{L(b, "EstimatedChargeRemaining")}%")
                .Add("Status", H.BatteryStatus((int)L(b, "BatteryStatus")))
                .Add("Chemistry", L(b, "Chemistry") switch { 6 => "Lithium-ion", 8 => "Lithium polymer", 3 => "Lead acid", 4 => "Nickel cadmium", 5 => "Nickel metal hydride", _ => "" });
            if (design > 0 && full > 0)
                g.Add("Design capacity", $"{design:N0} mWh").Add("Full charge capacity", $"{full:N0} mWh").Add("Health", $"{Math.Min(100, 100.0 * full / design):0}%");
            s.Summary = $"{L(b, "EstimatedChargeRemaining")}% · {H.BatteryStatus((int)L(b, "BatteryStatus"))}";
        }
    }

    private static void OperatingSystem(InfoSection s)
    {
        var os = Query("SELECT * FROM Win32_OperatingSystem").FirstOrDefault();
        var cs = Query("SELECT Domain, PartOfDomain, Workgroup FROM Win32_ComputerSystem").FirstOrDefault();
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var ubr = Registry.GetValue(key, "UBR", null);
        var g = s.Group("Windows").Add("Edition", OsName());
        if (os is not null)
        {
            g.Add("Version", S(os, "Version") + (ubr is int u ? $".{u}" : ""))
             .Add("Architecture", S(os, "OSArchitecture"))
             .Add("Installed", H.WmiDate(S(os, "InstallDate"))?.ToString("d"))
             .Add("Last boot", H.WmiDate(S(os, "LastBootUpTime"))?.ToString("g"))
             .Add("Registered to", S(os, "RegisteredUser"))
             .Add("System drive", S(os, "SystemDrive"));
        }
        s.Group("Computer")
         .Add("Name", Environment.MachineName)
         .Add("Signed-in user", Environment.UserName)
         .Add(cs is not null && B(cs, "PartOfDomain") == true ? "Domain" : "Workgroup", cs is null ? "" : B(cs, "PartOfDomain") == true ? S(cs, "Domain") : S(cs, "Workgroup"))
         .Add("Time zone", TimeZoneInfo.Local.DisplayName)
         .Add("Locale", System.Globalization.CultureInfo.CurrentCulture.DisplayName)
         .Add(".NET runtime", RuntimeInformation.FrameworkDescription);
        s.Summary = OsName();
    }

    private static string OsName()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Registry.GetValue(key, "ProductName", "Windows") as string ?? "Windows";
        var display = Registry.GetValue(key, "DisplayVersion", "") as string ?? "";
        if (Environment.OSVersion.Version.Build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        return string.IsNullOrEmpty(display) ? product : $"{product} {display}";
    }

    private static string Format(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}
