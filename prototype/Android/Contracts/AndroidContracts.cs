using AdamE.AppNav.Oahu.Hosting;
using Oahu;

namespace PresenterProbe.Android;

[OahuContract]
public interface IDemoShellViewModel
{
    IOahuState<ShellTarget> Target { get; }
    IOahuState<string> Status { get; }
    IOahuState<long> CompletedBack { get; }
    Task RequestBack(long request);
    void Report(NativeObservation observation);
    void RunCommand(string command);
    void RecordTrace(string message);
}

[OahuContract]
public interface IPageViewModel
{
    IOahuState<string> Title { get; }
}
