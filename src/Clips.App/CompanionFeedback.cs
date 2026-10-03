using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clips.App;

public sealed class CompanionFeedback : ObservableObject
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Action<Exception> reportFailure;
    private readonly Func<string> idleMessage;
    private Func<Task<string?>>? action;
    private int revision;
    private string message = "Ready to capture";
    private string? actionLabel;
    public string Message => message;
    public string? ActionLabel => actionLabel;
    public bool HasAction => action != null;
    public IAsyncRelayCommand ActionCommand { get; }
    public CompanionFeedback(Action<Exception> reportFailure, Func<string>? idleMessage = null)
    {
        this.reportFailure = reportFailure;
        this.idleMessage = idleMessage ?? (() => "Ready to capture");
        ActionCommand = new AsyncRelayCommand(RunActionAsync);
        timer.Tick += (_, _) => { if (!ActionCommand.IsRunning) Clear(); };
    }
    public void Show(string text, string? label = null, Func<Task<string?>>? run = null, bool transient = true)
    {
        revision++; timer.Stop(); message = text; actionLabel = label; action = run;
        OnPropertyChanged(nameof(Message)); OnPropertyChanged(nameof(ActionLabel)); OnPropertyChanged(nameof(HasAction));
        if (transient) timer.Start();
    }
    public void Clear() => Show(idleMessage(), transient: false);
    public void Stop() => timer.Stop();
    private async Task RunActionAsync()
    {
        var run = action; var started = revision;
        if (run == null) return;
        try
        {
            var result = await run();
            // A later capture owns the confirmation and Undo; an older action cannot replace it.
            if (revision == started) { if (result == null) Clear(); else Show(result); }
        }
        catch (Exception ex)
        {
            reportFailure(ex);
            if (revision == started) Show("Couldn’t complete this action. Try again.", actionLabel, run, false);
        }
    }
}
