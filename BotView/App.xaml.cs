using System.Configuration;
using System.Data;
using System.Windows;
using System.Collections.Generic;
using BotView.Configuration;
using BotView.Services;
using BotView.Interfaces;

namespace BotView
{
    public partial class App : Application
    {
        // Сервисы приложения
        public static IDataProvider DataProvider { get; private set; } = null!;
        public static IExchangeService ExchangeService { get; private set; } = null!;
        public static IExchangeLogger ExchangeLogger { get; private set; } = null!;
        public static IDataProviderLogger DataProviderLogger { get; private set; } = null!;

        // Доступ к конфигурации бирж через статический класс
        public static List<string> AvailableExchanges => ExchangeConfig.AvailableExchanges;
        public static List<string> ActiveExchanges => ExchangeConfig.GetActiveExchanges();
        public static List<string> SupportedTimeframes => ExchangeConfig.SupportedTimeframes;
        public static List<string> DefaultSymbols => ExchangeConfig.DefaultSymbols;
        public static ApplicationDefaults Defaults => ExchangeConfig.Defaults;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Инициализация сервисов при запуске приложения
            InitializeServices();
        }

        /// <summary>
        /// Инициализирует сервисы приложения с настройками по умолчанию
        /// </summary>
        private void InitializeServices()
        {
            try
            {
                // Создание логгеров
                DataProviderLogger = new ConsoleDataProviderLogger();
                ExchangeLogger = new ConsoleExchangeLogger();

                // Создание DataProvider с логгером
                DataProvider = new DataProvider(DataProviderLogger);

                // Получение настроек из конфигурации
                var cacheSettings = ExchangeConfig.GetCacheSettings();
                var connectionSettings = ExchangeConfig.GetConnectionSettings();

                // Создание ExchangeService с настройками из конфигурации и логгером
                ExchangeService = new ExchangeService(
                    dataProvider: DataProvider,
                    logger: ExchangeLogger,
                    cacheExpirationMinutes: cacheSettings.ExpirationMinutes,
                    maxRetryAttempts: connectionSettings.MaxRetryAttempts,
                    baseRetryDelaySeconds: connectionSettings.RetryDelaySeconds
                );

                // Логирование успешной инициализации
                System.Diagnostics.Debug.WriteLine("Services initialized successfully");
                System.Diagnostics.Debug.WriteLine($"Default exchange: {Defaults.Exchange}");
                System.Diagnostics.Debug.WriteLine($"Default symbol: {Defaults.Symbol}");
                System.Diagnostics.Debug.WriteLine($"Default timeframe: {Defaults.Timeframe}");
                System.Diagnostics.Debug.WriteLine($"Cache expiration: {cacheSettings.ExpirationMinutes} minutes");
                System.Diagnostics.Debug.WriteLine($"Max retry attempts: {connectionSettings.MaxRetryAttempts}");
            }
            catch (Exception ex)
            {
                // Логирование ошибки инициализации
                System.Diagnostics.Debug.WriteLine($"Failed to initialize services: {ex.Message}");
                
                // В случае ошибки создаем базовые сервисы
                DataProviderLogger = new ConsoleDataProviderLogger();
                ExchangeLogger = new ConsoleExchangeLogger();
                DataProvider = new DataProvider(DataProviderLogger);
                ExchangeService = new ExchangeService(DataProvider, ExchangeLogger);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Логирование завершения приложения
            System.Diagnostics.Debug.WriteLine("Application shutting down");
            
            base.OnExit(e);
        }
    }
}
