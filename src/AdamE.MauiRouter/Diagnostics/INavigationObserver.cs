namespace AdamE.MauiRouter.Diagnostics;

public interface INavigationObserver
{
    void OnNavigationEvent(NavigationDiagnosticEvent diagnosticEvent);
}
