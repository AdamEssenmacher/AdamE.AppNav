using AdamE.AppNav.Maui;
using Microsoft.Maui.ApplicationModel;

namespace Commerce.Sample;

public partial class App : Application
{
	private readonly IAppNavStartupService _startup;

	public App(IAppNavStartupService startup)
	{
		_startup = startup;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(CreateLoadingPage());

		MainThread.BeginInvokeOnMainThread(async () =>
		{
			await _startup.StartAsync(window);
		});

		return window;
	}

	private static Page CreateLoadingPage()
	{
		return new ContentPage
		{
			Title = "Loading",
			Content = new Grid
			{
				Children =
				{
					new ActivityIndicator
					{
						IsRunning = true,
						HorizontalOptions = LayoutOptions.Center,
						VerticalOptions = LayoutOptions.Center
					}
				}
			}
		};
	}
}
