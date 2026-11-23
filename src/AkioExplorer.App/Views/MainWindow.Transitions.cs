using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace AkioExplorer.App.Views;

/// <summary>
/// Light open/close transitions: content fades in and settles from 98.5 % scale when the window appears, and fades
/// out quickly before it hides or quits. Composition only (off the UI thread), skipped when Windows animations are off.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(120);
    private const float RestScale = 0.985f;

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private Task? _exitTransition;

    private Visual RootVisual => ElementCompositionPreview.GetElementVisual(RootGrid);

    private void PlayEnterTransition()
    {
        if (!RootGrid.IsLoaded) return; // the first show plays from OnRootLoaded, once the size is known
        var visual = RootVisual;
        if (!_uiSettings.AnimationsEnabled)
        {
            ResetTransition();
            return;
        }
        var c = visual.Compositor;
        visual.CenterPoint = new Vector3((float)RootGrid.ActualWidth / 2, (float)RootGrid.ActualHeight / 2, 0);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)); // decelerate

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = EnterDuration;

        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(RestScale, RestScale, 1));
        scale.InsertKeyFrame(1, Vector3.One, ease);
        scale.Duration = EnterDuration;

        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation(nameof(Visual.Scale), scale);
    }

    /// <summary>Completes when the content has faded out (at once if the window isn't on screen).</summary>
    private Task PlayExitTransitionAsync()
    {
        if (_exitTransition is { IsCompleted: false } running) return running;
        bool onScreen = AppWindow.IsVisible && AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
        if (!onScreen || !_uiSettings.AnimationsEnabled || !RootGrid.IsLoaded) return Task.CompletedTask;

        var visual = RootVisual;
        var c = visual.Compositor;
        visual.CenterPoint = new Vector3((float)RootGrid.ActualWidth / 2, (float)RootGrid.ActualHeight / 2, 0);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)); // accelerate

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0, ease);
        fade.Duration = ExitDuration;

        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, new Vector3(RestScale, RestScale, 1), ease);
        scale.Duration = ExitDuration;

        var done = new TaskCompletionSource();
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation(nameof(Visual.Scale), scale);
        batch.Completed += (_, _) => done.TrySetResult();
        batch.End();
        return _exitTransition = done.Task;
    }

    /// <summary>Back to fully visible, so a window shown without a transition never stays blank.</summary>
    private void ResetTransition()
    {
        var visual = RootVisual;
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Scale));
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
    }
}
