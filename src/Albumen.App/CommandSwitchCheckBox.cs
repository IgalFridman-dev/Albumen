using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Albumen.App;

// A command-bound switch. WPF's built-in Toggle pattern flips IsChecked without
// running the bound command, so Narrator or UI Automation could show a switch as
// on while the workflow kept the old setting. Automation activates it like a click.
public class CommandSwitchCheckBox : CheckBox
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new CommandSwitchCheckBoxAutomationPeer(this);

    internal void ActivateFromAutomation() => OnClick();
}

internal sealed class CommandSwitchCheckBoxAutomationPeer(CommandSwitchCheckBox owner)
    : CheckBoxAutomationPeer(owner), IToggleProvider
{
    private CommandSwitchCheckBox SwitchOwner => (CommandSwitchCheckBox)Owner;

    void IToggleProvider.Toggle()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        SwitchOwner.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(SwitchOwner.ActivateFromAutomation));
    }

    ToggleState IToggleProvider.ToggleState => SwitchOwner.IsChecked switch
    {
        true => ToggleState.On,
        false => ToggleState.Off,
        null => ToggleState.Indeterminate,
    };
}
