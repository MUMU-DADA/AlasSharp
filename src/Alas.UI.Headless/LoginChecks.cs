using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.Auth;

namespace Alas.UI.Headless;

/// <summary>
/// 登录页（上游 App.tsx 的 login-card）的离屏检查：
/// ① 未接 Core → 显示真实原因、输入与按钮禁用（不伪造"已登录"，也不假装密码正确）；
/// ② 可输入状态 → 上游三处文案（访问密码 / 进入控制台 / 密码提示）与卡片元素齐全；
/// ③ 提交中 → 按钮文案变「正在验证…」；
/// ④ 提交失败 → 显示真实错误；空密码不允许提交。
/// </summary>
internal static class LoginChecks
{
    internal static void Run()
    {
        Disconnected();
        Pending();
        BusyAndFailure();
        PasswordReveal();
        DelayedRequestsAndBackendSwitch();
        PasswordErrorsStayPrivate();
    }

    /// <summary>
    /// 密码显示/隐藏开关（上游 common.showPassword / hidePassword）：点击切换明文与掩码，
    /// 并且**按钮文案与可访问名同步切换**（否则读屏用户不知道当前是哪种状态）。
    /// </summary>
    private static void PasswordReveal()
    {
        var view = new LoginView(new FakeAuth(new AuthStatus("pending")));
        var window = Show(view);
        try
        {
            var box = Find<TextBox>(view, "LoginPasswordBox");
            var reveal = Find<Button>(view, "LoginPasswordReveal");
            Check(reveal.Content?.ToString() == "显示密码", $"the toggle starts as 显示密码 (got {reveal.Content})");
            Check(box.PasswordChar == '•', "the password is masked initially");

            reveal.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(box.PasswordChar == '\0', "revealing switches the box to plain text");
            Check(reveal.Content?.ToString() == "隐藏密码", $"the toggle becomes 隐藏密码 (got {reveal.Content})");
            Check(Avalonia.Automation.AutomationProperties.GetName(reveal) == "隐藏密码",
                "the accessible name follows the toggle state");

            reveal.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(box.PasswordChar == '•', "toggling back restores masking");
            Check(reveal.Content?.ToString() == "显示密码", "the toggle label is restored");
        }
        finally { window.Close(); }
    }

