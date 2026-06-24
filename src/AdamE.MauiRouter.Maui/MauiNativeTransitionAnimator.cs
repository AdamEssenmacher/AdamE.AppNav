using AdamE.MauiRouter.Plans;
using Microsoft.Maui.Controls;

#pragma warning disable CS0618 // Cross-target fallback uses APIs that are obsolete only on .NET 10 MAUI.

#if IOS || MACCATALYST
using CoreGraphics;
using UIKit;
#elif ANDROID
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidView = Android.Views.View;
#endif

namespace AdamE.MauiRouter.Maui;

internal static class MauiNativeTransitionAnimator
{
#if IOS
    public const string PlatformName = "iOS";
#elif MACCATALYST
    public const string PlatformName = "MacCatalyst";
#elif ANDROID
    public const string PlatformName = "Android";
#else
    public const string PlatformName = "Unknown";
#endif

    public static async ValueTask FadeInAsync(Page? page, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (page is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
#if IOS || MACCATALYST
        if (page.Handler?.PlatformView is UIView view)
        {
            view.Alpha = 0;
            await AnimateUIViewAsync(duration, () => view.Alpha = 1);
            return;
        }
#elif ANDROID
        if (page.Handler?.PlatformView is AndroidView view)
        {
            view.Alpha = 0f;
            await AnimateAndroidViewAsync(view, duration, animator => animator.Alpha(1f));
            return;
        }
#endif
        page.Opacity = 0;
        await page.FadeTo(1, ToMilliseconds(duration));
    }

    public static async ValueTask FadeOutAsync(Page? page, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (page is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
#if IOS || MACCATALYST
        if (page.Handler?.PlatformView is UIView view)
        {
            await AnimateUIViewAsync(duration, () => view.Alpha = 0);
            view.Alpha = 1;
            return;
        }
#elif ANDROID
        if (page.Handler?.PlatformView is AndroidView view)
        {
            await AnimateAndroidViewAsync(view, duration, animator => animator.Alpha(0f));
            view.Alpha = 1f;
            return;
        }
#endif
        await page.FadeTo(0, ToMilliseconds(duration));
        page.Opacity = 1;
    }

    public static async ValueTask SlideInAsync(
        Page? page,
        NavigationSlideDirection direction,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (page is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (x, y) = Offset(page, direction);
#if IOS || MACCATALYST
        if (page.Handler?.PlatformView is UIView view)
        {
            view.Transform = CGAffineTransform.MakeTranslation((nfloat)x, (nfloat)y);
            await AnimateUIViewAsync(duration, () => view.Transform = CGAffineTransform.MakeIdentity());
            return;
        }
#elif ANDROID
        if (page.Handler?.PlatformView is AndroidView view)
        {
            view.TranslationX = (float)x;
            view.TranslationY = (float)y;
            await AnimateAndroidViewAsync(view, duration, animator => animator.TranslationX(0f).TranslationY(0f));
            return;
        }
#endif
        page.TranslationX = x;
        page.TranslationY = y;
        await page.TranslateTo(0, 0, ToMilliseconds(duration));
    }

    public static async ValueTask SlideOutAsync(
        Page? page,
        NavigationSlideDirection direction,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (page is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (x, y) = Offset(page, direction);
#if IOS || MACCATALYST
        if (page.Handler?.PlatformView is UIView view)
        {
            await AnimateUIViewAsync(duration, () => view.Transform = CGAffineTransform.MakeTranslation((nfloat)x, (nfloat)y));
            view.Transform = CGAffineTransform.MakeIdentity();
            return;
        }
#elif ANDROID
        if (page.Handler?.PlatformView is AndroidView view)
        {
            await AnimateAndroidViewAsync(view, duration, animator => animator.TranslationX((float)x).TranslationY((float)y));
            view.TranslationX = 0f;
            view.TranslationY = 0f;
            return;
        }
#endif
        await page.TranslateTo(x, y, ToMilliseconds(duration));
        page.TranslationX = 0;
        page.TranslationY = 0;
    }

    public static object? CaptureSharedElements(IReadOnlyList<VisualElement?> sourceElements)
    {
#if IOS || MACCATALYST
        var captures = new List<UIKitSharedElementCapture>();
        foreach (var sourceElement in sourceElements)
        {
            if (sourceElement?.Handler?.PlatformView is not UIView sourceView ||
                sourceView.Window is not { } window ||
                CreateSnapshot(sourceView) is not { } snapshot)
            {
                return null;
            }

            var startFrame = sourceView.ConvertRectToView(sourceView.Bounds, window);
            if (startFrame.IsEmpty)
            {
                return null;
            }

            snapshot.Frame = startFrame;
            captures.Add(new UIKitSharedElementCapture(snapshot, startFrame));
        }

        return captures.Count == 0 ? null : captures;
#elif ANDROID
        var captures = new List<AndroidSharedElementCapture>();
        foreach (var sourceElement in sourceElements)
        {
            if (sourceElement?.Handler?.PlatformView is not AndroidView sourceView ||
                sourceView.RootView is not ViewGroup root ||
                sourceView.Width <= 0 ||
                sourceView.Height <= 0)
            {
                return null;
            }

            var startFrame = GetAndroidFrameInRoot(sourceView, root);
            if (startFrame.Width() <= 0 || startFrame.Height() <= 0)
            {
                return null;
            }

            var bitmap = Bitmap.CreateBitmap(sourceView.Width, sourceView.Height, Bitmap.Config.Argb8888!);
            if (bitmap is null)
            {
                return null;
            }

            using var canvas = new Canvas(bitmap);
            sourceView.Draw(canvas);
            captures.Add(new AndroidSharedElementCapture(bitmap, startFrame));
        }

        return captures.Count == 0 ? null : captures;
#else
        return null;
#endif
    }

    public static async ValueTask SharedElementAsync(
        IReadOnlyList<VisualElement?> sourceElements,
        IReadOnlyList<VisualElement?> targetElements,
        object? capturedSourceElements,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if IOS || MACCATALYST
        if (capturedSourceElements is IReadOnlyList<UIKitSharedElementCapture> captures &&
            await TryAnimateUIKitSharedElementsAsync(captures, targetElements, duration, cancellationToken))
        {
            return;
        }
#elif ANDROID
        if (capturedSourceElements is IReadOnlyList<AndroidSharedElementCapture> captures &&
            await TryAnimateAndroidSharedElementsAsync(captures, targetElements, duration, cancellationToken))
        {
            return;
        }
#endif
        var targets = targetElements.Where(element => element is not null).Cast<VisualElement>().ToArray();
        foreach (var target in targets)
        {
#if IOS || MACCATALYST
            if (target.Handler?.PlatformView is UIView view)
            {
                view.Alpha = 0.15f;
                view.Transform = CGAffineTransform.MakeScale(0.92f, 0.92f);
            }
#elif ANDROID
            if (target.Handler?.PlatformView is AndroidView view)
            {
                view.Alpha = 0.15f;
                view.ScaleX = 0.92f;
                view.ScaleY = 0.92f;
            }
#else
            target.Opacity = 0.15;
            target.Scale = 0.92;
#endif
        }

        foreach (var target in targets)
        {
#if IOS || MACCATALYST
            if (target.Handler?.PlatformView is UIView view)
            {
                await AnimateUIViewAsync(duration, () =>
                {
                    view.Alpha = 1;
                    view.Transform = CGAffineTransform.MakeIdentity();
                });
                continue;
            }
#elif ANDROID
            if (target.Handler?.PlatformView is AndroidView view)
            {
                await AnimateAndroidViewAsync(view, duration, animator => animator.Alpha(1f).ScaleX(1f).ScaleY(1f));
                continue;
            }
#endif
            await Task.WhenAll(
                target.FadeTo(1, ToMilliseconds(duration)),
                target.ScaleTo(1, ToMilliseconds(duration)));
        }
    }

#if IOS || MACCATALYST
    private static async Task<bool> TryAnimateUIKitSharedElementsAsync(
        IReadOnlyList<UIKitSharedElementCapture> captures,
        IReadOnlyList<VisualElement?> targetElements,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var animations = new List<UIKitSharedElementAnimation>();
        var count = Math.Min(captures.Count, targetElements.Count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetElements[i]?.Handler?.PlatformView is not UIView targetView ||
                GetWindow(targetView) is not { } window)
            {
                continue;
            }

            var endFrame = targetView.ConvertRectToView(targetView.Bounds, window);
            if (endFrame.IsEmpty)
            {
                continue;
            }

            var capture = captures[i];
            capture.Snapshot.Frame = capture.StartFrame;
            animations.Add(new UIKitSharedElementAnimation(
                capture.Snapshot,
                targetView,
                window,
                endFrame,
                targetView.Alpha));
        }

        if (animations.Count == 0)
        {
            return false;
        }

        foreach (var animation in animations)
        {
            animation.TargetView.Alpha = 0;
            animation.Window.AddSubview(animation.Snapshot);
        }

        try
        {
            await AnimateUIViewAsync(duration, () =>
            {
                foreach (var animation in animations)
                {
                    animation.Snapshot.Frame = animation.EndFrame;
                }
            });
        }
        finally
        {
            foreach (var animation in animations)
            {
                animation.TargetView.Alpha = animation.OriginalTargetAlpha;
                animation.Snapshot.RemoveFromSuperview();
            }
        }

        return true;
    }

    private static UIView? CreateSnapshot(UIView sourceView)
    {
        if (sourceView.Bounds.IsEmpty)
        {
            return null;
        }

        return sourceView.SnapshotView(false);
    }

    private static UIView? GetWindow(UIView targetView)
    {
        return targetView.Window ?? targetView.Superview?.Window;
    }

    private sealed record UIKitSharedElementCapture(
        UIView Snapshot,
        CGRect StartFrame);

    private sealed record UIKitSharedElementAnimation(
        UIView Snapshot,
        UIView TargetView,
        UIView Window,
        CGRect EndFrame,
        nfloat OriginalTargetAlpha);
#endif

#if ANDROID
    private static async Task<bool> TryAnimateAndroidSharedElementsAsync(
        IReadOnlyList<AndroidSharedElementCapture> captures,
        IReadOnlyList<VisualElement?> targetElements,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var animations = new List<AndroidSharedElementAnimation>();
        var animatedBitmaps = new HashSet<Bitmap>(ReferenceEqualityComparer.Instance);
        var count = Math.Min(captures.Count, targetElements.Count);

        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 0; i < count; i++)
        {
            if (targetElements[i]?.Handler?.PlatformView is not AndroidView targetView ||
                targetView.RootView is not ViewGroup root ||
                root.Context is null)
            {
                continue;
            }

            var endFrame = GetAndroidFrameInRoot(targetView, root);
            if (endFrame.Width() <= 0 || endFrame.Height() <= 0)
            {
                continue;
            }

            var capture = captures[i];
            var overlay = new ImageView(root.Context)
            {
                Alpha = 1f
            };
            overlay.SetScaleType(ImageView.ScaleType.FitXy);
            overlay.SetImageBitmap(capture.Bitmap);
            overlay.SetX(capture.StartFrame.Left);
            overlay.SetY(capture.StartFrame.Top);

            var layoutParameters = new ViewGroup.LayoutParams(
                Math.Max(1, capture.StartFrame.Width()),
                Math.Max(1, capture.StartFrame.Height()));
            AddOverlay(root, overlay, layoutParameters);

            animations.Add(new AndroidSharedElementAnimation(
                overlay,
                root,
                targetView,
                capture.Bitmap,
                capture.StartFrame,
                endFrame,
                targetView.Alpha));
            animatedBitmaps.Add(capture.Bitmap);
        }

        if (animations.Count == 0)
        {
            DisposeAndroidCaptures(captures);
            return false;
        }

        foreach (var animation in animations)
        {
            animation.TargetView.Alpha = 0f;
        }

        try
        {
            var tasks = animations.Select(animation => AnimateAndroidOverlayAsync(animation, duration, cancellationToken));
            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var animation in animations)
            {
                animation.TargetView.Alpha = animation.OriginalTargetAlpha;
                RemoveOverlay(animation.Root, animation.Overlay);
                animation.Overlay.SetImageBitmap(null);
                animation.Bitmap.Dispose();
            }

            foreach (var capture in captures)
            {
                if (!animatedBitmaps.Contains(capture.Bitmap))
                {
                    capture.Bitmap.Dispose();
                }
            }
        }

        return true;
    }

    private static Task AnimateAndroidOverlayAsync(
        AndroidSharedElementAnimation animation,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startWidth = Math.Max(1, animation.StartFrame.Width());
        var startHeight = Math.Max(1, animation.StartFrame.Height());
        var scaleX = animation.EndFrame.Width() / (float)startWidth;
        var scaleY = animation.EndFrame.Height() / (float)startHeight;

        animation.Overlay.PivotX = 0f;
        animation.Overlay.PivotY = 0f;

        return AnimateAndroidViewAsync(
            animation.Overlay,
            duration,
            animator => animator
                .X(animation.EndFrame.Left)
                .Y(animation.EndFrame.Top)
                .ScaleX(scaleX)
                .ScaleY(scaleY));
    }

    private static Android.Graphics.Rect GetAndroidFrameInRoot(AndroidView view, AndroidView root)
    {
        var viewLocation = new int[2];
        var rootLocation = new int[2];
        view.GetLocationOnScreen(viewLocation);
        root.GetLocationOnScreen(rootLocation);

        var left = viewLocation[0] - rootLocation[0];
        var top = viewLocation[1] - rootLocation[1];
        return new Android.Graphics.Rect(
            left,
            top,
            left + view.Width,
            top + view.Height);
    }

    private static void AddOverlay(ViewGroup root, AndroidView overlay, ViewGroup.LayoutParams layoutParameters)
    {
        root.AddView(overlay, layoutParameters);
        overlay.BringToFront();
        if (OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            overlay.Elevation = float.MaxValue / 4;
        }
    }

    private static void RemoveOverlay(ViewGroup root, AndroidView overlay)
    {
        root.RemoveView(overlay);
    }

    private static void DisposeAndroidCaptures(IReadOnlyList<AndroidSharedElementCapture> captures)
    {
        foreach (var capture in captures)
        {
            capture.Bitmap.Dispose();
        }
    }

    private sealed record AndroidSharedElementCapture(
        Bitmap Bitmap,
        Android.Graphics.Rect StartFrame);

    private sealed record AndroidSharedElementAnimation(
        ImageView Overlay,
        ViewGroup Root,
        AndroidView TargetView,
        Bitmap Bitmap,
        Android.Graphics.Rect StartFrame,
        Android.Graphics.Rect EndFrame,
        float OriginalTargetAlpha);
#endif

    private static (double X, double Y) Offset(Page page, NavigationSlideDirection direction)
    {
        var width = page.Width > 0 ? page.Width : 320;
        var height = page.Height > 0 ? page.Height : 480;

        return direction switch
        {
            NavigationSlideDirection.Left => (width, 0),
            NavigationSlideDirection.Right => (-width, 0),
            NavigationSlideDirection.Up => (0, height),
            NavigationSlideDirection.Down => (0, -height),
            _ => (width, 0)
        };
    }

    private static uint ToMilliseconds(TimeSpan duration)
    {
        return (uint)Math.Max(1, duration.TotalMilliseconds);
    }

#if IOS || MACCATALYST
    private static Task AnimateUIViewAsync(TimeSpan duration, Action animation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UIView.Animate(
            duration.TotalSeconds,
            animation,
            () => completion.TrySetResult());
        return completion.Task;
    }
#endif

#if ANDROID
    private static Task AnimateAndroidViewAsync(
        Android.Views.View view,
        TimeSpan duration,
        Func<ViewPropertyAnimator, ViewPropertyAnimator> configure)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new AnimationEndListener(() => completion.TrySetResult());
        var animator = view.Animate();
        if (animator is null)
        {
            completion.TrySetResult();
            return completion.Task;
        }

        configure(animator.SetDuration((long)Math.Max(1, duration.TotalMilliseconds)))
            .SetListener(listener)
            .Start();
        return completion.Task;
    }

    private sealed class AnimationEndListener : Java.Lang.Object, Android.Animation.Animator.IAnimatorListener
    {
        private readonly Action _completed;

        public AnimationEndListener(Action completed)
        {
            _completed = completed;
        }

        public void OnAnimationCancel(Android.Animation.Animator animation)
        {
            _completed();
        }

        public void OnAnimationEnd(Android.Animation.Animator animation)
        {
            _completed();
        }

        public void OnAnimationRepeat(Android.Animation.Animator animation)
        {
        }

        public void OnAnimationStart(Android.Animation.Animator animation)
        {
        }
    }
#endif
}

#pragma warning restore CS0618
