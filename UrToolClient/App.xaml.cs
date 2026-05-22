using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using UrToolClient.Services;
using UrToolClient.Services.Log;
using UrToolClient.ViewModels;
using UrToolClient.Views;

namespace UrToolClient
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static IHost AppHost { get; private set; }

        public App()
        {
            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<UrRobotControl>();
                    services.AddSingleton<CalibrationViewModel>();
                    services.AddSingleton<CalibrationPage>();
                    services.AddSingleton<VariablesPage>();
                    services.AddSingleton<VariablesViewModel>();
                    services.AddSingleton<SimulationViewModel>(); // 保持后台状态
                    services.AddSingleton<SimulationPage>();      // 与 ViewModel 同生命周期，避免 SceneNode 重复挂载
                })
                .ConfigureLogging(logger =>
                {
                    logger.ClearProviders();
                    logger.AddWpfLogger();
                }).Build();
        }



        protected override async void OnStartup(StartupEventArgs e)
        {
            await AppHost.StartAsync();
            var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);

            var logger = AppHost.Services.GetRequiredService<ILogger<App>>();

            // await URHelper.OpenSetUpConfigAsync(@"C:\Users\27540\Desktop\default.variables");
            logger.LogWarning("Application started");


        }
    }

}
