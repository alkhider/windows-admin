using System.Text;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class HealthReportFormatter
{
    public static string ToHtml(HealthReport report)
    {
        var findings = string.Join("", report.Findings.Select(f => $"<li>{System.Net.WebUtility.HtmlEncode(f)}</li>"));
        var drives = string.Join("", report.Drives.Select(d =>
            $"<tr><td>{d.Name} {System.Net.WebUtility.HtmlEncode(d.Label)}</td><td>{d.UsedPercent}%</td><td>{StorageService.FormatBytes(d.FreeBytes)} free</td></tr>"));
        var errors = string.Join("", report.RecentErrors.Select(e =>
            $"<tr><td>{e.Time:g}</td><td>{System.Net.WebUtility.HtmlEncode(e.Source)}</td><td>{System.Net.WebUtility.HtmlEncode(e.Message)}</td></tr>"));

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8" />
              <title>Windows health report - {{report.ComputerName}}</title>
              <style>
                body { font-family: "Segoe UI", sans-serif; background: #f4f6f8; color: #1b1f24; margin: 32px; }
                .card { background: white; border-radius: 16px; padding: 24px; margin-bottom: 20px; box-shadow: 0 8px 24px rgba(0,0,0,.06); }
                h1 { margin: 0 0 8px; }
                table { width: 100%; border-collapse: collapse; }
                td, th { text-align: left; padding: 8px 6px; border-bottom: 1px solid #e6ebf0; }
                .score { font-size: 48px; font-weight: 700; color: #0f7b6c; }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>Windows health report</h1>
                <div>{{report.ComputerName}} · {{report.GeneratedAt:yyyy-MM-dd HH:mm}}</div>
                <div class="score">{{report.HealthScore}} · {{report.HealthLabel}}</div>
              </div>
              <div class="card">
                <h2>System</h2>
                <p>{{System.Net.WebUtility.HtmlEncode(report.Os.Caption)}} {{report.Os.Architecture}} (build {{report.Os.Build}})</p>
                <p>{{System.Net.WebUtility.HtmlEncode(report.Hardware.Manufacturer)}} {{System.Net.WebUtility.HtmlEncode(report.Hardware.Model)}}</p>
                <p>CPU {{report.Cpu.LoadPercent}}% · Memory {{report.Memory.UsedPercent}}% · Uptime {{FormatUptime(report.Uptime)}}</p>
                <p>Pending reboot: {{(report.PendingReboot ? "Yes" : "No")}} · Defender realtime: {{(report.Defender.RealTimeProtection ? "On" : "Off")}}</p>
              </div>
              <div class="card">
                <h2>Findings</h2>
                <ul>{{findings}}</ul>
              </div>
              <div class="card">
                <h2>Storage</h2>
                <table><thead><tr><th>Drive</th><th>Used</th><th>Free</th></tr></thead><tbody>{{drives}}</tbody></table>
              </div>
              <div class="card">
                <h2>Recent errors</h2>
                <table><thead><tr><th>Time</th><th>Source</th><th>Message</th></tr></thead><tbody>{{errors}}</tbody></table>
              </div>
            </body>
            </html>
            """;
    }

    public static string ToJson(HealthReport report) =>
        System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public static string FormatUptime(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}d {value.Hours}h";
        }

        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{value.Minutes}m";
    }
}
