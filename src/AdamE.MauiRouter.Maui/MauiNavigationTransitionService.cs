using System.Diagnostics;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal sealed class MauiNavigationTransitionService
{
    private readonly IServiceProvider _services;
    private readonly MauiRoutePresentationOptions _options;
    private readonly NavigationDiagnostics _diagnostics;

    public MauiNavigationTransitionService(
        IServiceProvider services,
        MauiRoutePresentationOptions options,
        NavigationDiagnostics? diagnostics = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        transition ??= _options.Transitions.DefaultTransition ?? new NoNavigationTransition();
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

        return operation is MauiNavigationTransitionOperation.StackPop or MauiNavigationTransitionOperation.ModalPop
            ? sourcePage
            : targetPage;
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
            NavigationDiagnosticSeverity.Warning);
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
                if (_options.Transitions.TryCreateHandler(_services, fade.GetType(), out var fadeHandler))
                {
                    await ((IMauiNavigationTransitionHandler<FadeNavigationTransition>)fadeHandler)
                        .ApplyAsync(CreateContext(fade), cancellationToken)
                        ;
                    return;
                }

                await new BuiltInFadeTransitionHandler().ApplyAsync(CreateContext(fade), cancellationToken);
                return;
            case SlideNavigationTransition slide:
                if (_options.Transitions.TryCreateHandler(_services, slide.GetType(), out var slideHandler))
                {
                    await ((IMauiNavigationTransitionHandler<SlideNavigationTransition>)slideHandler)
                        .ApplyAsync(CreateContext(slide), cancellationToken)
                        ;
                    return;
                }

                await new BuiltInSlideTransitionHandler().ApplyAsync(CreateContext(slide), cancellationToken);
                return;
            case SharedElementNavigationTransition shared:
                if (_options.Transitions.TryCreateHandler(_services, shared.GetType(), out var sharedHandler))
                {
                    await ((IMauiNavigationTransitionHandler<SharedElementNavigationTransition>)sharedHandler)
                        .ApplyAsync(CreateContext(shared), cancellationToken)
                        ;
                    return;
                }

                await new BuiltInSharedElementTransitionHandler(this).ApplyAsync(CreateContext(shared), cancellationToken);
                return;
            default:
                if (_options.Transitions.TryCreateHandler(_services, transition.GetType(), out var handler))
                {
                    await InvokeCustomHandlerAsync(handler, transition, operation, sourcePage, targetPage, sourceEntry, targetEntry, operationId, executeNativeOperationAsync, cancellationToken)
                        ;
                    return;
                }

                WriteFallback(operationId, transition, operation, $"No MAUI transition handler is registered for '{transition.GetType().FullName}'.");
                await executeNativeOperationAsync(false, cancellationToken);
                return;
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

    private static ValueTask InvokeCustomHandlerAsync(
        object handler,
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
        var contextType = typeof(MauiNavigationTransitionContext<>).MakeGenericType(transition.GetType());
        var constructor = contextType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single();
        var context = constructor.Invoke(new object?[]
        {
            transition,
            operation,
            sourcePage,
            targetPage,
            sourceEntry,
            targetEntry,
            operationId,
            executeNativeOperationAsync
        });
        var method = handler.GetType().GetMethod(nameof(IMauiNavigationTransitionHandler<NavigationTransition>.ApplyAsync))
            ?? throw new InvalidOperationException($"Transition handler '{handler.GetType().FullName}' does not expose ApplyAsync.");
        return (ValueTask)method.Invoke(handler, new[] { context, cancellationToken })!;
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
}
