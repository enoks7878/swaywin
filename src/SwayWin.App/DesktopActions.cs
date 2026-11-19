using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SwayWin;

internal sealed class DesktopActions : IDisposable
{
    private const string DefaultScreenshotDirectory = "/fem/Data/media/Photo/Screenshots";
    private static readonly Regex ScreenshotAction = new(
        """\Af=(?<directory>/[A-Za-z0-9._/-]+)/\$\(\s*date\s+\+(?:'%Y-%m-%d_%H-%M-%S'|"%Y-%m-%d_%H-%M-%S"|%Y-%m-%d_%H-%M-%S)\s*\)\.hui\.png\s*(?:&&|;)\s*grim(?<region>\s+-g\s+"\$\(slurp\s+-b\s*000000cc\s+-c\s*39ff14\s+-w\s*2\)")?\s+"\$f"\s*&&\s*wl-copy\s+--type\s+image/png\s*<\s*"\$f"\z""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ScreenshotWrapper = new(
        """\A(?:sh|bash|/bin/sh|/bin/bash|/usr/bin/sh|/usr/bin/bash)\s+-c\s+(?<quote>['"])(?<payload>.*)\k<quote>\z""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex VolumeAction = new(
        @"\Awpctl\s+set-volume\s+(?:(?:-l\s*|--limit\s+)1(?:\.0)?\s+)?@DEFAULT_AUDIO_SINK@\s+5%([+-])\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex MuteAction = new(
        @"\Awpctl\s+set-mute\s+@DEFAULT_AUDIO_(SINK|SOURCE)@\s+toggle\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BrightnessAction = new(
        @"\Abrightnessctl\s+set\s+5%([+-])\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private const string BrightnessScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $panels = @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness | Where-Object { $_.Active })
            $methods = @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | Where-Object { $_.Active })
            $changed = 0
            foreach ($panel in $panels) {
                $method = $methods | Where-Object { $_.InstanceName -eq $panel.InstanceName } | Select-Object -First 1
                if ($null -eq $method) { continue }
                $next = [byte][Math]::Min(100, [Math]::Max(0, [int]$panel.CurrentBrightness + $delta))
                $result = Invoke-CimMethod -InputObject $method -MethodName WmiSetBrightness -Arguments @{ Timeout = [uint32]0; Brightness = $next }
                if ($result.ReturnValue -ne 0) { throw 'The display driver rejected the brightness change.' }
                [Console]::WriteLine(('Display brightness: {0}%.' -f $next))
                $changed++
            }
            if ($changed -eq 0) {
                [Console]::Error.WriteLine('No supported laptop brightness interface. External desktop monitors are not adjusted.')
                exit 3
            }
        } catch {
            [Console]::Error.WriteLine(('Laptop WMI brightness is unsupported or unavailable; external desktop monitors are not adjusted. ' + $_.Exception.Message))
            exit 1
        }
        """;

    private readonly Func<Rectangle> _activeMonitor;
    private readonly Action _restoreFocus;
    private readonly Action<string> _notify;
    private readonly string _sourceRoot;
    private readonly List<string> _history = [];
    private readonly Queue<int> _brightnessSteps = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Form? _overlay;
    private bool _disposed;
    private bool _brightnessRunning;

    internal DesktopActions(Func<Rectangle> activeMonitor, Action restoreFocus,
        Action<string> notify, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(activeMonitor);
        ArgumentNullException.ThrowIfNull(restoreFocus);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        _activeMonitor = activeMonitor;
        _restoreFocus = restoreFocus;
        _notify = notify;
        _sourceRoot = sourceRoot;
    }

    internal bool IsOverlayOpen => _overlay is { IsDisposed: false };

    internal bool TryExecute(string command)
    {
        if (_disposed || !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(command) || command.Length > 16_384)
            return false;

        string value = command.Trim();
        if (value.Equals("ghostty", StringComparison.Ordinal))
        {
            if (!TryLaunchWindows("wt.exe -w new"))
                Notify("Windows Terminal could not be launched. The ghostty mapping requires wt.exe on PATH.");
            return true;
        }

        if (value.Equals("swaywin-launcher", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(value, @"\Awofi\s+--show(?:\s+|=)run(?:\s+--allow-images)?\z", RegexOptions.CultureInvariant))
        {
            OpenLauncher();
            return true;
        }

        if (value.Equals("swaywin-screenshot full", StringComparison.OrdinalIgnoreCase))
        {
            CaptureScreenshot(false, DefaultScreenshotDirectory);
            return true;
        }
        if (value.Equals("swaywin-screenshot region", StringComparison.OrdinalIgnoreCase))
        {
            CaptureScreenshot(true, DefaultScreenshotDirectory);
            return true;
        }
        if (TryGetScreenshotAction(value, out string directory, out bool region))
        {
            CaptureScreenshot(region, directory);
            return true;
        }

        Match volume = VolumeAction.Match(value);
        if (volume.Success)
        {
            ChangeAudio(false, volume.Groups[1].Value == "+" ? 0.05f : -0.05f);
            return true;
        }
        Match mute = MuteAction.Match(value);
        if (mute.Success)
        {
            ChangeAudio(mute.Groups[1].Value == "SOURCE", null);
            return true;
        }
        Match brightness = BrightnessAction.Match(value);
        if (brightness.Success)
        {
            QueueBrightness(brightness.Groups[1].Value == "+" ? 1 : -1);
            return true;
        }
        if (value.Equals("swaybg", StringComparison.Ordinal) || value.StartsWith("swaybg ", StringComparison.Ordinal))
        {
            Notify("swaybg is intentionally ignored. Existing Windows wallpaper is preserved; SwayWin does not change the global OS theme.");
            return true;
        }
        return false;
    }

    internal bool TryLaunchWindows(string command)
    {
        if (_disposed || !OperatingSystem.IsWindows() || !TrySplitWindowsCommand(command, out string target, out string arguments))
            return false;

        if (target.StartsWith('/'))
            return false;

        try
        {
            var start = new ProcessStartInfo { Arguments = arguments };
            if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && uri.Scheme.Length > 1 &&
                uri.Scheme is not ("file" or "data" or "javascript" or "vbscript" or "about"))
            {
                if (!string.IsNullOrWhiteSpace(arguments))
                    return false;
                start.FileName = target;
                start.UseShellExecute = true;
            }
            else
            {
                string? path = ResolveWindowsTarget(target);
                if (path is null)
                    return false;
                if (Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "sh" or "bash" or "dash" or "zsh" or "fish")
                    return false;
                start.FileName = path;
                start.UseShellExecute = Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url" or ".appref-ms";
            }

            // Arguments are the original native tail, never re-tokenized or wrapped in a shell.
            using Process? process = Process.Start(start);
            return true;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Notify($"Could not launch {target}: {error.Message}");
            return false;
        }
    }

    private static bool TrySplitWindowsCommand(string command, out string target, out string arguments)
    {
        target = arguments = string.Empty;
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(['\0', '\r', '\n']) >= 0)
            return false;

        string value = command.TrimStart();
        int end;
        if (value[0] == '"')
        {
            end = value.IndexOf('"', 1);
            if (end < 0 || (end + 1 < value.Length && !char.IsWhiteSpace(value[end + 1])))
                return false;
            target = value[1..end];
            end++;
        }
        else
        {
            end = 0;
            while (end < value.Length && !char.IsWhiteSpace(value[end]))
                end++;
            target = value[..end];
            if (target.Contains('"') || target.Contains('\''))
                return false;
        }
        while (end < value.Length && char.IsWhiteSpace(value[end]))
            end++;
        arguments = value[end..];
        return target.Length > 0;
    }

    private static string? ResolveWindowsTarget(string target)
    {
        target = Environment.ExpandEnvironmentVariables(target);
        string extension = Path.GetExtension(target).ToLowerInvariant();
        if (extension.Length > 0 && extension is not (".exe" or ".com" or ".lnk" or ".url" or ".appref-ms"))
            return null;

        string[] names = extension.Length == 0 ? [target + ".exe", target + ".com"] : [target];
        if (target.Contains('\\') || target.Contains('/') || Path.IsPathRooted(target))
        {
            if (Path.IsPathRooted(target) && !Path.IsPathFullyQualified(target))
                return null;
            return names.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        }

        IEnumerable<string> directories = new[] { Environment.SystemDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
        foreach (string entry in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(directory))
                continue;
            foreach (string file in names)
            {
                string path = Path.Combine(directory, file);
                if (File.Exists(path))
                    return path;
            }
        }
        return null;
    }

    private static bool TryGetScreenshotAction(string command, out string directory, out bool region)
    {
        directory = string.Empty;
        region = false;
        Match wrapper = ScreenshotWrapper.Match(command);
        if (wrapper.Success)
        {
            command = wrapper.Groups["payload"].Value;
            command = wrapper.Groups["quote"].Value == "'"
                ? command.Replace("'\\''", "'", StringComparison.Ordinal)
                : command.Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\$", "$", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        Match screenshot = ScreenshotAction.Match(command);
        if (!screenshot.Success)
            return false;
        directory = screenshot.Groups["directory"].Value;
        region = screenshot.Groups["region"].Success;
        return true;
    }

    private void OpenLauncher()
    {
        if (IsOverlayOpen)
            return;
        nint previousFocus = Native.GetForegroundWindow();
        var launcher = new LauncherForm(_activeMonitor(), command =>
        {
            if (!TryLaunchWindows(command))
                return false;
            _history.RemoveAll(item => item.Equals(command, StringComparison.OrdinalIgnoreCase));
            _history.Insert(0, command);
            if (_history.Count > 50)
                _history.RemoveAt(_history.Count - 1);
            return true;
        }, _history);
        _overlay = launcher;
        launcher.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_overlay, launcher))
                _overlay = null;
            bool canceled = launcher.DialogResult != DialogResult.OK;
            launcher.Dispose();
            if (canceled && !_disposed)
                RestoreOverlayFocus(previousFocus);
        };
        try
        {
            launcher.Show();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            _overlay = null;
            launcher.Dispose();
            RestoreOverlayFocus(previousFocus);
            Notify($"Could not open the command launcher: {error.Message}");
        }
    }

    private void CaptureScreenshot(bool region, string directory)
    {
        if (IsOverlayOpen)
        {
            Notify("Close the current overlay before taking a screenshot.");
            return;
        }
        nint previousFocus = Native.GetForegroundWindow();
        Bitmap? frozen = null;
        try
        {
            Rectangle bounds = Screen.AllScreens.Select(screen => screen.Bounds).Aggregate(Rectangle.Union);
            frozen = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(frozen))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

            if (!region)
            {
                SaveScreenshot(frozen, directory);
                return;
            }

            var picker = new RegionCaptureForm(bounds, frozen);
            frozen = null; // The picker owns the frozen bitmap, including cancellation and disposal.
            _overlay = picker;
            picker.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_overlay, picker))
                    _overlay = null;
                try
                {
                    if (!_disposed && picker.DialogResult == DialogResult.OK)
                    {
                        using Bitmap image = picker.CopySelection();
                        SaveScreenshot(image, directory);
                    }
                }
                catch (Exception error) when (error is ArgumentException or ExternalException or InvalidOperationException)
                {
                    Notify($"Screenshot failed: {error.Message}");
                }
                finally
                {
                    picker.Dispose();
                    if (!_disposed)
                        RestoreOverlayFocus(previousFocus);
                }
            };
            try
            {
                picker.Show();
            }
            catch
            {
                _overlay = null;
                picker.Dispose();
                RestoreOverlayFocus(previousFocus);
                throw;
            }
        }
        catch (Exception error) when (error is Win32Exception or ArgumentException or ExternalException or InvalidOperationException)
        {
            Notify($"Screenshot failed: {error.Message}. The secure desktop cannot be captured.");
        }
        finally
        {
            frozen?.Dispose();
        }
    }

    internal string TranslateScreenshotDirectory(string directory)
    {
        string? root = Path.GetPathRoot(_sourceRoot);
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
            throw new IOException("The Linux import drive is unavailable.");
        if (directory.Split('/').Skip(1).Any(part => part is "" or "." or ".."))
            throw new IOException("The configured screenshot directory contains an invalid path component.");
        string relative = directory.TrimStart('/');
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private void SaveScreenshot(Bitmap image, string directory)
    {
        string path;
        try
        {
            path = SavePng(image, TranslateScreenshotDirectory(directory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ExternalException)
        {
            Notify($"Configured screenshot directory is unusable: {error.Message}. Trying Pictures\\Screenshots\\SwayWin.");
            try
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (string.IsNullOrEmpty(pictures))
                    throw new IOException("The Pictures folder is unavailable.");
                path = SavePng(image, Path.Combine(pictures, "Screenshots", "SwayWin"));
            }
            catch (Exception fallbackError) when (fallbackError is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ExternalException)
            {
                Notify($"Screenshot could not be saved: {fallbackError.Message}. Nothing was copied to the clipboard.");
                return;
            }
        }

        try
        {
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            png.Position = 0;
            var data = new DataObject();
            data.SetImage(image);
            data.SetData("PNG", false, png);
            Clipboard.SetDataObject(data, true, 10, 80);
            Notify($"Screenshot saved and image copied to clipboard: {path}");
        }
        catch (Exception error) when (error is ExternalException or ThreadStateException or InvalidOperationException or ArgumentException)
        {
            Notify($"Screenshot saved to {path}, but copying the image to the clipboard failed: {error.Message}");
        }
    }

    private static string SavePng(Bitmap image, string directory)
    {
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        for (int suffix = 0; suffix < 10_000; suffix++)
        {
            string file = stamp + (suffix == 0 ? string.Empty : "_" + suffix.ToString(CultureInfo.InvariantCulture)) + ".hui.png";
            string path = Path.Combine(directory, file);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                continue;
            }
            try
            {
                using (stream)
                    image.Save(stream, ImageFormat.Png);
                return path;
            }
            catch
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        throw new IOException("No unused timestamp filename is available.");
    }

    private void ChangeAudio(bool capture, float? delta)
    {
        if (!OperatingSystem.IsWindows())
            return;
        object? enumerator = null;
        IMMDevice? device = null;
        object? volume = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(capture ? 1 : 0, 1, out device));
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out volume));
            var endpoint = (IAudioEndpointVolume)volume;
            Guid context = Guid.Empty;
            if (delta is float step)
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float current));
                float next = Math.Clamp(current + step, 0f, 1f);
                Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(next, ref context));
                Notify($"Output volume: {Math.Round(next * 100)}%.");
            }
            else
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
                Marshal.ThrowExceptionForHR(endpoint.SetMute(!muted, ref context));
                Notify($"{(capture ? "Microphone" : "Output audio")} {(muted ? "unmuted" : "muted")}.");
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
        {
            Notify($"Could not change the default {(capture ? "microphone" : "output audio")} endpoint: {error.Message}");
        }
        finally
        {
            if (volume is not null && Marshal.IsComObject(volume))
                Marshal.ReleaseComObject(volume);
            if (device is not null && Marshal.IsComObject(device))
                Marshal.ReleaseComObject(device);
            if (enumerator is not null && Marshal.IsComObject(enumerator))
                Marshal.ReleaseComObject(enumerator);
        }
    }

    private async void QueueBrightness(int direction)
    {
        _brightnessSteps.Enqueue(direction);
        if (_brightnessRunning)
            return;
        _brightnessRunning = true;
        try
        {
            while (!_disposed && _brightnessSteps.TryDequeue(out int step))
            {
                if (!await ApplyBrightnessAsync(step))
                {
                    _brightnessSteps.Clear();
                    break;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or Win32Exception or IOException or InvalidOperationException)
        {
            _brightnessSteps.Clear();
            Notify($"Laptop brightness could not be changed: {error.Message}");
        }
        finally
        {
            _brightnessRunning = false;
        }
    }

    private async Task<bool> ApplyBrightnessAsync(int direction)
    {
        // Only this fixed CIM script reaches PowerShell. Imported payloads are never interpolated.
        string script = (direction > 0 ? "$delta = 5;\n" : "$delta = -5;\n") + BrightnessScript;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("Windows PowerShell could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string stdout = (await output).Trim();
            string stderr = (await error).Trim();
            if (process.ExitCode != 0)
            {
                Notify(stderr.Length > 0 ? stderr : "Laptop WMI brightness is unsupported. External desktop displays are not adjusted.");
                return false;
            }
            Notify(stdout.Length > 0 ? stdout : "Laptop brightness updated.");
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
                await process.WaitForExitAsync();
                await output;
                await error;
            }
            catch (Exception cleanupError) when (cleanupError is Win32Exception or InvalidOperationException or IOException) { }
            Notify("Laptop brightness operation was canceled or timed out.");
            return false;
        }
    }

    private void Notify(string message)
    {
        if (!_disposed)
            _notify(message);
    }

    private void RestoreOverlayFocus(nint previousFocus)
    {
        _restoreFocus();
        if (previousFocus != 0 && Native.IsWindow(previousFocus) && Native.IsWindowVisible(previousFocus))
            Native.FocusWindow(previousFocus);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _brightnessSteps.Clear();
        _lifetime.Cancel();
        _lifetime.Dispose();
        Form? overlay = _overlay;
        _overlay = null;
        overlay?.Close();
        overlay?.Dispose();
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)]
    private sealed class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint classContext, nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint callback);
        [PreserveSig] int UnregisterControlChangeNotify(nint callback);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
