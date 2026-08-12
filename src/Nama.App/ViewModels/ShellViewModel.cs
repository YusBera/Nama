using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nama.App.Services;
using Nama.Core.Identification;
using Nama.Core.Models;

namespace Nama.App.ViewModels;

/// <summary>
/// Owns the four-step flow and the state shared between steps.
/// <para>
/// The steps are a straight line — select, identify, artwork, done — because the whole
/// product claim is that adding a game is one short path with no detours.
/// </para>
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(AppServices services)
    {
        Services = services;

        SelectStep = new SelectViewModel(this);
        IdentifyStep = new IdentifyViewModel(this);
        ArtworkStep = new ArtworkViewModel(this);
        SuccessStep = new SuccessViewModel(this);

        CurrentStep = SelectStep;
    }

    public AppServices Services { get; }

    public SelectViewModel SelectStep { get; }

    public IdentifyViewModel IdentifyStep { get; }

    public ArtworkViewModel ArtworkStep { get; }

    public SuccessViewModel SuccessStep { get; }

    [ObservableProperty]
    private ObservableObject currentStep;

    [ObservableProperty]
    private int stepNumber = 1;

    /// <summary>Result of reading the selected path. Set once, read by later steps.</summary>
    public ExtractionResult? Extraction { get; private set; }

    /// <summary>The game the user confirmed.</summary>
    public GameCandidate? ConfirmedGame { get; private set; }

    public bool CanGoBack => StepNumber is 2 or 3;

    partial void OnStepNumberChanged(int value) => OnPropertyChanged(nameof(CanGoBack));

    /// <summary>Entry point for a path from the file picker, a drop, or the command line.</summary>
    public async Task StartWithPathAsync(string path)
    {
        GoTo(IdentifyStep, 2);
        await IdentifyStep.LoadAsync(path).ConfigureAwait(true);
    }

    public void SetExtraction(ExtractionResult extraction) => Extraction = extraction;

    public async Task ConfirmGameAsync(GameCandidate game)
    {
        ConfirmedGame = game;
        GoTo(ArtworkStep, 3);
        await ArtworkStep.LoadAsync(game).ConfigureAwait(true);
    }

    public void ShowSuccess() => GoTo(SuccessStep, 4);

    [RelayCommand]
    private void Back()
    {
        switch (StepNumber)
        {
            case 2:
                GoTo(SelectStep, 1);
                break;
            case 3:
                GoTo(IdentifyStep, 2);
                break;
        }
    }

    /// <summary>Returns to the start so another game can be added without relaunching.</summary>
    [RelayCommand]
    public void Reset()
    {
        Extraction = null;
        ConfirmedGame = null;

        IdentifyStep.Clear();
        ArtworkStep.Clear();

        GoTo(SelectStep, 1);
    }

    private void GoTo(ObservableObject step, int number)
    {
        CurrentStep = step;
        StepNumber = number;
    }
}
