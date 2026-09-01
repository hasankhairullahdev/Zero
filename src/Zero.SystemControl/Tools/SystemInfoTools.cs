using System.ComponentModel;
using LibreHardwareMonitor.Hardware;
using ModelContextProtocol.Server;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class SystemInfoTools
{
    // ─── get_cpu_usage ────────────────────────────────────────────────────────

    [McpServerTool, Description("Get current CPU usage percentage.")]
    public static string get_cpu_usage()
    {
        var computer = new Computer { IsCpuEnabled = true };
        computer.Open();
        computer.Accept(new SensorVisitor());
        try
        {
            foreach (var hw in computer.Hardware)
            {
                hw.Update();
                foreach (var sensor in hw.Sensors)
                    if (sensor.SensorType == SensorType.Load && sensor.Name == "CPU Total")
                        return $"CPU Usage: {sensor.Value:F1}%";
            }
            return "CPU usage: unavailable";
        }
        finally { computer.Close(); }
    }

    // ─── get_ram_usage ────────────────────────────────────────────────────────

    [McpServerTool, Description("Get current RAM usage (used / total in GB).")]
    public static string get_ram_usage()
    {
        var computer = new Computer { IsMemoryEnabled = true };
        computer.Open();
        computer.Accept(new SensorVisitor());
        float used = 0, available = 0;
        try
        {
            foreach (var hw in computer.Hardware)
            {
                hw.Update();
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType == SensorType.Data && sensor.Name == "Memory Used")
                        used = sensor.Value ?? 0;
                    if (sensor.SensorType == SensorType.Data && sensor.Name == "Memory Available")
                        available = sensor.Value ?? 0;
                }
            }
        }
        finally { computer.Close(); }

        var total = used + available;
        var pct   = total > 0 ? (int)(used / total * 100) : 0;
        return $"RAM: {used:F1} GB used / {total:F1} GB total ({pct}%)";
    }

    // ─── get_battery_status ───────────────────────────────────────────────────

    [McpServerTool, Description("Get battery percentage and charging status.")]
    public static string get_battery_status()
    {
        var status = System.Windows.Forms.SystemInformation.PowerStatus;
        var pct    = (int)(status.BatteryLifePercent * 100);
        var state  = status.BatteryChargeStatus;

        var charging = state.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging)
            ? "Charging" : "Discharging";

        return $"Battery: {pct}% — {charging}";
    }

    // ─── get_disk_usage ───────────────────────────────────────────────────────

    [McpServerTool, Description("Get disk usage for all drives.")]
    public static string get_disk_usage()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var total = drive.TotalSize / 1_073_741_824.0;
            var free  = drive.AvailableFreeSpace / 1_073_741_824.0;
            var used  = total - free;
            var pct   = (int)(used / total * 100);
            sb.AppendLine($"{drive.Name}  {used:F1} GB used / {total:F1} GB total ({pct}%) — free: {free:F1} GB");
        }
        return sb.Length == 0 ? "No drives found." : sb.ToString();
    }
}

/// <summary>Satisfy IVisitor contract required by LibreHardwareMonitor.</summary>
internal sealed class SensorVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
