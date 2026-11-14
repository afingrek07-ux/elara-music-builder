using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using ElaraMusicBuilder.Services;

namespace ElaraMusicBuilder
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Русский интерфейс; числа для FFmpeg всегда форматируются отдельно (с точкой).
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            Thread.CurrentThread.CurrentCulture = ru;
            Thread.CurrentThread.CurrentUICulture = ru;
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(ru.IetfLanguageTag)));

            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Error("Необработанная ошибка", ex.Exception);
                try { (MainWindow as Views.MainWindow)?.EmergencySave(); } catch { }
                MessageBox.Show("Произошла непредвиденная ошибка. Проект сохранён автоматически, работу можно продолжить." +
                                Environment.NewLine + Environment.NewLine + ex.Exception.Message,
                    "Elara Music Builder", MessageBoxButton.OK, MessageBoxImage.Warning);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Error("Критическая ошибка", ex.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Error("Ошибка фоновой задачи", ex.Exception); ex.SetObserved(); };

            Log.Info("Запуск Elara Music Builder 4.0");
            AppPaths.CleanupWork();
            base.OnStartup(e);
        }
    }
}
