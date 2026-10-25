// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace EncoDeck.Platform.Lhdc;

public sealed record LhdcDevice(string Instance, string Name, string Address)
{
    public string Label => $"{Name} · {Address}";
}

public sealed record LhdcProfile(int SampleRate = 48000, int Bits = 24, int Kbps = 400, bool Adaptive = false)
{
    public void Validate()
    {
        if (SampleRate is not (44100 or 48000 or 96000 or 192000) || Bits is not (16 or 24)
            || Kbps is not (64 or 160 or 192 or 240 or 256 or 320 or 400 or 480 or 500 or 900 or 1000))
            throw new ArgumentOutOfRangeException(nameof(SampleRate), "Invalid LHDC profile.");
    }
}

/// <summary>The profile the LHDC service currently runs with, as installed in the registry.</summary>
public sealed record LhdcInstalledProfile(int SampleRate, int Bits, int Kbps, bool Adaptive);

/// <summary>
/// The bitrates the upstream encoder accepts. 44.1 kHz uses 240 and 480 where the other sample
/// rates use 256 and 500, so both forms are mapped onto a canonical value to keep the user's
/// choice while the sample rate changes.
/// </summary>
public static class LhdcBitrates
{
    public static readonly int[] Canonical = [64, 160, 192, 256, 320, 400, 500, 900, 1000];

    public static int ToCanonical(int sampleRate, int kbps) => sampleRate == 44100
        ? kbps switch { 240 => 256, 480 => 500, _ => kbps }
        : kbps;

    public static int ToActual(int sampleRate, int canonical) => sampleRate == 44100
        ? canonical switch { 256 => 240, 500 => 480, _ => canonical }
        : canonical;
}

