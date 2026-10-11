using System;
using System.Threading;
using System.Threading.Tasks;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Auth;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.App.ViewModels;

/// <summary>
/// Login gate view model: sign in / register / license modes against KeyAuth.
/// Never persists passwords - only the username and an "authenticated at least once" flag.
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private enum AuthMode
    {
        Login,
        Register,
        License,
    }

    private readonly AppServices _services;

    private AuthMode _mode = AuthMode.Login;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _licenseKey = string.Empty;
    private string _errorText = string.Empty;
    private bool _isBusy;
    private bool _rememberUsername = true;

    /// <summary>Raised after KeyAuth accepted the credentials.</summary>
    public event EventHandler? Authenticated;

    public LoginViewModel(AppServices services)
    {
        _services = services;

        var settings = services.SettingsStore.Load();
        _username = settings.AuthLastUsername ?? string.Empty;
        _rememberUsername = true;

        // Register-first until the user has signed in at least once.
        _mode = settings.AuthCompleted ? AuthMode.Login : AuthMode.Register;

        SubmitCommand = new AsyncRelayCommand(SubmitAsync, onException: LogError);
    }

    public AsyncRelayCommand SubmitCommand { get; }

    public bool IsLoginMode
    {
        get => _mode == AuthMode.Login;
        set => SetMode(AuthMode.Login, value);
    }

    public bool IsRegisterMode
    {
        get => _mode == AuthMode.Register;
        set => SetMode(AuthMode.Register, value);
    }

    public bool IsLicenseMode
    {
        get => _mode == AuthMode.License;
        set => SetMode(AuthMode.License, value);
    }

    public bool ShowUsername => _mode is AuthMode.Login or AuthMode.Register;

    public bool ShowPassword => _mode != AuthMode.License;

    public bool ShowConfirmPassword => _mode == AuthMode.Register;

    public bool ShowLicenseKey => _mode != AuthMode.Login;

    public bool ShowRememberUsername => _mode == AuthMode.Login;

    public string HeadingText => _mode switch
    {
        AuthMode.Register => "Create your account",
        AuthMode.License => "Activate with a license key",
        _ => "Sign in to ProPS1 Studio",
    };

    public string SubtitleText => _mode switch
    {
        AuthMode.Register => "Register a KeyAuth account to start using the studio.",
        AuthMode.License => "Enter your license key to sign in without a password.",
        _ => "Sign in with your KeyAuth account to continue.",
    };

    public string SubmitText => _mode switch
    {
        AuthMode.Register => "Create account",
        AuthMode.License => "Activate",
        _ => "Sign in",
    };

    public string Username
    {
        get => _username;
        set
        {
            if (SetProperty(ref _username, value))
            {
                ClearError();
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                ClearError();
            }
        }
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            if (SetProperty(ref _confirmPassword, value))
            {
                ClearError();
            }
        }
    }

    public string LicenseKey
    {
        get => _licenseKey;
        set
        {
            if (SetProperty(ref _licenseKey, value))
            {
                ClearError();
            }
        }
    }

    public bool RememberUsername
    {
        get => _rememberUsername;
        set => SetProperty(ref _rememberUsername, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private void SetMode(AuthMode mode, bool value)
    {
        if (!value || _mode == mode)
        {
            return;
        }

        _mode = mode;
        OnPropertyChanged(nameof(IsLoginMode));
        OnPropertyChanged(nameof(IsRegisterMode));
        OnPropertyChanged(nameof(IsLicenseMode));
        OnPropertyChanged(nameof(ShowUsername));
        OnPropertyChanged(nameof(ShowPassword));
        OnPropertyChanged(nameof(ShowConfirmPassword));
        OnPropertyChanged(nameof(ShowLicenseKey));
        OnPropertyChanged(nameof(ShowRememberUsername));
        OnPropertyChanged(nameof(HeadingText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(SubmitText));
        ClearError();
    }

    private void ClearError()
    {
        if (HasError)
        {
            ErrorText = string.Empty;
        }
    }

    private bool Fail(string message)
    {
        ErrorText = message;
        return false;
    }

    private bool Validate()
    {
        switch (_mode)
        {
            case AuthMode.Login:
                if (string.IsNullOrWhiteSpace(Username))
                {
                    return Fail("Enter your username.");
                }

                if (string.IsNullOrEmpty(Password))
                {
                    return Fail("Enter your password.");
                }

                break;

            case AuthMode.Register:
                if (string.IsNullOrWhiteSpace(Username))
                {
                    return Fail("Choose a username.");
                }

                if (string.IsNullOrEmpty(Password))
                {
                    return Fail("Choose a password.");
                }

                if (Password != ConfirmPassword)
                {
                    return Fail("Passwords do not match.");
                }

                if (string.IsNullOrWhiteSpace(LicenseKey))
                {
                    return Fail("Enter your license key.");
                }

                break;

            case AuthMode.License:
                if (string.IsNullOrWhiteSpace(LicenseKey))
                {
                    return Fail("Enter your license key.");
                }

                break;
        }

        return true;
    }

    private async Task SubmitAsync(CancellationToken ct)
    {
        ErrorText = string.Empty;

        // Local development credentials (username "1", password "1"): skip KeyAuth entirely.
        // Applies to the Login and Register screens (a first run may open in Register mode);
        // the License screen still requires a real key. In Register mode an explicit
        // mismatching confirmation still falls through to validation.
        if (_mode != AuthMode.License
            && Username.Trim() == "1"
            && Password == "1"
            && (string.IsNullOrEmpty(ConfirmPassword) || ConfirmPassword == Password))
        {
            CompleteAuthentication("1");
            return;
        }

        if (!Validate())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var username = Username.Trim();
            var key = LicenseKey.Trim();

            var result = _mode switch
            {
                AuthMode.Login => await _services.KeyAuth.LoginAsync(username, Password, ct),
                AuthMode.Register => await _services.KeyAuth.RegisterAsync(username, Password, key, string.Empty, ct),
                _ => await _services.KeyAuth.LicenseAsync(key, ct),
            };

            if (!result.Success)
            {
                ErrorText = string.IsNullOrEmpty(result.DownloadUrl)
                    ? result.Message
                    : $"{result.Message} Download: {result.DownloadUrl}";
                _services.Logger.Write(LogLevel.Warn, nameof(LoginViewModel),
                    $"Auth {_mode} failed: {result.Message}");
                return;
            }

            CompleteAuthentication(result.Username ?? username);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CompleteAuthentication(string username)
    {
        try
        {
            var settings = _services.SettingsStore.Load();
            settings.AuthCompleted = true;
            settings.AuthLastUsername = RememberUsername ? username : string.Empty;
            _services.SettingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            _services.Logger.Write(LogLevel.Warn, nameof(LoginViewModel),
                $"Could not persist auth settings: {ex.Message}");
        }

        _services.Logger.Write(LogLevel.Info, nameof(LoginViewModel), $"Authenticated as '{username}'.");
        Authenticated?.Invoke(this, EventArgs.Empty);
    }

    private void LogError(Exception ex)
    {
        _services.Logger.Write(LogLevel.Error, nameof(LoginViewModel), $"Authentication error: {ex}");
        ErrorText = $"Unexpected error: {ex.Message}";
        IsBusy = false;
    }
}
