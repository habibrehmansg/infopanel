using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Services;
using Serilog;
using System;
using System.Threading.Tasks;

namespace InfoPanel.ViewModels;

public partial class AccountViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<AccountViewModel>();

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string? _avatarUrl;

    [ObservableProperty]
    private bool _isLoggingIn;

    [ObservableProperty]
    private string? _errorMessage;

    [RelayCommand]
    private async Task LoginWithDiscordAsync()
    {
        if (IsLoggingIn) return;

        IsLoggingIn = true;
        ErrorMessage = null;

        try
        {
            var token = await AuthService.StartOAuthFlowAsync();
            if (token != null)
            {
                AuthService.SaveToken(token);
                await FetchAndSetUserInfoAsync();
                IsLoggedIn = true;
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Sign-in was cancelled or timed out.";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Discord OAuth flow failed");
            ErrorMessage = "Sign-in failed. Please try again.";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            await InfoPanelApiService.Instance.Client.Post_LogoutAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Server logout call failed");
        }

        AuthService.ClearToken();
        IsLoggedIn = false;
        Username = string.Empty;
        AvatarUrl = null;
        ErrorMessage = null;
    }

    public async Task TryRestoreSessionAsync()
    {
        var token = AuthService.LoadAndRestoreToken();
        if (token == null) return;

        try
        {
            await FetchAndSetUserInfoAsync();
            IsLoggedIn = true;
            Logger.Information("Session restored for user {Username}", Username);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to validate stored token, clearing");
            AuthService.ClearToken();
        }
    }

    private async Task FetchAndSetUserInfoAsync()
    {
        var response = await InfoPanelApiService.Instance.Client.Get_GetCurrentUserAsync();
        Username = response.User.Username;
        AvatarUrl = response.User.Avatar;
    }
}