    private static void Disconnected()
    {
        var view = new LoginView();
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(model.HasError && model.Error.Contains("不可用"),
                "disconnected login explains why it is unavailable");
            Check(!model.CanSignIn && !Find<Button>(view, "LoginSubmitButton").IsEnabled,
                "the submit button is disabled while unconnected");
            Check(!Find<TextBox>(view, "LoginPasswordBox").IsEnabled, "the password box is disabled while unconnected");
            Check(!model.Authenticated, "an unconnected login never claims to be authenticated");
        }
        finally { window.Close(); }
    }

    private static void Pending()
    {
        var view = new LoginView(new FakeAuth(new AuthStatus("pending")));
        var window = Show(view);
        try
        {
            var model = view.Model;
            // 上游文案三处：访问密码（标签）、进入控制台（按钮）、密码提示。
            Check(Find<TextBlock>(view, "LoginPasswordLabel").Text == "访问密码", "the password label matches upstream");
            Check(Find<Button>(view, "LoginSubmitButton").Content?.ToString() == "进入控制台",
                "the submit label matches upstream");
            Check(Find<TextBlock>(view, "LoginPasswordHint").Text == "自动生成的密码保存在服务端 password.txt 中。",
                "the password hint matches upstream");
            Check(Find<TextBlock>(view, "LoginWelcome").Text == "欢迎回到指挥室", "the welcome line matches upstream");
            Check(model.CanSignIn && !Find<Button>(view, "LoginSubmitButton").IsEnabled,
                "pending login allows input but requires a password before submission");
            Check(Find<TextBox>(view, "LoginPasswordBox").PasswordChar == '•', "the password box masks input");
            Check(!Find<TextBlock>(view, "LoginError").IsVisible, "no error is shown before a failure");
        }
        finally { window.Close(); }
    }

    private static void BusyAndFailure()
    {
        // 空密码：不允许提交（不发起请求、也不报错）。
        var backend = new FakeAuth(new AuthStatus("pending"));
        var view = new LoginView(backend);
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(!model.CanSubmit, "an empty password cannot be submitted");
            model.SignInCommand.Execute(null);
            Pump();
            Check(backend.SignInCalls == 0, "no sign-in request is sent without a password");

            // 密码错误：后端仍返回 pending → 页面必须显示真实结果，不得假装成功。
            Find<TextBox>(view, "LoginPasswordBox").Text = "wrong";
            Pump();
            Check(model.CanSubmit, "a non-empty password can be submitted");
            model.SignInCommand.Execute(null);
            Pump();
            Check(backend.SignInCalls == 1, "the password is submitted once");
            Check(!model.Authenticated, "a rejected password does not authenticate");
            Check(Find<TextBlock>(view, "LoginError").IsVisible, "a rejected password shows an error");

            // 密码正确：进入已认证状态。
            var ok = new LoginView(new FakeAuth(new AuthStatus("pending"), new AuthStatus("authenticated")));
            var okWindow = Show(ok);
            try
            {
                Find<TextBox>(ok, "LoginPasswordBox").Text = "secret";
                Pump();
                ok.Model.SignInCommand.Execute(null);
                Pump();
                Check(ok.Model.Authenticated, "a correct password authenticates");
                Check(!ok.Model.HasError, "no error is shown after a successful sign-in");
            }
            finally { okWindow.Close(); }
        }
        finally { window.Close(); }
    }

    private static void DelayedRequestsAndBackendSwitch()
    {
        var oldRead = new TaskCompletionSource<AuthStatus>();
        var initial = new DeferredAuth(oldRead.Task);
        var view = new LoginView(initial);
        var window = Show(view);
        try
        {
            var old = new DeferredAuth(Task.FromResult(new AuthStatus("pending")));
            view.Backend = old;
            oldRead.SetResult(new AuthStatus("authenticated"));
            Pump();
            Check(!view.Model.Authenticated && view.Model.CanSignIn, "old refresh cannot authenticate a new backend");
            var password = Find<TextBox>(view, "LoginPasswordBox");
            var submit = Find<Button>(view, "LoginSubmitButton");
            password.Text = "old-test-password";
            Click(window, Find<Button>(view, "LoginPasswordReveal"));
            Check(password.PasswordChar == '\0', "the pointer can reveal a password");
            Click(window, submit);
            Check(view.Model.Busy && !view.Model.CanSubmit && !submit.IsEnabled,
                "a delayed submission immediately disables the bound button");
            Check(submit.Content?.ToString() == "正在验证…", "a delayed submission displays its busy label");
            view.Model.SignInCommand.Execute(null);
            Check(old.SignInCalls == 1, "a repeated command cannot submit while busy");

            var current = new DeferredAuth(Task.FromResult(new AuthStatus("pending")));
            view.Backend = current;
            Pump();
            Check(password.Text == string.Empty && password.PasswordChar == '•',
                "backend replacement clears and masks the actual password control");
            Check(!submit.IsEnabled && !view.Model.SignInCommand.CanExecute(null),
                "backend replacement requires new input before another request");
            password.Text = "new-test-password";
            Click(window, submit);
            old.SignInResult.SetResult(new AuthStatus("authenticated"));
            Pump();
            Check(view.Model.Busy && !view.Model.Authenticated && !submit.IsEnabled,
                "old completion cannot authenticate or clear the new request's busy state");
            current.SignInResult.SetResult(new AuthStatus("pending", "认证拒绝"));
            Pump();
            Check(!view.Model.Busy && submit.IsEnabled && view.Model.Error == "认证拒绝",
                "the current failure restores submission and presents its error");
            Check(current.SignInCalls == 1, "new credentials go only to the current backend");
        }
        finally { window.Close(); }
    }

    private static void PasswordErrorsStayPrivate()
    {
        const string credential = "test-credential";
        var backend = new DeferredAuth(Task.FromResult(new AuthStatus("pending")));
        var view = new LoginView(backend);
        var window = Show(view);
        try
        {
            Find<TextBox>(view, "LoginPasswordBox").Text = credential;
            Click(window, Find<Button>(view, "LoginSubmitButton"));
            backend.SignInResult.SetException(new InvalidOperationException("Rejected " + credential));
            Pump();
            Check(view.Model.HasError && !view.Model.Error.Contains(credential, StringComparison.Ordinal),
                "an exception never displays the submitted password");
            view.Backend = new FakeAuth(new AuthStatus("pending"), new AuthStatus("pending", "Rejected " + credential));
            Find<TextBox>(view, "LoginPasswordBox").Text = credential;
            Click(window, Find<Button>(view, "LoginSubmitButton"));
            Check(view.Model.HasError && !view.Model.Error.Contains(credential, StringComparison.Ordinal),
                "a returned error also hides the submitted password");
        }
        finally { window.Close(); }
    }

    private static void Click(Window window, Button button)
    {
        Pump();
        var origin = button.TranslatePoint(new Point(0, 0), window) ?? throw new Exception("Unattached button");
        var point = origin + new Vector(button.Bounds.Width / 2, button.Bounds.Height / 2);
        window.MouseMove(point);
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Pump();
    }

    private sealed class DeferredAuth(Task<AuthStatus> read) : IAlasAuth
    {
        public TaskCompletionSource<AuthStatus> SignInResult { get; } = new();
        public int SignInCalls { get; private set; }
        public Task<AuthStatus> ReadAsync(CancellationToken cancellationToken = default) => read;
        public Task<AuthStatus> SignInAsync(string password, CancellationToken cancellationToken = default)
        {
            ++SignInCalls;
            return SignInResult.Task;
        }
    }

    private static Window Show(LoginView view)
    {
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Pump();
        return window;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new Exception("Missing control: " + name);

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }

    /// <summary>局部接口的假实现：只用于离屏检查，不访问服务、设备或真实凭据。</summary>
    private sealed class FakeAuth(AuthStatus read, AuthStatus? afterSignIn = null) : IAlasAuth
    {
        public int SignInCalls { get; private set; }

        public Task<AuthStatus> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(read);

        public Task<AuthStatus> SignInAsync(string password, CancellationToken cancellationToken = default)
        {
            SignInCalls++;
            return Task.FromResult(afterSignIn ?? read);
        }
    }
}
