// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// The Sign in dialog (docs/features/F2.9-operator-sign-in.md, behaviors 4, 7, 8): a name and password posted to the
/// gateway, its refusal in words, and why it isn't offered over plain HTTP. The password is cleared after every try.
/// </summary>
public sealed partial class SignInViewModel(
    IOperatorSession session,
    Localizer text,
    bool needsHttps,
    Action signedIn,
    Action closed
) : ObservableObject
{
    /// <summary>The page came over plain HTTP from another machine: the dialog says why it can't sign in.</summary>
    public bool NeedsHttps { get; } = needsHttps;

    /// <summary>Whether the name and password fields show.</summary>
    public bool CanSignIn => !NeedsHttps;

    /// <summary>The explanation shown instead of the form over plain HTTP.</summary>
    public string? NeedsHttpsText { get; } = needsHttps ? text["Error.auth.needs-https"] : null;

    /// <summary>The operator's name.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The password, kept only until it's sent.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>Why the last try didn't sign in; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Whether a try is on its way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanTry() => CanSignIn && !IsBusy && Name.Trim().Length > 0 && Password.Length > 0;

    /// <summary>Signs in; on success the app starts again as the operator.</summary>
    [RelayCommand(CanExecute = nameof(CanTry))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        Error = null;
        var request = new SignInRequest(Name.Trim(), Password);
        Password = string.Empty;
        try
        {
            await session.SignInAsync(request);
            signedIn();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Error = text.Describe(ex);
        }
        catch (HttpRequestException)
        {
            Error = text["Auth.Unreachable"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Closes the dialog.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Password = string.Empty;
        closed();
    }
}
