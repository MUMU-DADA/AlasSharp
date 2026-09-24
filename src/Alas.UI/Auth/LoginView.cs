using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alas.UI.Auth;

/// <summary>
/// 登录/进入控制台需要的能力（上游 App.tsx 的 login-card：访问密码 → 进入控制台）。
/// 与其他页面一致：只依赖页面局部接口，不触碰外壳与后端实现。
/// </summary>
public interface IAlasAuth
{
    /// <summary>读取当前认证状态；失败时返回带 Error 的结果而不抛异常。</summary>
    Task<AuthStatus> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>提交访问密码。密码不得写入日志或错误信息（页面侧也不回显）。</summary>
    Task<AuthStatus> SignInAsync(string password, CancellationToken cancellationToken = default);
}

/// <summary>
/// 认证状态。<c>State</c> 取上游语义：<c>pending</c>（未认证、可输入）/ <c>authenticated</c> /
/// <c>unavailable</c>（没有可用的认证能力，页面如实显示原因）。
/// </summary>
public sealed record AuthStatus(string State, string? Error = null)
{
    public bool IsAuthenticated => State == "authenticated";

    public bool CanSignIn => State == "pending";

    public bool IsUnavailable => State == "unavailable";
}

/// <summary>未接能力时的默认实现：如实报告不可用，不伪造"已登录"也不假装密码正确。</summary>
public sealed class DisconnectedAuth : IAlasAuth
{
    public static DisconnectedAuth Instance { get; } = new();

    public const string Notice = "未连接 Alas.Core：访问校验由 Core 提供，当前不可用。";

    private static AuthStatus Off => new("unavailable", Notice);

    public Task<AuthStatus> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Off);

    public Task<AuthStatus> SignInAsync(string password, CancellationToken cancellationToken = default) =>
        Task.FromResult(Off);
}

/// <summary>
/// 登录页（上游 <c>form.login-card</c>）：品牌标识、标语、访问密码、进入控制台/正在验证、
/// 密码提示与错误提示。未接能力时按钮禁用并显示真实原因，**不伪造登录成功**。
/// </summary>
public sealed class LoginView : UserControl
{
    private readonly LoginViewModel _model;

    public LoginView()
        : this(DisconnectedAuth.Instance)
    {
    }

    public LoginView(IAlasAuth backend)
    {
        _model = new LoginViewModel(backend);
        DataContext = _model;
        Content = Build();
        _ = _model.RefreshAsync();
    }

    public LoginViewModel Model => _model;

    /// <summary>
    /// 外壳注入点（按 master 的承载约定）：设置后由 VM 清空密码/错误/认证标记并按新后端刷新。
    /// 未注入时保持"未连接"的真实状态，不伪造已登录。
    /// </summary>
    public IAlasAuth Backend
    {
        get => _model.Backend;
        set => _model.Backend = value;
    }

    private Control Build()
    {
        var mark = new Border
        {
            Name = "LoginBrandMark", Width = 40, Height = 40, CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = "AzurPilot", FontSize = 10, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        mark[!Border.BackgroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasAccentBrush");

        var welcome = new TextBlock { Name = "LoginWelcome", Text = "欢迎回到指挥室", FontSize = 20, FontWeight = FontWeight.SemiBold };
        var slogan = new TextBlock { Name = "LoginSlogan", Text = "让每一次出航，都井然有序。", FontSize = 12, Opacity = 0.7 };
        var label = new TextBlock { Name = "LoginPasswordLabel", Text = "访问密码", FontSize = 12 };
        var password = new TextBox
        {
            Name = "LoginPasswordBox", PasswordChar = '•', Width = 280,
            IsEnabled = _model.CanSignIn, PlaceholderText = "访问密码",
        };
        // 用属性变更管线回写（离屏下程序化赋值也能生效，沿用设置页验证过的做法）。
        password.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty) _model.Password = password.Text ?? string.Empty;
        };
        // 上游 common.showPassword / common.hidePassword：登录页的密码可临时显示，
        // 便于用户核对粘贴的密码；按钮文案与可访问名同步切换。
        var reveal = new Button
        {
            Name = "LoginPasswordReveal", Content = "显示密码", FontSize = 11,
            Padding = new Thickness(8, 3), HorizontalAlignment = HorizontalAlignment.Left,
        };
        reveal.Click += (_, _) =>
        {
            var hidden = password.PasswordChar != '\0';
            password.PasswordChar = hidden ? '\0' : '•';
            reveal.Content = hidden ? "隐藏密码" : "显示密码";
            Avalonia.Automation.AutomationProperties.SetName(reveal, reveal.Content?.ToString() ?? string.Empty);
        };
        Avalonia.Automation.AutomationProperties.SetName(reveal, "显示密码");
        var submit = new Button
        {
            Name = "LoginSubmitButton", Content = _model.SubmitLabel, Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = _model.CanSubmit,
            Command = _model.SignInCommand,
        };
        submit[!Button.BackgroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasAccentBrush");
        var hint = new TextBlock
        {
            Name = "LoginPasswordHint", Text = "自动生成的密码保存在服务端 password.txt 中。",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        };
        var error = new TextBlock
        {
            Name = "LoginError", Text = _model.Error, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            IsVisible = _model.HasError,
        };
        error[!TextBlock.ForegroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasDangerBrush");

        _model.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(LoginViewModel.Error):
                    error.Text = _model.Error;
                    error.IsVisible = _model.HasError;
                    break;
                case nameof(LoginViewModel.CanSignIn):
                    password.IsEnabled = _model.CanSignIn;
                    break;
                case nameof(LoginViewModel.CanSubmit):
                    submit.IsEnabled = _model.CanSubmit;
                    break;
                case nameof(LoginViewModel.Password):
                    password.Text = _model.Password;
                    if (string.IsNullOrEmpty(_model.Password))
                    {
                        password.PasswordChar = '•';
                        reveal.Content = "显示密码";
                        Avalonia.Automation.AutomationProperties.SetName(reveal, "显示密码");
                    }
                    break;
                case nameof(LoginViewModel.SubmitLabel):
                    submit.Content = _model.SubmitLabel;
                    break;
            }
        };

        var card = new StackPanel
        {
            Name = "LoginCard", Spacing = 10, Width = 320,
            Children = { mark, welcome, slogan, label, password, reveal, submit, hint, error },
        };
        var centered = new Border
        {
            Name = "LoginCardFrame", Classes = { "panel" }, Padding = new Thickness(28, 24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = card,
        };
        return new StackPanel { Name = "LoginPage", Children = { centered } };
    }
}

/// <summary>登录页状态：密码、能否提交、提交中、错误。密码只在内存中，不写日志、不回显。</summary>
public sealed class LoginViewModel : INotifyPropertyChanged
{
    private IAlasAuth _backend;
    private long _requestVersion;

