using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace HoursTracker
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Hours Tracker", "Считает, сколько времени открыта Visual Studio", "1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class HoursTrackerPackage : AsyncPackage
    {
        public const string PackageGuidString = "b3f1c2a4-7d5e-4a8b-9c21-5e6f7a8b9c0d";
        public const string CmdSetGuidString = "1d9e4f60-2a3b-4c5d-8e7f-0a1b2c3d4e5f";
        public const int ShowTimeCmdId = 0x0100;

        private const int TickSeconds = 30;
        // Если между тиками прошло больше (сон/гибернация) — это время не считаем.
        private const int MaxCountedDeltaSeconds = 120;

        private static readonly string DataFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HoursTracker", "total_seconds.txt");

        private readonly object _lock = new object();
        private DateTime _sessionStart;
        private DateTime _lastTick;
        private Timer? _timer;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            _sessionStart = Process.GetCurrentProcess().StartTime;
            _lastTick = DateTime.UtcNow;

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is IMenuCommandService commandService)
            {
                var id = new CommandID(new Guid(CmdSetGuidString), ShowTimeCmdId);
                commandService.AddCommand(new MenuCommand(OnShowTime, id));
            }

            UpdateStatusBar();
            _timer = new Timer(_ => OnTick(), null,
                TimeSpan.FromSeconds(TickSeconds), TimeSpan.FromSeconds(TickSeconds));
        }

        private void OnTick()
        {
            AccumulateTotal();
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                UpdateStatusBar();
            });
        }

        /// <summary>Добавляет прошедшее с прошлого тика время к общему счётчику на диске.</summary>
        private void AccumulateTotal()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                long delta = (long)(now - _lastTick).TotalSeconds;
                if (delta <= 0) return;
                _lastTick = _lastTick.AddSeconds(delta);

                if (delta > MaxCountedDeltaSeconds) return;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
                    long total = ReadTotalSeconds() + delta;
                    File.WriteAllText(DataFile, total.ToString());
                }
                catch
                {
                    // Не мешаем работе IDE из-за проблем с файлом.
                }
            }
        }

        private static long ReadTotalSeconds()
        {
            try
            {
                if (File.Exists(DataFile) && long.TryParse(File.ReadAllText(DataFile).Trim(), out long v))
                    return v;
            }
            catch { }
            return 0;
        }

        private TimeSpan SessionTime => DateTime.Now - _sessionStart;

        private static string Format(TimeSpan t) =>
            $"{(int)t.TotalHours} ч {t.Minutes:D2} мин";

        private void UpdateStatusBar()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GetService(typeof(SVsStatusbar)) is IVsStatusbar bar)
            {
                bar.IsFrozen(out int frozen);
                if (frozen == 0)
                    bar.SetText($"VS открыта: {Format(SessionTime)}");
            }
        }

        private void OnShowTime(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            AccumulateTotal();

            var total = TimeSpan.FromSeconds(ReadTotalSeconds());
            VsShellUtilities.ShowMessageBox(
                this,
                $"Эта сессия: {Format(SessionTime)}\nВсего за всё время: {Format(total)}",
                "Hours Tracker",
                Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                AccumulateTotal(); // финальное сохранение при закрытии VS
            }
            base.Dispose(disposing);
        }
    }
}
