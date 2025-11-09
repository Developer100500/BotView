# Проект интеграции с криптовалютными биржами

## Обзор

Данный проект расширяет существующее WPF приложение для технического анализа, добавляя возможность получения реальных данных OHLCV с криптовалютных бирж через библиотеку CCXT. Архитектура построена на принципах разделения ответственности, где существующая система отрисовки графиков остается неизменной, а новые компоненты обеспечивают получение и обработку данных с бирж.

## Архитектура

### Общая архитектура системы

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   MainWindow    │───▶│  ExchangeService │───▶│   CCXT Library  │
│   (UI Layer)    │    │  (Business Logic)│    │  (External API) │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                        │
         ▼                        ▼
┌─────────────────┐    ┌──────────────────┐
│   ChartView     │◀───│   DataProvider   │
│ (Visualization) │    │ (Data Processing)│
└─────────────────┘    └──────────────────┘
```

### Слои архитектуры

1. **UI Layer (MainWindow)**: Управляет пользовательским интерфейсом и взаимодействием
2. **Business Logic (ExchangeService)**: Координирует работу с биржами и обработку данных
3. **Data Processing (DataProvider)**: Преобразует данные из CCXT в формат приложения
4. **Visualization (ChartView)**: Существующая система отрисовки графиков (без изменений)
5. **External API (CCXT)**: Библиотека для подключения к биржам

## Компоненты и интерфейсы

### 1. IExchangeService

Основной интерфейс для работы с биржами:

```csharp
public interface IExchangeService
{
    Task<CandlestickData> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit = 500);
    Task<List<string>> GetAvailableSymbolsAsync(string exchange);
    Task<bool> TestConnectionAsync(string exchange);
    List<string> GetSupportedExchanges();
    List<string> GetSupportedTimeframes();
}
```

### 2. ExchangeService

Реализация сервиса для работы с биржами и CCXT:

```csharp
public class ExchangeService : IExchangeService
{
    private readonly Dictionary<string, ccxt.Exchange> _exchanges;
    private readonly IDataProvider _dataProvider;
    private readonly Dictionary<string, CandlestickData> _cache;
    
    public async Task<CandlestickData> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit = 500)
    {
        var cacheKey = $"{exchange}_{symbol}_{timeframe}_{limit}";
        
        if (_cache.ContainsKey(cacheKey))
            return _cache[cacheKey];
            
        var exchangeInstance = GetExchangeInstance(exchange);
        var ccxtData = await exchangeInstance.FetchOHLCV(symbol, timeframe, null, limit);
        
        var result = _dataProvider.ConvertFromCCXT(ccxtData, timeframe);
        _cache[cacheKey] = result;
        
        return result;
    }
    
    private ccxt.Exchange GetExchangeInstance(string exchangeName)
    {
        if (!_exchanges.ContainsKey(exchangeName))
        {
            _exchanges[exchangeName] = ExchangeFactory.CreateExchange(exchangeName);
        }
        return _exchanges[exchangeName];
    }
}
```

### 3. IDataProvider

Интерфейс для преобразования данных из формата CCXT:

```csharp
public interface IDataProvider
{
    CandlestickData ConvertFromCCXT(object[][] ccxtData, string timeframe);
    OHLCV ConvertCCXTCandle(object[] candleData);
    bool ValidateCCXTData(object[][] data);
}
```

### 4. DataProvider

Реализация провайдера данных для работы с форматом CCXT:

```csharp
public class DataProvider : IDataProvider
{
    public CandlestickData ConvertFromCCXT(object[][] ccxtData, string timeframe)
    {
        if (!ValidateCCXTData(ccxtData))
            throw new ArgumentException("Invalid CCXT data format");
            
        var candles = ccxtData.Select(ConvertCCXTCandle).ToArray();
        
        if (candles.Length == 0)
            return new CandlestickData(timeframe, DateTime.UtcNow, DateTime.UtcNow, new OHLCV[0]);
            
        var beginTime = candles[0].GetDateTime();
        var endTime = candles[candles.Length - 1].GetDateTime();
        
        return new CandlestickData(timeframe, beginTime, endTime, candles);
    }
    
