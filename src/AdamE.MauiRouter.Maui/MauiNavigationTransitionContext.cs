using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal sealed class MauiNavigationTransitionContext<TTransition>
    where TTransition : NavigationTransition
{
    internal MauiNavigationTransitionContext(
        TTransition transition,
        MauiNavigationTransitionOperation operation,
        Page? sourcePage,
        Page? targetPage,
        RouteEntry? sourceEntry,
        RouteEntry? targetEntry,
        string operationId,
        Func<bool, CancellationToken, ValueTask<Page?>> executeNativeOperationAsync)
    {
        Transition = transition;
        Operation = operation;
        SourcePage = sourcePage;
        TargetPage = targetPage;
        SourceEntry = sourceEntry;
        TargetEntry = targetEntry;
        OperationId = operationId;
        ExecuteNativeOperationAsync = executeNativeOperationAsync;
    }

    public TTransition Transition { get; }

    public MauiNavigationTransitionOperation Operation { get; }

    public Page? SourcePage { get; }

    public Page? TargetPage { get; }

    public object? SourcePlatformView => SourcePage?.Handler?.PlatformView;

    public object? TargetPlatformView => TargetPage?.Handler?.PlatformView;

    public RouteEntry? SourceEntry { get; }

    public RouteEntry? TargetEntry { get; }

    public string OperationId { get; }

    public Func<bool, CancellationToken, ValueTask<Page?>> ExecuteNativeOperationAsync { get; }
}
