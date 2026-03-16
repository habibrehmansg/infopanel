using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.ApiClient;
using InfoPanel.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;

namespace InfoPanel.ViewModels;

public partial class AccountViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<AccountViewModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string? _avatarUrl;

    [ObservableProperty]
    private bool _isLoggingIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    private bool _isRestoring;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isRefreshingSessions;

    public bool ShowSignIn => !IsLoggedIn && !IsRestoring;

    public ObservableCollection<Sessions> Sessions { get; } = [];

    private TaskCompletionSource _restoreCompletionSource = new();
    public Task RestoreTask => _restoreCompletionSource.Task;

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
                IsLoggedIn = true;
                await FetchAndSetUserInfoAsync();
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

        ClearState();
    }

    [RelayCommand]
    private async Task LogoutAllAsync()
    {
        try
        {
            await InfoPanelApiService.Instance.Client.Post_LogoutAsync(All.True);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Server logout-all call failed");
        }

        ClearState();
    }

    [RelayCommand]
    private async Task RevokeSessionAsync(string sessionId)
    {
        try
        {
            await InfoPanelApiService.Instance.Client.Delete_RevokeSessionAsync(sessionId);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to revoke session {SessionId}", sessionId);
        }
    }

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        if (!IsLoggedIn) return;

        IsRefreshingSessions = true;
        try
        {
            var response = await InfoPanelApiService.Instance.Client.Get_ListSessionsAsync();
            RunOnUiThread(() =>
            {
                Sessions.Clear();
                foreach (var session in response.Sessions)
                {
                    Sessions.Add(session);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to fetch sessions");
        }
        finally
        {
            IsRefreshingSessions = false;
        }
    }

    public async Task TryRestoreSessionAsync()
    {
        var token = AuthService.LoadAndRestoreToken();
        if (token == null)
        {
            _restoreCompletionSource.TrySetResult();
            return;
        }

        RunOnUiThread(() => IsRestoring = true);

        try
        {
            var response = await InfoPanelApiService.Instance.Client.Get_GetCurrentUserAsync();
            RunOnUiThread(() =>
            {
                Username = response.User.Username;
                AvatarUrl = response.User.Avatar;
                IsLoggedIn = true;
                IsRestoring = false;
            });
            _restoreCompletionSource.TrySetResult();
            Logger.Information("Session restored for user {Username}", Username);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to validate stored token, clearing");
            AuthService.ClearToken();
            RunOnUiThread(() =>
            {
                IsLoggedIn = false;
                IsRestoring = false;
            });
            _restoreCompletionSource.TrySetResult();
        }
    }

    private async Task FetchAndSetUserInfoAsync()
    {
        var response = await InfoPanelApiService.Instance.Client.Get_GetCurrentUserAsync();
        Username = response.User.Username;
        AvatarUrl = response.User.Avatar;
        await RefreshSessionsAsync();
    }

    private void ClearState()
    {
        AuthService.ClearToken();
        IsLoggedIn = false;
        Username = string.Empty;
        AvatarUrl = null;
        ErrorMessage = null;
        Sessions.Clear();
    }

    private static void RunOnUiThread(Action action)
    {
        if (Application.Current.Dispatcher.CheckAccess())
            action();
        else
            Application.Current.Dispatcher.Invoke(action);
    }
}
