using System.IO;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Tests;

public sealed class FuturesTradingTests
{
    private static readonly InstrumentKey Contract = new("binance", "futures", "", "BTC/USDT:USDT");

    /// <summary>Verifies: futures Window Renders Contract Units At Minimum Size Without Connecting An Account.</summary>
    [Fact]
    public void FuturesWindowRendersContractUnitsAtMinimumSizeWithoutConnectingAnAccount()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var source = new Moq.Mock<BotView.Services.OrderBooks.IOrderBookService>();
                var window = new BotView.Windows.OrderBookWindow("binance", Contract.SecurityId, source.Object,
                    environment: TradingEnvironment.Testnet, market: TradingMarket.UsdtFutures);
                var viewModel = (BotView.ViewModels.OrderBookViewModel)window.DataContext;
                var content = (System.Windows.FrameworkElement)window.Content;
                content.Measure(new System.Windows.Size(320, 440));
                content.Arrange(new System.Windows.Rect(0, 0, 320, 440));
                content.UpdateLayout();

                Assert.True(viewModel.IsFutures);
                Assert.False(viewModel.ShowPositionSide);
                Assert.Equal("", viewModel.PositionModeText);
                Assert.Equal(new[] { "Long", "Short" }, viewModel.PositionTargets);
                Assert.Equal("Long", viewModel.PositionTarget);
                viewModel.PositionTarget = "Auto";
                Assert.Equal("Long", viewModel.PositionTarget);
                viewModel.PositionTarget = "Short";
                Assert.Equal("Short", viewModel.PositionTarget);
                Assert.Contains("contracts", viewModel.QuantityHeader);
                Assert.Contains("USDT", viewModel.QuoteHeader);
                Assert.False(viewModel.CanTrade);
                Assert.True(((System.Windows.FrameworkElement)window.FindName("PriceInput")).ActualWidth > 80);
                var actions = (System.Windows.FrameworkElement)window.FindName("TradeActions");
                Assert.True(actions.TransformToAncestor(content).Transform(new System.Windows.Point(0, actions.ActualHeight)).Y <= 440);

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 440, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                if (Environment.GetEnvironmentVariable("BOTVIEW_FUTURES_PREVIEW") is { Length: > 0 } path)
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(path);
                    encoder.Save(output);
                }

                window.Close();
                window.CleanupTask.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Verifies: futures Clients Use Derivative Endpoints Before Any Request.</summary>
    [Fact]
    public void LegacyProfileDefaultsToSpotAndNewMarketSurvivesPersistence()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<TradingAccountProfile>(
            "{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Name\":\"Legacy\",\"Exchange\":\"binance\",\"Environment\":0}")!;
        Assert.Equal(TradingMarket.Spot, legacy.Market);
        var profile = legacy with { Market = TradingMarket.UsdtFutures };
        var path = Path.Combine(AppContext.BaseDirectory, "futures-" + Guid.NewGuid().ToString("N"), "profiles.json");
        var store = new TradingAccountStore(path);
        store.Save(new([profile], new Dictionary<string, Guid> { ["binance"] = profile.Id }));
        Assert.Equal(TradingMarket.UsdtFutures, store.Load().Profiles.Single().Market);
    }
}
