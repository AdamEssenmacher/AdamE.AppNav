using System.Diagnostics;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal sealed class MauiNavigationTransitionService
{
    private readonly NavigationDiagnostics _diagnostics;

    public MauiNavigationTransitionService(
        NavigationDiagnostics? diagnostics = null)
    {
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;
    }

    public async ValueTask<Page?> ApplyAsync(
        NavigationTransition? transition,
        MauiNavigationTransitionOperation operation,
        Page? sourcePage,
        Page? targetPage,
        RouteEntry? sourceEntry,
        RouteEntry? targetEntry,
        string operationId,
        Func<bool, CancellationToken, ValueTask<Page?>> executeNativeOperationAsync,
        CancellationToken cancellationToken = default)
    {
        transition ??= new NoNavigationTransition();
        var timer = Stopwatch.StartNew();
        Write(
            NavigationDiagnosticEventKind.PresentationTransitionStarted,
            operationId,
            $"Presentation transition '{transition.GetType().Name}' started.",
            transition,
            operation);

        try
        {
            await ApplyCoreAsync(
                transition,
                operation,
                sourcePage,
                targetPage,
                sourceEntry,
                targetEntry,
                operationId,
                executeNativeOperationAsync,
                cancellationToken);
            Write(
                NavigationDiagnosticEventKind.PresentationTransitionCompleted,
                operationId,
                $"Presentation transition '{transition.GetType().Name}' completed.",
                transition,
                operation,
                duration: timer.Elapsed);
        }
        catch (Exception ex)
        {
            var data = Data(transition, operation, timer.Elapsed);
            data[NavigationDiagnosticDataKeys.ExceptionType] = ex.GetType().FullName;
            data[NavigationDiagnosticDataKeys.ExceptionMessage] = ex.Message;
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationTransitionFailed,
                operationId,
                $"Presentation transition '{transition.GetType().Name}' failed.",
                data);
            throw;
        }

        return CompletionPageFor(operation, sourcePage, targetPage);
    }

    internal void WriteFallback(
        string operationId,
        NavigationTransition transition,
        MauiNavigationTransitionOperation operation,
        string reason)
    {
        var data = Data(transition, operation);
        data[NavigationDiagnosticDataKeys.TransitionFallbackReason] = reason;
        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationTransitionFallback,
            operationId,
            reason,
            data,
            LogLevel.Warning);
    }

    internal ValueTask ApplyFallbackAsync(
        NavigationTransition? fallbackTransition,
        MauiNavigationTransitionOperation operation,
        Page? sourcePage,
        Page? targetPage,
        RouteEntry? sourceEntry,
        RouteEntry? targetEntry,
        string operationId,
        TimeSpan? defaultFadeDuration,
        Func<bool, CancellationToken, ValueTask<Page?>> executeNativeOperationAsync,
        CancellationToken cancellationToken)
    {
        var resolvedTransition = fallbackTransition ?? new FadeNavigationTransition(defaultFadeDuration);
        return ApplyCoreAsync(
            resolvedTransition,
            operation,
            sourcePage,
            targetPage,
            sourceEntry,
            targetEntry,
            operationId,
            executeNativeOperationAsync,
            cancellationToken);
    }

    private async ValueTask ApplyCoreAsync(
        NavigationTransition transition,
        MauiNavigationTransitionOperation operation,
        Page? sourcePage,
        Page? targetPage,
        RouteEntry? sourceEntry,
        RouteEntry? targetEntry,
        string operationId,
        Func<bool, CancellationToken, ValueTask<Page?>> executeNativeOperationAsync,
        CancellationToken cancellationToken)
    {
        switch (transition)
        {
            case NoNavigationTransition noTransition:
                await CreateContext(noTransition).ExecuteNativeOperationAsync(false, cancellationToken);
                return;
            case PlatformDefaultNavigationTransition platformDefault:
                await CreateContext(platformDefault).ExecuteNativeOperationAsync(true, cancellationToken);
                return;
            case FadeNavigationTransition fade:
                await new BuiltInFadeTransitionHandler().ApplyAsync(CreateContext(fade), cancellationToken);
                return;
            case SlideNavigationTransition slide:
                await new BuiltInSlideTransitionHandler().ApplyAsync(CreateContext(slide), cancellationToken);
                return;
            case SharedElementNavigationTransition shared:
                await new BuiltInSharedElementTransitionHandler(this).ApplyAsync(CreateContext(shared), cancellationToken);
                return;
            default:
                throw new NotSupportedException(
                    $"Navigation transition '{transition.GetType().FullName}' is not supported by the MAUI adapter.");
        }

        MauiNavigationTransitionContext<TTransition> CreateContext<TTransition>(TTransition typedTransition)
            where TTransition : NavigationTransition
        {
            return new MauiNavigationTransitionContext<TTransition>(
                typedTransition,
                operation,
                sourcePage,
                targetPage,
                sourceEntry,
                targetEntry,
                operationId,
                executeNativeOperationAsync);
        }
    }

    private void Write(
        NavigationDiagnosticEventKind kind,
        string operationId,
        string message,
        NavigationTransition transition,
        MauiNavigationTransitionOperation operation,
        TimeSpan? duration = null)
    {
        _diagnostics.Write(kind, operationId, message, Data(transition, operation, duration));
    }

    private static Dictionary<string, object?> Data(
        NavigationTransition transition,
        MauiNavigationTransitionOperation operation,
        TimeSpan? duration = null)
    {
        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.TransitionType] = transition.GetType().FullName,
            [NavigationDiagnosticDataKeys.TransitionOperation] = operation.ToString(),
            [NavigationDiagnosticDataKeys.Platform] = MauiNativeTransitionAnimator.PlatformName
        };

        if (duration is not null)
        {
            data[NavigationDiagnosticDataKeys.TransitionDurationMs] = duration.Value.TotalMilliseconds;
        }

        if (transition is SharedElementNavigationTransition shared)
        {
            data[NavigationDiagnosticDataKeys.TransitionElementIds] =
                string.Join(",", shared.Elements.Select(element => $"{element.SourceId}->{element.DestinationId}"));
        }

        return data;
    }

    private static Page? CompletionPageFor(
        MauiNavigationTransitionOperation operation,
        Page? sourcePage,
        Page? targetPage)
    {
        return operation is MauiNavigationTransitionOperation.StackPop or MauiNavigationTransitionOperation.ModalPop
            ? sourcePage
            : targetPage;
    }
}
