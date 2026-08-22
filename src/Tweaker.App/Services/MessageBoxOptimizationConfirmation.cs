using System.Windows;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Services;

public sealed class MessageBoxOptimizationConfirmation : IOptimizationConfirmation
{
    public bool Confirm(OptimizationReview review)
    {
        var warning = review.RequiresExperimentalWarning
            ? "\n\nEXPERIMENTAL WARNING: hardware-dependent changes can reduce stability. You explicitly accepted this warning in the review screen."
            : review.RequiresAdvancedWarning
                ? "\n\nAdvanced changes can alter performance, power use, or compatibility."
                : string.Empty;
        var elevation = review.PrivilegedCount > 0
            ? $"\n\n{review.PrivilegedCount} selected operation(s) will run in a scoped administrator worker."
            : string.Empty;
        return MessageBox.Show(
            $"Apply {review.OperationCount} reviewed operation(s)?{warning}{elevation}\n\nNo automatic restart will occur.",
            "Review selected changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}
