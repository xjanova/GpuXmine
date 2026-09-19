using System.ComponentModel;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Windows;
using GpuxMine.Core.Updates;

namespace GpuxMine.App.Shell;

/// <summary>
/// What the program checks before it opens, said out loud.
/// </summary>
/// <remarks>
/// <para>
/// A node has real work to do at startup — read its identity, open a ledger
/// that may be damaged, find ComfyUI, reach the relay — and until now all of it
/// happened behind a window that either appeared configured or did not, with no
/// account of how it got there. When the Settings screen showed "not
/// registered" on a machine that plainly was, there was nothing to look at.
/// </para>
/// <para>
/// So each step names itself and reports what it actually found, and the same
/// lines go to the activity log. The splash is not decoration with a fake
/// progress bar: every step here is work the program was doing anyway.
/// </para>
/// </remarks>
public partial class SplashWindow : Window, INotifyPropertyChanged
{
    private const double TrackWidth = 424;

    public SplashWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public string Version => SelfUpdater.CurrentVersion;

    private string _stepText = "กำลังเริ่มต้น…";
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }

    private string _detailText = "";
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    private int _step;
    private int _totalSteps = 1;

    public string StepCountText => $"{_step}/{_totalSteps}";
    public double BarWidth => _totalSteps == 0 ? 0 : TrackWidth * _step / _totalSteps;

    public void Begin(int totalSteps)
    {
        _totalSteps = Math.Max(totalSteps, 1);
        _step = 0;
        Raise(nameof(StepCountText)); Raise(nameof(BarWidth));
    }

    /// <summary>
    /// The shortest time a step is allowed to stay on screen.
    /// </summary>
    /// <remarks>
    /// Startup finished in under a second, so the first build of this screen
    /// flashed and vanished before any of it could be read — which defeats the
    /// point of saying what is being checked. A floor per step gives about a
    /// second and a quarter in total, and adds nothing when the work itself
    /// takes longer.
    /// </remarks>
    private static readonly TimeSpan MinimumOnScreen = TimeSpan.FromMilliseconds(260);

    private DateTime _stepStarted = DateTime.UtcNow;

    /// <summary>Names the step about to run. The detail arrives when it finishes.</summary>
    public void Step(string what)
    {
        StepText = what;
        DetailText = "";
        _stepStarted = DateTime.UtcNow;
        Pump();
    }

    /// <summary>What the step found — the part worth reading.</summary>
    public void Done(string detail)
    {
        _step++;
        DetailText = detail;
        Raise(nameof(StepCountText)); Raise(nameof(BarWidth));
        Pump();

        // Held after the detail is on screen, never before: the owner should be
        // reading the answer during the pause, not an empty line.
        TimeSpan left = MinimumOnScreen - (DateTime.UtcNow - _stepStarted);
        if (left > TimeSpan.Zero) Wait(left);
    }

    /// <summary>
    /// Waits without freezing the window.
    /// </summary>
    /// <remarks>
    /// Thread.Sleep on the UI thread would stop the splash repainting, so the
    /// pause meant to make it readable would be the thing that blanked it.
    /// </remarks>
    private void Wait(TimeSpan howLong)
    {
        var until = DateTime.UtcNow + howLong;
        while (DateTime.UtcNow < until)
        {
            Pump();
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// Lets the window paint between steps.
    /// </summary>
    /// <remarks>
    /// Startup runs on the UI thread, so without this the splash would show its
    /// first frame and then freeze until everything was finished — which is the
    /// opposite of the point. Background priority: the render pass and nothing
    /// queued behind it.
    /// </remarks>
    private void Pump() =>
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