    public OHLCV ConvertCCXTCandle(object[] candleData)
    {
        // Формат CCXT: [timestamp, open, high, low, close, volume]
        if (candleData.Length < 6)
            throw new ArgumentException("Invalid candle data format");
            
        return new OHLCV(
            timestamp: Convert.ToInt64(candleData[0]),
            open: Convert.ToDouble(candleData[1]),
            high: Convert.ToDouble(candleData[2]),
            low: Convert.ToDouble(candleData[3]),
            close: Convert.ToDouble(candleData[4]),
            volume: Convert.ToDouble(candleData[5])
        );
    }
    
    public bool ValidateCCXTData(object[][] data)
    {
        return data != null && data.All(candle => 
            candle != null && candle.Length >= 6);
    }
}
```

### 5. ExchangeFactory

Фабрика для создания экземпляров бирж:

```csharp
public static class ExchangeFactory
{
    public static ccxt.Exchange CreateExchange(string exchangeName)
    {
        return exchangeName.ToLower() switch
        {
            "binance" => new ccxt.binance(),
            "bybit" => new ccxt.bybit(),
            "okx" => new ccxt.okx(),
            "kraken" => new ccxt.kraken(),
            _ => throw new NotSupportedException($"Exchange {exchangeName} is not supported")
        };
    }
}
```

## Модели данных

### Адаптация существующей структуры OHLCV для CCXT

Поскольку CCXT возвращает данные в формате `[timestamp, open, high, low, close, volume]`, необходимо добавить timestamp в существующую структуру OHLCV:

```csharp
// Обновленная структура OHLCV с timestamp
public struct OHLCV
{
    public long timestamp;    // UTC timestamp в миллисекундах (добавлено для CCXT)
    public double open;
    public double high;
    public double low;
    public double close;
    public double volume;

    // Существующий конструктор (для обратной совместимости)
    public OHLCV(double open, double high, double low, double close, double volume = -1)
    {
        this.timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Текущее время по умолчанию
        this.open = open;
        this.high = high;
        this.low = low;
        this.close = close;
        this.volume = volume;
    }
    
    // Новый конструктор с timestamp (для CCXT)
    public OHLCV(long timestamp, double open, double high, double low, double close, double volume = -1)
    {
        this.timestamp = timestamp;
        this.open = open;
        this.high = high;
        this.low = low;
        this.close = close;
        this.volume = volume;
    }
    
    // Конвертация timestamp в DateTime
    public DateTime GetDateTime() => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
}
```

### ExchangeConfiguration

```csharp
public class ExchangeConfiguration
{
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public bool IsEnabled { get; set; }
    public List<string> SupportedTimeframes { get; set; }
    public Dictionary<string, object> Settings { get; set; }
}
```

### MarketSymbol

```csharp
public class MarketSymbol
{
    public string Symbol { get; set; }
    public string BaseAsset { get; set; }
    public string QuoteAsset { get; set; }
    public bool IsActive { get; set; }
}
```

### DataRequest

```csharp
public class DataRequest
{
    public string Exchange { get; set; }
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public int Limit { get; set; }
    public DateTime? Since { get; set; }
}
```

## Обработка ошибок

### Стратегия обработки ошибок

1. **Сетевые ошибки**: Повторные попытки с экспоненциальной задержкой
2. **Ошибки API**: Логирование и уведомление пользователя
3. **Ошибки данных**: Валидация и использование кэшированных данных
4. **Ошибки конфигурации**: Возврат к настройкам по умолчанию

### ExchangeException

```csharp
public class ExchangeException : Exception
{
    public string ExchangeName { get; }
    public string ErrorCode { get; }
    public ExchangeErrorType ErrorType { get; }
    
    public ExchangeException(string exchangeName, string message, ExchangeErrorType errorType) 
        : base(message)
    {
        ExchangeName = exchangeName;
        ErrorType = errorType;
    }
}

