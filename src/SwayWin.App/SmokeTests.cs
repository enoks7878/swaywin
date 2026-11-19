using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SwayWin.Core.Configuration;
using SwayWin.Core.Layout;

namespace SwayWin;

internal static class SmokeTests
{
    private sealed class TestWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.ExStyle |= (int)(Native.WsExToolwindow | Native.WsExNoactivate); return parameters; }
        }
    }

    internal static int Run()
    {
        using ApplicationContext context = new();
        List<TestWindow> forms = [];
        List<WindowRestoreData> originals = [];
        using System.Windows.Forms.Timer timer = new() { Interval = 220 };
        PixelRect area = new(100, 160, 1000, 600);
        WindowManagerState state = new(new WindowSettings { GapInner = 6, GapOuter = 0, SmartGaps = "on", HideEdgeBorders = "smart" },
            [new("test-only", new(100, 140, 1000, 640), area, true)]);
        int phase = 0;
        int result = 0;
        long thirdId = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        void Add(bool maximized = false)
        {
            TestWindow form = new() { Text = $"SwayWin isolated test {forms.Count + 1}", Bounds = new(160 + forms.Count * 40, 240 + forms.Count * 30, 320, 240), ShowInTaskbar = false };
            forms.Add(form);
            form.Show();
            if (maximized) Native.ShowWindow(form.Handle, Native.SwShowmaximized);
            originals.Add(WindowRestoreData.Capture(form.Handle));
            state.AddWindow(form.Handle.ToInt64(), form.Text, WindowManagerContext.ToPixelRect(form.Bounds));
        }
        void Apply()
        {
            foreach (WindowLayout layout in state.CalculateLayout().Windows)
            {
                nint hwnd = (nint)layout.WindowId;
                Theme.ConfigureFrame(hwnd, originals.Single(original => original.Handle == layout.WindowId).Style, true);
                Theme.Apply(hwnd, state.FocusedWindow?.WindowId == layout.WindowId, layout.BorderWidth > 0);
                if (!layout.Visible) Native.ShowWindow(hwnd, Native.SwHide);
                else
                {
                    Native.ShowWindow(hwnd, Native.SwShownoactivate);
                    Check(Native.SetVisibleBounds(hwnd, WindowManagerContext.ToRectangle(layout.Bounds)), "SetVisibleBounds failed for a test-owned window.");
                }
            }
        }
        void CheckNativeGeometry()
        {
            foreach (WindowLayout layout in state.CalculateLayout().Windows.Where(layout => layout.Visible))
            {
                Rectangle actual = Native.GetBounds((nint)layout.WindowId, true);
                Rectangle expected = WindowManagerContext.ToRectangle(layout.Bounds);
                Check(Math.Abs(actual.X - expected.X) <= 1 && Math.Abs(actual.Y - expected.Y) <= 1 &&
                    Math.Abs(actual.Width - expected.Width) <= 1 && Math.Abs(actual.Height - expected.Height) <= 1,
                    $"Native geometry mismatch: expected {expected}, got {actual}.");
            }
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                switch (phase++)
                {
                    case 0:
                        Add(); Add(true); state.Split(true); Add(); thirdId = forms[2].Handle.ToInt64();
                        Native.SetStyle(forms[1].Handle, Native.GetStyle(forms[1].Handle) | Native.WsDisabled);
                        Apply(); break;
                    case 1:
                        CheckNativeGeometry();
                        Check(!Native.IsZoomed(forms[1].Handle), "Tiling reinserted the original maximized state.");
                        Check((Native.GetStyle(forms[1].Handle) & Native.WsDisabled) != 0, "Frame styling changed a modal disabled state.");
                        Native.SetStyle(forms[1].Handle, Native.GetStyle(forms[1].Handle) & ~Native.WsDisabled);
                        Console.WriteLine("PASS native nested tiling / maximized adoption / modal state / DWM theme");
                        state.MoveFocusedToWorkspace("2"); Apply(); break;
                    case 2:
                        Check(!Native.IsWindowVisible((nint)thirdId), "Inactive workspace was not hidden.");
                        state.SwitchWorkspace("2"); Apply(); break;
                    case 3:
                        CheckNativeGeometry(); Check(Native.IsWindowVisible((nint)thirdId), "Workspace switch did not restore visibility.");
                        Console.WriteLine("PASS native workspace hide/show"); state.ToggleFullscreen(); Apply(); break;
                    case 4:
                        CheckNativeGeometry(); Console.WriteLine("PASS native fullscreen geometry");
                        state.ToggleFullscreen(); state.ToggleFloating(); state.SetFloatingBounds(thirdId, new(180, 220, 400, 260)); state.Resize(ResizeAxis.Width, 20); Apply(); break;
                    case 5:
                        CheckNativeGeometry(); Check(state.GetFloatingBounds(thirdId).Width == 420, "Pixel resize failed.");
                        Console.WriteLine("PASS native floating / pixel resize"); state.MoveToScratchpad(); Apply(); break;
                    case 6:
                        Check(!Native.IsWindowVisible((nint)thirdId), "Scratchpad was not hidden."); state.ShowScratchpad(); Apply(); break;
                    case 7:
                        Check(Native.IsWindowVisible((nint)thirdId), "Scratchpad was not shown.");
                        Console.WriteLine("PASS native scratchpad hide/show");
                        JsonSerializerOptions jsonOptions = new() { IncludeFields = true };
                        string serialized = JsonSerializer.Serialize(new RecoveryJournal { Windows = originals }, jsonOptions);
                        RecoveryJournal roundtrip = JsonSerializer.Deserialize<RecoveryJournal>(serialized, jsonOptions)
                            ?? throw new InvalidOperationException("Recovery journal did not deserialize.");
                        Check(roundtrip.Windows.Count == originals.Count, "Recovery journal lost windows.");
                        Native.SetStyle(forms[1].Handle, Native.GetStyle(forms[1].Handle) | Native.WsDisabled);
                        foreach (WindowRestoreData original in roundtrip.Windows) Check(original.Restore(), "Test window restore failed.");
                        break;
                    case 8:
                        foreach (WindowRestoreData original in originals)
                        {
                            Check((Native.GetStyle((nint)original.Handle) & (Native.WsCaption | Native.WsThickframe)) ==
                                (original.Style & (Native.WsCaption | Native.WsThickframe)), "Native frame was not restored.");
                            Check(Native.IsWindowVisible((nint)original.Handle), "Restore left an owned window hidden.");
                            Native.Rect normal = Native.GetPlacement((nint)original.Handle).NormalPosition;
                            Check(normal.ToRectangle() == original.Placement.NormalPosition.ToRectangle(), "Original window position was not restored.");
                            Check(Native.DwmGetInt((nint)original.Handle, Native.DwmwaCaptionColor, Native.DwmColorDefault) == original.CaptionColor,
                                "Original DWM caption color was not restored.");
                        }
                        Check(Native.IsZoomed(forms[1].Handle), "Originally maximized window was not restored maximized.");
                        Check((Native.GetStyle(forms[1].Handle) & Native.WsDisabled) != 0, "Recovery overwrote a live modal disabled state.");
                        Native.SetStyle(forms[1].Handle, Native.GetStyle(forms[1].Handle) & ~Native.WsDisabled);
                        Console.WriteLine("PASS native journal roundtrip / geometry / theme restoration");
                        timer.Stop(); context.ExitThread(); break;
                }
            }
            catch (Exception exception)
            {
                result = 1; Console.Error.WriteLine($"FAIL native smoke test: {exception}"); timer.Stop(); context.ExitThread();
            }
        };
        timer.Start();
        try { Application.Run(context); }
        finally
        {
            foreach (WindowRestoreData original in originals) original.Restore();
            foreach (TestWindow form in forms) form.Dispose();
        }
        if (result == 0)
        {
            try
            {
                RunWorkerChecks(); RunRecoveryChecks();
                Console.WriteLine("Smoke test passed. Only temporary own-process windows and test journals were touched; no global hooks or AppBar reservations.");
            }
            catch (Exception exception) { Console.Error.WriteLine($"FAIL recovery smoke test: {exception}"); result = 1; }
        }
        return result;
    }

    private static void RunWorkerChecks()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim resume = new();
        using ManualResetEventSlim finished = new();
        NativeWorkQueue worker = new();
        int executed = 0;
        worker.Submit(() => { entered.Set(); resume.Wait(); });
        if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Native worker did not start.");
        worker.Submit(() => Interlocked.Exchange(ref executed, 2));
        worker.Submit(() => { Interlocked.Exchange(ref executed, 3); finished.Set(); });
        resume.Set();
        if (!finished.Wait(TimeSpan.FromSeconds(2)) || executed != 3) throw new InvalidOperationException("Native worker did not coalesce obsolete requests.");
        worker.Cancel();
        using DesktopActions actions = new(() => Rectangle.Empty, () => { }, _ => { }, @"M:\");
        if (actions.TranslateScreenshotDirectory("/fem/Data/media/Photo/Screenshots") != @"M:\fem\Data\media\Photo\Screenshots")
            throw new InvalidOperationException("Screenshot path dropped part of the imported Linux filesystem path.");
        using ManualResetEventSlim secondEntered = new();
        using ManualResetEventSlim secondResume = new();
        NativeWorkQueue secondWorker = new();
        secondWorker.Submit(() => { secondEntered.Set(); secondResume.Wait(); });
        if (!secondEntered.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Drain test worker did not start.");
        Task drained = secondWorker.Cancel();
        if (drained.IsCompleted) throw new InvalidOperationException("Canceled worker incorrectly reported its in-flight operation as drained.");
        secondResume.Set();
        if (!drained.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Canceled worker did not drain.");
        string pipe = $"swaywin-smoke-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using CommandServer server = new(_ => Task.FromResult("[{\"success\":true}]"), pipeName: pipe);
        string response = CommandServer.Send("exit", pipe).GetAwaiter().GetResult();
        if (response != "[{\"success\":true}]") throw new InvalidOperationException("Local IPC acknowledgement was lost.");
        Console.WriteLine("PASS coalesced native worker / ordered drain / screenshot path / isolated IPC");
    }

    private sealed class RecoveryWindow : IDisposable
    {
        private readonly ManualResetEventSlim ready = new();
        private readonly ManualResetEventSlim resume = new();
        private readonly Thread thread;
        private Exception? failure;
        internal WindowRestoreData Original { get; private set; } = null!;

        internal RecoveryWindow(bool frozen)
        {
            thread = new(() =>
            {
                try
                {
                    using ApplicationContext context = new();
                    using TestWindow form = new() { Text = "SwayWin isolated recovery test", Bounds = new(240, 280, 320, 240), ShowInTaskbar = false };
                    form.FormClosed += (_, _) => context.ExitThread();
                    form.Show();
                    Original = WindowRestoreData.Capture(form.Handle);
                    Theme.ConfigureFrame(form.Handle, Original.Style, true);
                    if (frozen)
                        form.BeginInvoke(() => { ready.Set(); WaitForSingleObject(resume.WaitHandle.SafeWaitHandle, uint.MaxValue); });
                    else { Native.ShowWindow(form.Handle, Native.SwHide); ready.Set(); }
                    Application.Run(context);
                }
                catch (Exception exception) { failure = exception; ready.Set(); }
            }) { IsBackground = true, Name = "SwayWin isolated recovery UI" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(3)) || failure is not null)
                throw new InvalidOperationException("Could not create isolated recovery window.", failure);
        }

        internal void Resume() => resume.Set();

        public void Dispose()
        {
            resume.Set();
            if (Original is not null) Native.PostMessage((nint)Original.Handle, Native.WmClose, 0, 0);
            if (thread.Join(TimeSpan.FromSeconds(3))) { ready.Dispose(); resume.Dispose(); }
        }
    }

    private static void RunRecoveryChecks()
    {
        using RecoveryWindow healthy = new(false);
        using RecoveryWindow frozen = new(true);
        nint hungHandle = (nint)frozen.Original.Handle;
        if (Native.IsResponsiveWindow(hungHandle))
            throw new InvalidOperationException("Isolated stalled UI unexpectedly answered the bounded responsiveness probe.");
        string root = Path.Combine(Path.GetTempPath(), "kilo");
        Directory.CreateDirectory(root);
        string journalPath = Path.Combine(root, $"swaywin-smoke-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        JsonSerializerOptions options = new() { IncludeFields = true };
        try
        {
            File.WriteAllText(journalPath, JsonSerializer.Serialize(new RecoveryJournal { OwnerProcessId = -1, Windows = [healthy.Original, frozen.Original] }, options));
            if (Recovery.RestoreJournal(journalPath) != -1 || !File.Exists(journalPath))
                throw new InvalidOperationException("Incomplete recovery incorrectly deleted its journal.");
            RecoveryJournal pending = JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllText(journalPath), options)
                ?? throw new InvalidOperationException("Retained recovery journal is invalid.");
            if (pending.Windows.Count != 1 || pending.Windows[0].Handle != frozen.Original.Handle || !Native.IsWindowVisible((nint)healthy.Original.Handle))
                throw new InvalidOperationException("A stalled window prevented independent healthy-window recovery.");
            frozen.Resume();
            if (!SpinWait.SpinUntil(() => Native.IsResponsiveWindow(hungHandle), TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Isolated UI did not resume.");
            if (Recovery.RestoreJournal(journalPath) < 0 || File.Exists(journalPath) || !Native.IsWindowVisible(hungHandle))
                throw new InvalidOperationException("Deferred window recovery did not complete after the application resumed.");
            Console.WriteLine("PASS stalled-window isolation / retained journal / deferred recovery");
        }
        finally { File.Delete(journalPath); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
