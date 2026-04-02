using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Slipsten.Views;

public enum PromptResult { None, Start, Cancel, KeepContinue, Discard }

public partial class WorkPromptWindow : Window
{
    public PromptResult Result { get; private set; } = PromptResult.None;

    private const int Width = 340;
    private const int Height = 130;

    public void UpdateMessage(string message) => MessageText.Text = message;

    public WorkPromptWindow(string message, PromptMode mode)
    {
        InitializeComponent();
        MessageText.Text = message;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Width, Height));
        WindowHelper.SetAppIcon(this);
        CenterOnScreen();

        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.CreateForDialog();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        BuildButtons(mode);
    }

    private void BuildButtons(PromptMode mode)
    {
        if (mode == PromptMode.Start)
        {
            var start = new Button { Content = "Start", Style = (Style)App.Current.Resources["AccentButtonStyle"] };
            start.Click += (_, _) => { Result = PromptResult.Start; Close(); };

            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) => { Result = PromptResult.Cancel; Close(); };

            ButtonPanel.Children.Add(start);
            ButtonPanel.Children.Add(cancel);
        }
        else // IdleReturn
        {
            var keep = new Button { Content = "Keep & Continue", Style = (Style)App.Current.Resources["AccentButtonStyle"] };
            keep.Click += (_, _) => { Result = PromptResult.KeepContinue; Close(); };

            var discard = new Button { Content = "Discard" };
            discard.Click += (_, _) => { Result = PromptResult.Discard; Close(); };

            ButtonPanel.Children.Add(keep);
            ButtonPanel.Children.Add(discard);
        }
    }

    private void CenterOnScreen()
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            (workArea.Width - Width) / 2 + workArea.X,
            (workArea.Height - Height) / 2 + workArea.Y));
    }
}

public enum PromptMode { Start, IdleReturn }
