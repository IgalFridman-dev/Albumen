using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.App.Shell;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly IChooseScopeWorkflow chooseScopeWorkflow;
    private ImmutableArray<PhaseItemViewModel> phases;
    private string instruction;

    public ShellViewModel(IChooseScopeWorkflow chooseScopeWorkflow)
    {
        this.chooseScopeWorkflow = chooseScopeWorkflow
            ?? throw new ArgumentNullException(nameof(chooseScopeWorkflow));
        phases = Project(InitialWorkflow.Create());
        instruction = "PhotoSift works offline. This workflow stays on this PC.";
        ChooseScopeCommand = new AsyncCommand(
            () => OpenChooseScopeAsync(CancellationToken.None));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<PhaseItemViewModel> Phases
    {
        get => phases;
        private set
        {
            phases = value;
            OnPropertyChanged();
        }
    }

    public string Instruction
    {
        get => instruction;
        private set
        {
            instruction = value;
            OnPropertyChanged();
        }
    }

    public AsyncCommand ChooseScopeCommand { get; }

    public async Task OpenChooseScopeAsync(CancellationToken cancellationToken)
    {
        var snapshot = await chooseScopeWorkflow.OpenAsync(
            new OpenChooseScopeCommand(),
            cancellationToken);

        Phases = Project(snapshot.Phases);
        Instruction = snapshot.Instruction;
    }

    private static ImmutableArray<PhaseItemViewModel> Project(
        ImmutableArray<WorkflowPhaseState> phases) =>
        phases
            .Select(phase => new PhaseItemViewModel(
                phase.Phase,
                phase.DisplayName,
                phase.Availability))
            .ToImmutableArray();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
