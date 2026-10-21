using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using McInstaller.App.Services;
using McInstaller.App.ViewModels;
using McInstaller.App.Views;

namespace McInstaller.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configService = new ConfigService();
            var config = configService.Load();

            if (!config.AgreementAccepted)
            {
                // 首次启动：先要求同意用户协议。
                // 不同意或直接关窗一律退出程序。
                var agreement = new AgreementWindow();

                agreement.Closed += (_, _) =>
                {
                    if (!agreement.Accepted)
                    {
                        desktop.Shutdown();
                        return;
                    }

                    config.AgreementAccepted = true;
                    configService.Save(config);

                    LaunchMainWindow(desktop);
                };

                desktop.MainWindow = agreement;
                agreement.Show();
            }
            else
            {
                LaunchMainWindow(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void LaunchMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var main = new MainWindow
        {
            DataContext = new MainViewModel(),
        };

        desktop.MainWindow = main;
        main.Show();
    }
}
