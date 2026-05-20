using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Configuration;
using System.Data;
using System.Windows;
using UrToolClient.Helper;
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

            await URHelper.OpenSetUpConfigAsync(@"C:\Users\27540\Desktop\default.variables");
            logger.LogWarning("Application started");


        }
    }

}
