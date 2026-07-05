namespace AdamE.MauiRouter.Requests;

/// <summary>
/// Identifies where a router navigation request came from. The source is part of the router's
/// decision context so the app and router can distinguish external and internal requests, apply
/// different policy and UX behavior, and enrich diagnostics and persisted history.
/// </summary>
public enum NavigationRequestSource
{
    /// <summary>
    /// The request source is not known or was not classified by the caller.
    /// </summary>
    Unknown,

    /// <summary>
    /// The request came from a platform app-link or deep-link handoff.
    /// </summary>
    AppLink,

    /// <summary>
    /// The request came from a push-notification interaction.
    /// </summary>
    Push,

    /// <summary>
    /// The request came from a scanned QR code.
    /// </summary>
    QrCode,

    /// <summary>
    /// The request was initiated by in-app code or UI.
    /// </summary>
    InAppCommand,

    /// <summary>
    /// The request was created by tests or test helpers.
    /// </summary>
    Test = 6,

    /// <summary>
    /// The request was synthesized while reconciling native navigation back into router state.
    /// </summary>
    NativeReconciliation = 7
}