public enum ExchangeErrorType
{
    NetworkError,
    ApiError,
    DataError,
    ConfigurationError,
    RateLimitError
}
```

## Стратегия тестирования

### Модульные тесты

1. **ExchangeService**: Тестирование логики получения данных
2. **DataProvider**: Тестирование конвертации данных
3. **ExchangeFactory**: Тестирование создания экземпляров бирж

### Интеграционные тесты

1. **CCXT Integration**: Тестирование подключения к реальным биржам
2. **Data Flow**: Тестирование полного потока данных от API до UI
3. **Error Handling**: Тестирование обработки различных типов ошибок

### Тестовые данные

```csharp
public static class TestDataProvider
{
    public static object[][] GetSampleCCXTData()
    {
        // Возвращает тестовые данные в формате CCXT
        // [timestamp, open, high, low, close, volume]
        return new object[][]
        {
            new object[] { 1504541580000L, 4235.4, 4240.6, 4230.0, 4230.7, 37.72941911 },
            new object[] { 1504541640000L, 4230.7, 4238.1, 4225.3, 4235.2, 42.15832156 },
            new object[] { 1504541700000L, 4235.2, 4245.8, 4232.1, 4240.5, 35.89471234 }
        };
    }
    
    public static CandlestickData GetSampleCandlestickData()
    {
        var ccxtData = GetSampleCCXTData();
        var dataProvider = new DataProvider();
        return dataProvider.ConvertFromCCXT(ccxtData, "1m");
    }
}
```

## Производительность и кэширование

### Стратегия кэширования

1. **Memory Cache**: Кэширование последних запрошенных данных
2. **Time-based Expiration**: Автоматическое истечение кэша через определенное время
3. **Smart Invalidation**: Инвалидация кэша при смене параметров

### Оптимизация запросов

1. **Batch Requests**: Группировка запросов когда это возможно
2. **Rate Limiting**: Соблюдение лимитов API бирж
3. **Connection Pooling**: Переиспользование соединений

## Конфигурация

### Настройки по умолчанию

```json
{
  "DefaultExchange": "binance",
  "DefaultSymbol": "BTC/USDT",
  "DefaultTimeframe": "1d",
  "CacheExpirationMinutes": 5,
  "MaxRetryAttempts": 3,
  "RequestTimeoutSeconds": 30,
  "SupportedExchanges": [
    {
      "Name": "binance",
      "DisplayName": "Binance",
      "IsEnabled": true,
      "SupportedTimeframes": ["1m", "5m", "15m", "30m", "1h", "4h", "1d", "1w"]
    },
    {
      "Name": "bybit",
      "DisplayName": "Bybit", 
      "IsEnabled": true,
      "SupportedTimeframes": ["1m", "5m", "15m", "30m", "1h", "4h", "1d", "1w"]
    }
  ]
}
```

## Интеграция с существующим кодом

### Изменения в ChartView.cs

1. **Обновление структуры OHLCV**: Добавление поля timestamp для поддержки данных CCXT
2. **Обновление метода GetCandleTime()**: Использование timestamp из данных вместо вычисления на основе индекса
3. **Обратная совместимость**: Сохранение существующего конструктора OHLCV без timestamp

### Изменения в MainWindow

1. Добавление сервиса ExchangeService
2. Замена метода LoadDemoData на LoadRealData
3. Добавление обработчиков для выбора биржи и символа

### Изменения в App.xaml.cs

1. Регистрация сервисов в контейнере зависимостей
2. Инициализация конфигурации бирж
3. Настройка логирования

### Сохранение совместимости

- ChartView сохраняет все существующие методы навигации и масштабирования
- Формат данных CandlestickData остается прежним
- Существующий код с демонстрационными данными продолжит работать через конструктор OHLCV без timestamp

## Безопасность

### API Keys Management

1. Хранение API ключей в зашифрованном виде (если потребуется в будущем)
2. Использование переменных окружения для конфиденциальных данных
3. Валидация входящих данных от API

### Rate Limiting

1. Соблюдение лимитов запросов каждой биржи
2. Реализация очереди запросов
3. Мониторинг использования API

## Мониторинг и логирование

### Логирование

```csharp
public interface IExchangeLogger
{
    void LogApiRequest(string exchange, string endpoint, TimeSpan duration);
    void LogError(string exchange, Exception exception);
    void LogDataReceived(string exchange, string symbol, int candleCount);
}
```

### Метрики

1. Время ответа API
2. Количество успешных/неуспешных запросов
3. Размер полученных данных
4. Использование кэша