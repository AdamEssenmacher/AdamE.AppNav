namespace AdamE.MauiRouter.Diagnostics;

public enum NavigationDiagnosticPhase
{
    Navigation,
    RouteMatching,
    RequestPolicy,
    Planning,
    PlanPolicy,
    Presentation,
    Persistence,
    Startup,
    Reconciliation,
    Back,
    AppLink,
    Diagnostics
}
