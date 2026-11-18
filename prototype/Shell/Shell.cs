using AdamE.AppNav.Oahu.Hosting;
using Oahu;

namespace PresenterProbe.Shell;

[OahuContract]
public interface IShellViewModel
{
    IOahuState<ShellTarget> Target { get; }
    IOahuState<string> Status { get; }
    void Report(NativeObservation observation);
    void RecordTrace(string message);
    void RunCommand(string command);
}

[OahuContract]
public interface IPageViewModel
{
    IOahuState<string> Title { get; }
}

public sealed class PageViewModel(string title) : IPageViewModel
{
    public IOahuState<string> Title { get; } = new StateSource<string>(title);
}

public sealed class ShellViewModel(
    NavigationShellViewModel navigation, Action<string> trace, Action<string> command,
    Func<ShellViewModel, NativeObservation, bool> holdObservation) : IShellViewModel
{
    private readonly StateSource<string> status = new("Starting");

    public IOahuState<ShellTarget> Target => navigation.Target;
    public IOahuState<string> Status => status;
    public long Epoch => navigation.Epoch;
    public void Report(NativeObservation observation)
    {
        if (!holdObservation(this, observation))
            Forward(observation);
    }
    public void Forward(NativeObservation observation) => navigation.Report(observation);
    public void SetStatus(string message) => status.Set(message);
    public void RecordTrace(string message) => trace(message);
    public void RunCommand(string name) => command(name);
}