    /// <summary>
    /// 可替换的认证后端：外壳按 master 的承载约定注入（XAML 传不了构造参数，所以不能只读）。
    /// 换后端时**清空已输入的密码**——用户为旧后端敲的密码不应被带到新后端去提交（安全考虑，
    /// 不是顺手清理），同时清掉错误与认证标记，再按新后端刷新状态。
    /// </summary>
    public IAlasAuth Backend
    {
        get => _backend;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_backend, value)) return;
            ++_requestVersion;
            _backend = value;
            Password = string.Empty;
            Error = string.Empty;
            Authenticated = false;
            CanSignInInternal = false;
            Busy = false;
            _ = RefreshAsync();
        }
    }
    private string _password = string.Empty;
    private string _error = string.Empty;
    private bool _canSignIn;
    private bool _busy;
    private bool _authenticated;

    public LoginViewModel(IAlasAuth backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        SignInCommand = new LoginCommand(_ => _ = SignInAsync(), () => CanSubmit);
    }

    public ICommand SignInCommand { get; }

    public string Password
    {
        get => _password;
        set { if (SetField(ref _password, value)) Notify(nameof(CanSubmit)); }
    }

    public string Error { get => _error; private set { if (SetField(ref _error, value)) Notify(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSignIn => _canSignIn && !_busy;

    /// <summary>非空密码才允许提交（前端不预判密码正确性，那是后端的事）。</summary>
    public bool CanSubmit => CanSignIn && !string.IsNullOrWhiteSpace(Password);

    public bool Busy { get => _busy; private set { if (SetField(ref _busy, value)) { Notify(nameof(CanSignIn)); Notify(nameof(CanSubmit)); Notify(nameof(SubmitLabel)); } } }

    public bool Authenticated { get => _authenticated; private set => SetField(ref _authenticated, value); }

    /// <summary>上游按钮文案：提交中显示「正在验证…」，否则「进入控制台」。</summary>
    public string SubmitLabel => Busy ? "正在验证…" : "进入控制台";

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Busy) return;
        var backend = _backend;
        var request = ++_requestVersion;
        Busy = true;
        try
        {
            var status = await backend.ReadAsync(cancellationToken);
            if (request == _requestVersion) Apply(status);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (request == _requestVersion)
                Apply(new AuthStatus("unavailable", "访问校验状态读取失败，请重试。"));
        }
        finally { if (request == _requestVersion) Busy = false; }
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSubmit) return;
        var backend = _backend;
        var password = Password;
        var request = ++_requestVersion;
        Busy = true;
        Error = string.Empty;
        try
        {
            var status = await backend.SignInAsync(password, cancellationToken);
            if (request != _requestVersion) return;
            // 后端错误也不可把本次提交的凭据带回界面。
            status = status with { Error = status.Error?.Replace(password, "[已隐藏]", StringComparison.Ordinal) };
            Apply(status);
            if (status.IsAuthenticated) Password = string.Empty;
            if (status.State == "pending" && string.IsNullOrEmpty(status.Error))
            {
                Error = "密码不正确。";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (request == _requestVersion)
                Apply(new AuthStatus("pending", "访问校验失败，请重试。"));
        }
        finally
        {
            if (request == _requestVersion) Busy = false;
        }
    }

    private void Apply(AuthStatus status)
    {
        Authenticated = status.IsAuthenticated;
        CanSignInInternal = status.CanSignIn;
        Error = status.Error ?? string.Empty;
    }

    private bool CanSignInInternal
    {
        set
        {
            if (_canSignIn == value) return;
            _canSignIn = value;
            Notify(nameof(CanSignIn));
            Notify(nameof(CanSubmit));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(CanSubmit)) ((LoginCommand)SignInCommand).Invalidate();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName!);
        return true;
    }
}

internal sealed class LoginCommand(Action<object?> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter) { if (canExecute()) execute(parameter); }

    public void Invalidate() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