/// <summary>EncoDeck's UI talks directly to the source-built CLI and deployment script.</summary>
public sealed class LhdcBackend(string packageRoot, string resultsDirectory)
{
    // Read-only commands the unelevated process may run. Anything that writes to the earbuds,
    // the drivers or the service gets its own method so it cannot be reached through a query.
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "targets", "target", "inspect", "audio-status", "profile-options", "profile-check", "configure", "connection",
    };

    public async Task<JsonObject> QueryAsync(string? instance, string command, params string[] arguments)
    {
        // Only expose read-only commands to the unelevated process.
        if (!ReadOnlyCommands.Contains(command))
            throw new ArgumentException("Unsupported query.", nameof(command));
        return await RunAsync(instance, command, arguments, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the earbud's Hi-Res switch over the vendor control channel. The switch is what makes
    /// the earbud advertise higher sample rates and bitrates, so it gates every option above 400 kbps.
    /// </summary>
    public Task<JsonObject> QueryHiResAsync(string instance) => RunAsync(instance, "hires", [], null);

    /// <summary>
    /// Turns the earbud's Hi-Res switch on or off through the upstream CLI. The CLI opens its own
    /// control channel to the earbud, so this only works for an earbud that EncoDeck does not
    /// control itself; the CLI also asks the service to negotiate the capabilities again.
    /// </summary>
    public Task<JsonObject> SetHiResAsync(string instance, bool enabled) =>
        RunAsync(instance, "hires", [enabled ? "on" : "off"], TimeSpan.FromSeconds(90));

    /// <summary>
    /// The formats the earbud's advertised capabilities allow. The media MTU narrows the result:
    /// the encoder cannot split a frame, so a single frame that does not fit is dropped. A media MTU
    /// of zero means the transport has not reported one yet; the result then only reflects what the
    /// earbud itself advertises.
    /// </summary>
    public Task<JsonObject> ProfileOptionsAsync(string instance, string capabilities, int mediaMtu) =>
        QueryAsync(instance, "profile-options", capabilities, mediaMtu.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Asks the LHDC service to negotiate the earbud's capabilities again. The service caches what
    /// the earbud advertised and compares the revision it published with the one in the registry;
    /// when the two differ it drops the cache and negotiates again. This is the same request the
    /// upstream control panel sends after it changed the earbud's Hi-Res switch, and it is the only
    /// way a changed switch becomes visible to the encoder.
    /// </summary>
    public static void RequestCapabilityRefresh()
    {
        int revision;
        using (var read = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\LHDC-Win", writable: false))
        {
            if (read is null) throw new InvalidOperationException("LHDC is not installed.");
            revision = read.GetValue("CapabilityRevision") is int value ? value : 0;
        }
        // The key grants normal users data-value writes only. The Windows API is called directly:
        // the managed registry API refuses to write this key even though its access mask allows
        // it, and the upstream control panel writes the same two values the same way.
        if (RegOpenKeyExW(Registry.LocalMachine.Handle.DangerousGetHandle(), @"SOFTWARE\LHDC-Win", 0,
                KeyQueryValue | KeySetValue, out IntPtr key) != 0)
            throw new InvalidOperationException("LHDC is not installed.");
        try
        {
            // Clearing the cache makes the next session a fresh negotiation instead of a replay.
            if (RegSetValueExW(key, "PeerCapabilities", 0, RegistryBinary, [], 0) != 0 ||
                RegSetValueExW(key, "CapabilityRevision", 0, RegistryDword, BitConverter.GetBytes(revision + 1), 4) != 0)
                throw new InvalidOperationException("The LHDC service could not be asked to renegotiate.");
        }
        finally { RegCloseKey(key); }
    }

    private const int KeyQueryValue = 0x0001, KeySetValue = 0x0002;
    private const int RegistryBinary = 3, RegistryDword = 4;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr root, string subKey, uint options, int desired, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr key, string valueName, uint reserved, int type, byte[] data, uint dataSize);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr key);

    /// <summary>
    /// Checks a profile against the earbud's advertised capabilities. This runs offline, so an
    /// impossible profile is reported before the elevated install is started.
    /// </summary>
    public Task<JsonObject> ConfigureAsync(string instance, string capabilities, LhdcProfile profile)
    {
        profile.Validate();
        return QueryAsync(instance, "configure", capabilities,
            profile.SampleRate.ToString(CultureInfo.InvariantCulture),
            profile.Bits.ToString(CultureInfo.InvariantCulture),
            profile.Kbps.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The profile the LHDC service runs with, or null when LHDC is not installed.</summary>
    public static LhdcInstalledProfile? InstalledProfile()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\LHDC-Win");
            if (key?.GetValue("Profile") is not byte[] value || value.Length != 12) return null;
            int adaptive = key.GetValue("AdaptiveBitrate") is int flag ? flag : 0;
            return new LhdcInstalledProfile(
                BitConverter.ToInt32(value, 0),
                BitConverter.ToInt32(value, 4),
                BitConverter.ToInt32(value, 8),
                adaptive == 1);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static string ModeCommand(string root, string instance, bool lhdc, LhdcProfile profile, string resultFile)
    {
        profile.Validate();
        if (!instance.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) || instance.Any(char.IsControl))
            throw new ArgumentException("Select a paired Bluetooth Audio Sink.", nameof(instance));
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        return $"& {Quote(Path.Combine(root, "scripts", "Set-AudioMode.ps1"))} -PackageRoot {Quote(root)} "
            + $"-DeviceInstance {Quote(instance)} -Mode {(lhdc ? "LHDC" : "Windows")} "
            + $"-SampleRate {profile.SampleRate.ToString(CultureInfo.InvariantCulture)} -Bits {profile.Bits} -Kbps {profile.Kbps} "
            + (profile.Adaptive ? "-AdaptiveBitrate " : "") + $"-ResultFile {Quote(resultFile)}";
    }

    public async Task<JsonObject> ApplyAsync(string instance, bool lhdc, LhdcProfile profile)
    {
        Directory.CreateDirectory(resultsDirectory);
        string resultFile = Path.Combine(resultsDirectory, $"mode-{Guid.NewGuid():N}.json");
        string command = ModeCommand(packageRoot, instance, lhdc, profile, resultFile);
        var start = new ProcessStartInfo(Path.Combine(packageRoot, "runtime", "powershell", "pwsh.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
        };
        using var process = Process.Start(start) ?? throw new IOException("Cannot start LHDC deployment.");
        // Never kill a deployment: the upstream script must be allowed to finish rollback.
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (!File.Exists(resultFile)) throw new IOException($"LHDC deployment returned no result (exit {process.ExitCode}).");
        var result = JsonNode.Parse(await File.ReadAllTextAsync(resultFile).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Invalid LHDC deployment result.");
        if (process.ExitCode != 0 || result["Completed"]?.GetValue<bool>() != true)
            throw new IOException($"{result["Error"]} {result["RecoveryError"]} RebootRequired={result["RebootRequired"]}. {result["EvidenceDirectory"]}");
        return result;
    }

    private async Task<JsonObject> RunAsync(string? instance, string command, string[] arguments, TimeSpan? timeout)
    {
        var start = new ProcessStartInfo(Path.Combine(packageRoot, "backend", "lhdc-host.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(instance) && command != "targets")
        {
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(instance);
        }
        start.ArgumentList.Add(command);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start LHDC backend.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("LHDC backend query timed out.");
        }
        string text = await output.ConfigureAwait(false), errors = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(errors.Trim().Length > 0 ? errors.Trim() : text.Trim());
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? throw new InvalidDataException("No LHDC backend result.");
        return JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("Invalid LHDC backend result.");
    }
}
