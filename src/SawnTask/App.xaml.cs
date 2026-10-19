using System.IO;
using System.Windows;
using SawnTask.Core;

namespace SawnTask;

public partial class App : Application
{
    static void SaveVisual(FrameworkElement element, string path)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
        int w = Math.Max(1, (int)(element.ActualWidth * dpi.DpiScaleX)), h = Math.Max(1, (int)(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "SawnTask.exe --selftest out.txt" runs the collector without a window
        // and writes what every source measured.
        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            string path = e.Args.Length >= 2 ? e.Args[1] : "selftest.txt";
            int code;
            using (var output = new StreamWriter(path))
                code = SelfTest.Run(output);
            Shutdown(code);
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0] == "--ipf-probe")
        {
            string path = e.Args.Length >= 2 ? e.Args[1] : "ipf-probe.txt";
            int code;
            using (var output = new StreamWriter(path))
                code = IpfProbe.Run(output);
            Shutdown(code);
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0] == "--sysinfo")
        {
            string path = e.Args.Length >= 2 ? e.Args[1] : "sysinfo.txt";
            int code;
            using (var output = new StreamWriter(path))
                code = SystemInfoDump.Run(output);
            Shutdown(code);
            return;
        }

        // "SawnTask.exe --bench out.txt D:\ [quick|normal|thorough]": the disk speed test without a window.
        if (e.Args.Length >= 3 && e.Args[0] == "--bench")
        {
            var profile = e.Args.Length >= 4 && Enum.TryParse(e.Args[3], true, out BenchProfile p) ? p : BenchProfile.Quick;
            int code = 0;
            using (var output = new StreamWriter(e.Args[1]))
            {
                try
                {
                    DiskBench.Run(e.Args[2], BenchOptions.For(profile),
                        pr => { if (pr.LiveMBps > 0) output.WriteLine($"live {pr.Phase,-7} {pr.Test,-12} {(pr.Write ? "w" : "r")} pass {pr.Pass} {pr.LiveMBps,8:0} MB/s"); },
                        r => output.WriteLine($"{r.Test,-12} {(r.Write ? "write" : "read"),-5} {r.MBps,9:0.0} MB/s {r.Iops,10:0} IOPS {r.LatencyUs,9:0.0} us"),
                        CancellationToken.None);
                }
                catch (BenchException ex) { output.WriteLine("FAILED: " + ex.Message); code = 1; }
            }
            Shutdown(code);
            return;
        }

        // "SawnTask.exe --health out.txt": what every disk says about its own health.
        if (e.Args.Length >= 2 && e.Args[0] == "--health")
        {
            using (var output = new StreamWriter(e.Args[1]))
                foreach (var disk in Native.StorageDevices.List())
                {
                    output.WriteLine($"Disk {disk.Number}: {disk.Model} ({disk.Bus} {disk.Kind})");
                    var h = Native.DiskHealthReader.Read(disk.Number, disk.Bus);
                    if (h is null) { output.WriteLine("  health report not available"); continue; }
                    var (level, reasons) = h.Assess();
                    output.WriteLine($"  {level}: {string.Join("; ", reasons.Select(r => r.Key))}");
                    output.WriteLine($"  {h}");
                }
            Shutdown(0);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--disk-scan")
        {
            int code;
            using (var output = new StreamWriter(e.Args[2]))
                code = DiskScanTest.Run(e.Args[1], output);
            Shutdown(code);
            return;
        }

        // Theme and language come from settings.json; with no saved language, Turkish Windows
        // starts in Turkish. SAWNTASK_THEME / SAWNTASK_LANG override both (used for testing).
        var saved = Settings.Load();
        Ui.Appearance.Apply(
            Environment.GetEnvironmentVariable("SAWNTASK_THEME") ?? saved.Theme,
            Environment.GetEnvironmentVariable("SAWNTASK_LANG") ?? saved.Language ?? Ui.Appearance.DefaultLanguage);

        var window = new MainWindow();
        window.Show();

        // Test hook: SAWNTASK_SWITCH="Light,tr" switches theme and language 3 s after start,
        // to check that everything on screen follows a live switch.
        if (Environment.GetEnvironmentVariable("SAWNTASK_SWITCH") is string sw && sw.Split(',') is [var theme, var lang])
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { timer.Stop(); Ui.Appearance.Apply(theme, lang); };
            timer.Start();
        }

        // "--open-disk [path]": used by "Restart as admin". Open the Disk space page and,
        // if a path was given, scan it again with the new rights.
        if (e.Args.Length >= 1 && e.Args[0] == "--open-disk")
        {
            window.Page = SawnTask.MainWindow.PageDisk;
            if (e.Args.Length >= 2)
            {
                string path = Elevation.FromArgs(e.Args[1]);
                window.Dispatcher.BeginInvoke(() => _ = window.DiskPage.StartScan(path),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        // "SawnTask.exe --snapshot out.png [seconds]": render the window to a PNG from
        // inside the app after a few seconds, then exit. Works even when the monitor is
        // asleep, where screen capture of GPU-drawn windows returns blank images.
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            int seconds = e.Args.Length >= 3 && int.TryParse(e.Args[2], out int n) ? n : 8;
            window.DiskPage.Automated = true; // no dialogs that would wait for a click
            if (e.Args.Length >= 4 && int.TryParse(e.Args[3], out int page)) window.Page = page;
            // Optional: start a disk scan, then open the largest path through the tree.
            if (e.Args.Length >= 5) window.Dispatcher.BeginInvoke(async () =>
            {
                await window.DiskPage.StartScan(e.Args[4]);
                window.DiskPage.ExpandLargestPath();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var content = (FrameworkElement)window.Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var file = File.Create(e.Args[1])) png.Save(file);

                // SAWNTASK_DIALOG=1: also save pictures of two themed message boxes.
                if (Environment.GetEnvironmentVariable("SAWNTASK_DIALOG") == "1")
                {
                    string dialogStem = Path.Combine(Path.GetDirectoryName(e.Args[1])!, Path.GetFileNameWithoutExtension(e.Args[1]));
                    Ui.ThemedMessageBox.SaveSnapshot(dialogStem + "-dialog1.png", Ui.Loc.F("Rec.Saved", 1234, "00:10:17", @"C:\Users\user\AppData\Local\SawnTask\recordings\rec-20261019-120000.sawn"),
                        Ui.Loc.T("Rec.SavedTitle"), MessageBoxButton.OK, MessageBoxImage.Information, (Ui.Loc.T("Rec.OpenFolder"), () => { }));
                    Ui.ThemedMessageBox.SaveSnapshot(dialogStem + "-dialog2.png", Ui.Loc.F("Msg.EndTask", "example.exe", 1234),
                        Ui.Loc.T("Proc.EndTask"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                }

                // SAWNTASK_SNAPSHOT_EXTRA=1: also save the Processes right-click menu (menus are
                // separate popup windows, so the window picture never contains them) and two
                // pictures of the CPU graph 300 ms apart (they differ only if the 60 fps slide works).
                if (Environment.GetEnvironmentVariable("SAWNTASK_SNAPSHOT_EXTRA") == "1")
                {
                    string stem = Path.Combine(Path.GetDirectoryName(e.Args[1])!, Path.GetFileNameWithoutExtension(e.Args[1]));
                    var menu = window.ProcGrid.ContextMenu;
                    menu.PlacementTarget = window.ProcGrid;
                    if (window.ProcGrid.Items.OfType<Ui.ProcessRow>().FirstOrDefault() is { } first) window.MarkCurrentPriority(first);
                    menu.IsOpen = true;
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    SaveVisual(menu, stem + "-menu.png");
                    window.PriorityMenu.IsSubmenuOpen = true;
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    if (window.PriorityMenu.Template.FindName("PART_Popup", window.PriorityMenu) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement sub })
                        SaveVisual(sub, stem + "-priority.png");
                    menu.IsOpen = false;
                    SaveVisual(window.CpuGraph, stem + "-graph1.png");
                    string status1 = window.StatusText.Text; // contains the generation (sample number)
                    var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    later.Tick += (_, _) =>
                    {
                        later.Stop();
                        SaveVisual(window.CpuGraph, stem + "-graph2.png");
                        File.WriteAllLines(stem + "-graph.txt", [status1, window.StatusText.Text]);
                        window.Close();
                    };
                    later.Start();
                    return;
                }
                window.Close();
            };
            timer.Start();
        }
    }
}
