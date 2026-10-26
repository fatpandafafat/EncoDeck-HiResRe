// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Text.Json;
using EncoDeck.Core.Capabilities;
using EncoDeck.Platform.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Builds a zip a user can attach to a bug report: redacted logs, settings and a state summary.
/// Bluetooth addresses are redacted (log lines are redacted when written and again here).
/// </summary>
internal static class DiagnosticsExporter
{
    public static async Task<string> ExportAsync(AppHost host)
    {
        Directory.CreateDirectory(host.Paths.DiagnosticsDirectory);
        string path = Path.Combine(host.Paths.DiagnosticsDirectory, $"EncoDeck-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        var state = host.Store.Current;
        var summary = new
        {
            app = typeof(DiagnosticsExporter).Assembly.GetName().Version?.ToString(),
            os = Environment.OSVersion.VersionString,
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            createdUtc = DateTimeOffset.UtcNow,
            device = state.Device is { } d ? new { name = d.Name, address = d.Address.ToRedactedString() } : null,
            model = state.Profile?.ModelId,
            connection = state.Connection.ToString(),
            leAudio = new { state.IsLeAudioBonded, links = state.LeAudioLinks, audioStreamingVerified = false },
            firmware = state.Firmware?.Value.Extra,
            battery = new
            {
                left = state.Battery.Left is { } l ? new { l.Value.Level, l.Value.IsCharging, source = l.Source.ToString(), l.Timestamp } : null,
                right = state.Battery.Right is { } r ? new { r.Value.Level, r.Value.IsCharging, source = r.Source.ToString(), r.Timestamp } : null,
                @case = state.Battery.Case is { } c ? new { c.Value.Level, c.Value.IsCharging, source = c.Source.ToString(), c.Timestamp } : null,
            },
            noise = state.NoiseControl is { } n ? new { mode = n.Value.Mode.ToString(), n.Value.LevelId, source = n.Source.ToString() } : null,
            equalizer = state.Equalizer?.Value.RawPresetValue,
            lastError = state.LastError?.Message,
            capabilities = state.Profile?.Capabilities.All.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Support.ToString()),
            settings = host.Settings.Current,
        };
        await WriteEntryAsync(zip, "summary.json", JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var log in new DirectoryInfo(host.Paths.LogDirectory).GetFiles("encodeck-*.log").OrderByDescending(f => f.Name).Take(3))
        {
            string text;
            using (var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                text = await reader.ReadToEndAsync();
            }
            await WriteEntryAsync(zip, "logs/" + log.Name, LogRedactor.Redact(text));
        }
        return path;
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(content);
    }

    public static void RevealInExplorer(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
