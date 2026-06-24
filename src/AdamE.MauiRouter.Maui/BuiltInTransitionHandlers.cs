using AdamE.MauiRouter.Plans;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal sealed class BuiltInFadeTransitionHandler : IMauiNavigationTransitionHandler<FadeNavigationTransition>
{
    public async ValueTask ApplyAsync(
        MauiNavigationTransitionContext<FadeNavigationTransition> context,
        CancellationToken cancellationToken = default)
    {
        var duration = context.Transition.Duration ?? TimeSpan.FromMilliseconds(220);
        var target = IsForward(context.Operation) ? context.TargetPage : context.SourcePage;

        if (IsForward(context.Operation))
        {
            await context.ExecuteNativeOperationAsync(false, cancellationToken);
            await MauiNativeTransitionAnimator.FadeInAsync(target, duration, cancellationToken);
            return;
        }

        await MauiNativeTransitionAnimator.FadeOutAsync(target, duration, cancellationToken);
        await context.ExecuteNativeOperationAsync(false, cancellationToken);
    }

    private static bool IsForward(MauiNavigationTransitionOperation operation)
    {
        return operation is MauiNavigationTransitionOperation.StackPush or MauiNavigationTransitionOperation.ModalPush;
    }
}

internal sealed class BuiltInSlideTransitionHandler : IMauiNavigationTransitionHandler<SlideNavigationTransition>
{
    public async ValueTask ApplyAsync(
        MauiNavigationTransitionContext<SlideNavigationTransition> context,
        CancellationToken cancellationToken = default)
    {
        var duration = context.Transition.Duration ?? TimeSpan.FromMilliseconds(260);
        var target = IsForward(context.Operation) ? context.TargetPage : context.SourcePage;

        if (IsForward(context.Operation))
        {
            await context.ExecuteNativeOperationAsync(false, cancellationToken);
            await MauiNativeTransitionAnimator.SlideInAsync(target, context.Transition.Direction, duration, cancellationToken);
            return;
        }

        await MauiNativeTransitionAnimator.SlideOutAsync(target, context.Transition.Direction, duration, cancellationToken);
        await context.ExecuteNativeOperationAsync(false, cancellationToken);
    }

    private static bool IsForward(MauiNavigationTransitionOperation operation)
    {
        return operation is MauiNavigationTransitionOperation.StackPush or MauiNavigationTransitionOperation.ModalPush;
    }
}

internal sealed class BuiltInSharedElementTransitionHandler : IMauiNavigationTransitionHandler<SharedElementNavigationTransition>
{
    private readonly MauiNavigationTransitionService _transitions;

    public BuiltInSharedElementTransitionHandler(MauiNavigationTransitionService transitions)
    {
        _transitions = transitions;
    }

    public async ValueTask ApplyAsync(
        MauiNavigationTransitionContext<SharedElementNavigationTransition> context,
        CancellationToken cancellationToken = default)
    {
        var duration = context.Transition.Duration ?? TimeSpan.FromMilliseconds(280);
        var sourceElements = context.Transition.Elements
            .Select(pair => MauiSharedElementLookup.Find(context.SourcePage, pair.SourceId))
            .ToArray();
        var missingSource = sourceElements.Length == 0 || sourceElements.Any(element => element is null);
        var capturedSourceElements = missingSource
            ? null
            : MauiNativeTransitionAnimator.CaptureSharedElements(sourceElements!);
        var missingSourceCapture = !missingSource && capturedSourceElements is null;
        var targetElementsBeforeNavigation = context.Transition.Elements
            .Select(pair => MauiSharedElementLookup.Find(context.TargetPage, pair.DestinationId))
            .ToArray();
        var targetElementOpacities = HideTargetElements(targetElementsBeforeNavigation);

        try
        {
            await context.ExecuteNativeOperationAsync(false, cancellationToken);

            var targetElements = context.Transition.Elements
                .Select(pair => MauiSharedElementLookup.Find(context.TargetPage, pair.DestinationId))
                .ToArray();
            var missingTarget = targetElements.Length == 0 || targetElements.Any(element => element is null);

            if (missingSource || missingSourceCapture || missingTarget)
            {
                _transitions.WriteFallback(
                    context.OperationId,
                    context.Transition,
                    context.Operation,
                    "Shared element transition fell back because one or more source or destination elements were not available.");

                await ApplyFallbackAsync(context, cancellationToken);
                return;
            }

            await MauiNativeTransitionAnimator.SharedElementAsync(
                sourceElements!,
                targetElements!,
                capturedSourceElements,
                duration,
                cancellationToken);
        }
        finally
        {
            RestoreTargetElements(targetElementsBeforeNavigation, targetElementOpacities);
        }
    }

    private static double[] HideTargetElements(IReadOnlyList<VisualElement?> targetElements)
    {
        var opacities = new double[targetElements.Count];
        for (var i = 0; i < targetElements.Count; i++)
        {
            opacities[i] = targetElements[i]?.Opacity ?? 1;
            if (targetElements[i] is { } target)
            {
                target.Opacity = 0;
            }
        }

        return opacities;
    }

    private static void RestoreTargetElements(IReadOnlyList<VisualElement?> targetElements, IReadOnlyList<double> opacities)
    {
        var count = Math.Min(targetElements.Count, opacities.Count);
        for (var i = 0; i < count; i++)
        {
            if (targetElements[i] is { } target)
            {
                target.Opacity = opacities[i];
            }
        }
    }

    private static ValueTask ApplyFallbackAsync(
        MauiNavigationTransitionContext<SharedElementNavigationTransition> context,
        CancellationToken cancellationToken)
    {
        var fallback = context.Transition.Fallback ?? new FadeNavigationTransition(context.Transition.Duration);
        return fallback switch
        {
            FadeNavigationTransition fade => MauiNativeTransitionAnimator.FadeInAsync(context.TargetPage, fade.Duration ?? TimeSpan.FromMilliseconds(220), cancellationToken),
            SlideNavigationTransition slide => MauiNativeTransitionAnimator.SlideInAsync(context.TargetPage, slide.Direction, slide.Duration ?? TimeSpan.FromMilliseconds(260), cancellationToken),
            _ => ValueTask.CompletedTask
        };
    }
}
