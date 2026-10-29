using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Albumen.App.Shell;
using Albumen.App.Themes;
using Albumen.Domain.Review;

namespace Albumen.App;

public partial class SurvivorOverrideDialog : Window
{
    private SurvivorOverrideDialogResult result = SurvivorOverrideDialogResult.Cancel;
    private bool explicitlyCompleted;

    public SurvivorOverrideDialog(OverrideProposalToken proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        InitializeComponent();
        // An owned window does not inherit the owner's resources, so it takes the palette itself.
        AlbumenPalette.Apply(Resources, SystemParameters.HighContrast);
        ConsequenceTextVersion = proposal.ConsequenceTextVersion;
        DataContext = this;
    }

    public string ConsequenceTextVersion { get; }
    public SurvivorOverrideDialogResult Result => result;

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        OverrideConsequenceText.Focus();
        Keyboard.Focus(OverrideConsequenceText);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape) return;
        eventArgs.Handled = true;
        Complete(SurvivorOverrideDialogResult.Cancel);
    }

    protected void OnCancel(object sender, RoutedEventArgs eventArgs) =>
        Complete(SurvivorOverrideDialogResult.Cancel);

    public void OnAffirm(object sender, RoutedEventArgs eventArgs) =>
        Complete(SurvivorOverrideDialogResult.Affirm);

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (!explicitlyCompleted) result = SurvivorOverrideDialogResult.Cancel;
    }

    private void Complete(SurvivorOverrideDialogResult completed)
    {
        explicitlyCompleted = true;
        result = completed;
        Close();
    }
}
