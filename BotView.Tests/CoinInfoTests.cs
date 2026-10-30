using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;
using BotView.Views;
using BotView.Windows;
using Moq;

namespace BotView.Tests;

public sealed class CoinInfoTests
{
    private const string SearchJson = """
        {"coins":[{"id":"ethena","name":"Ethena","symbol":"ENA","market_cap_rank":39},
                  {"id":"other-ena","name":"Other ENA","symbol":"ena","market_cap_rank":null},
                  {"id":"ethena","name":"Ethena","symbol":"ENA","market_cap_rank":39},
                  {"id":"ethenalabs-usde","name":"USDe","symbol":"USDE","market_cap_rank":20}]}
        """;

    [Fact]
    public async Task SearchMatchesWholeSymbolAndCachesResultsIncludingUnrankedCoins()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(SearchJson) });
        using var client = new HttpClient(handler);
        var provider = new CoinGeckoInfoProvider(client, "test-key");

        var matches = await provider.SearchAsync("ena", TestContext.Current.CancellationToken);
        var again = await provider.SearchAsync("ENA", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "ethena", "other-ena" }, matches.Select(c => c.Id));
        Assert.Same(matches, again);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://api.coingecko.com/api/v3/search?query=ENA", handler.LastUri);
        Assert.Equal("test-key", handler.ApiKey);
    }

    [Fact]
    // Verifies English description selection, HTML cleanup, and caching; takes no parameters.
    public async Task DetailsPreferEnglishStripHtmlAndCacheById()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "ethena", name = "Ethena", symbol = "ena",
            description = new { ru = "Other language", en = "<p>Protocol &amp; token.</p><p>Second line<br>Another line.</p><script>bad()</script>" },
            links = new { homepage = new[] { "", "javascript:bad()", "https://ethena.fi/" } },
            categories = new[] { "DeFi", "Ethereum Ecosystem" }
        });
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) });
        using var client = new HttpClient(handler);
        var provider = new CoinGeckoInfoProvider(client);

        var info = await provider.GetInfoAsync("ethena", TestContext.Current.CancellationToken);
        var again = await provider.GetInfoAsync("ethena", TestContext.Current.CancellationToken);

        Assert.Equal("en", info.Language);
        Assert.Equal("Protocol & token.\n\nSecond line\n\nAnother line.", info.Description);
        Assert.Equal("https://ethena.fi/", info.WebsiteUrl);
        Assert.Equal(2, info.Categories.Count);
        Assert.Equal("https://www.coingecko.com/en/coins/ethena", info.SourceUrl);
        Assert.Same(info, again);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("tickers=false&market_data=false", handler.LastUri);
    }

    [Fact]
    // Verifies English metadata when optional fields are missing; takes no parameters.
    public async Task DetailsUseEnglishAndHandleMissingOptionalMetadata()
    {
        const string json = """{"id":"ethena","name":"Ethena","symbol":"ena","description":{"ru":"","en":"English &amp; text."},"links":null,"categories":null}""";
        using var client = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var info = await new CoinGeckoInfoProvider(client).GetInfoAsync("ethena", TestContext.Current.CancellationToken);

        Assert.Equal("en", info.Language);
        Assert.Equal("English & text.", info.Description);
        Assert.Null(info.WebsiteUrl);
        Assert.Empty(info.Categories);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.Unauthorized, "Demo API key")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    public async Task ApiErrorsAreReadableAndCanBeRetried(HttpStatusCode code, string message)
    {
        var handler = new StubHandler(_ => new(code));
        using var client = new HttpClient(handler);
        var provider = new CoinGeckoInfoProvider(client);
        for (var i = 0; i < 2; i++)
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetInfoAsync("ethena", TestContext.Current.CancellationToken));
            Assert.Contains(message, ex.Message);
        }
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SingleMatchLoadsBaseCoinRatherThanQuoteCoin()
    {
        var provider = Provider([new("ethena", "Ethena", "ENA", 39)]);
        using var vm = new CoinInfoViewModel("ENA/USDT", provider.Object);

        await vm.LoadAsync();

        provider.Verify(p => p.SearchAsync("ENA", It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.GetInfoAsync("ethena", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(vm.HasInfo);
        Assert.Equal("Ethena", vm.Name);
        Assert.Equal("Description", vm.Description);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasChoices);
    }

    [Fact]
    public async Task DuplicateSymbolsRequireProjectChoice()
    {
        var provider = Provider([new("ethena", "Ethena", "ENA", 39), new("other-ena", "Other ENA", "ENA", null)]);
        using var vm = new CoinInfoViewModel("ENA/USDT", provider.Object);

        await vm.LoadAsync();

        Assert.True(vm.HasChoices);
        Assert.False(vm.HasInfo);
        Assert.Null(vm.SelectedCandidate);
        provider.Verify(p => p.GetInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.SelectedCandidate = vm.Candidates[0];
        Assert.True(vm.HasInfo);
    }

    [Fact]
    public async Task ChangingProjectCancelsAndIgnoresOldDescription()
    {
        var oldResponse = new TaskCompletionSource<CoinInfo>();
        var provider = Provider([new("ethena", "Ethena", "ENA", 39), new("other-ena", "Other ENA", "ENA", null)]);
        CancellationToken oldToken = default;
        provider.Setup(p => p.GetInfoAsync("ethena", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => { oldToken = ct; return oldResponse.Task; });
        using var vm = new CoinInfoViewModel("ENA/USDT", provider.Object);
        await vm.LoadAsync();

        var oldLoad = vm.SelectCandidateAsync(vm.Candidates[0]);
        await vm.SelectCandidateAsync(vm.Candidates[1]);
        oldResponse.SetResult(Info("ethena"));
        await oldLoad;

        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal("https://www.coingecko.com/en/coins/other-ena", vm.SourceUrl);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task ClosingWindowCancelsSearchAndDoesNotPopulateItLater()
    {
        var response = new TaskCompletionSource<IReadOnlyList<CoinSearchResult>>();
        var provider = new Mock<ICoinInfoProvider>();
        CancellationToken token = default;
        provider.Setup(p => p.SearchAsync("ENA", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => { token = ct; return response.Task; });
        var vm = new CoinInfoViewModel("ENA/USDT", provider.Object);
        var load = vm.LoadAsync();

        vm.Dispose();
        response.SetResult([new("ethena", "Ethena", "ENA", 39)]);
        await load;

        Assert.True(token.IsCancellationRequested);
        Assert.Empty(vm.Candidates);
        Assert.False(vm.HasInfo);
        provider.Verify(p => p.GetInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptySearchAndMissingDescriptionHaveExplicitMessages()
    {
        var provider = Provider([]);
        using var vm = new CoinInfoViewModel("ENA/USDT", provider.Object);
        await vm.LoadAsync();
        Assert.Contains("not found", vm.StatusText);

        provider.Setup(p => p.SearchAsync("ENA", It.IsAny<CancellationToken>())).ReturnsAsync([new("ethena", "Ethena", "ENA", 39)]);
        provider.Setup(p => p.GetInfoAsync("ethena", It.IsAny<CancellationToken>())).ReturnsAsync(Info("ethena") with { Description = "", Language = "" });
        await vm.LoadAsync();
        Assert.True(vm.HasInfo);
        Assert.Contains("no description", vm.Description);
    }

    [Fact]
    public async Task WrongSymbolIsNotDisplayed()
    {
        var provider = Provider([new("ethena", "Ethena", "ENA", 39)]);
        provider.Setup(p => p.GetInfoAsync("ethena", It.IsAny<CancellationToken>())).ReturnsAsync(Info("ethena") with { Symbol = "USDT" });
        using var vm = new CoinInfoViewModel("ENA/USDT", provider.Object);
        await vm.LoadAsync();
        Assert.False(vm.HasInfo);
        Assert.Contains("a different coin", vm.StatusText);
    }

    [Fact]
    public void InfoMenuUsesClickedPairAcrossFavoritesMoversAndSearch()
    {
        OnStaThread(() =>
        {
            foreach (var data in new object[] { new TradingPairModel("ENA/USDT"), new MarketMover("ENA/USDT", 5), "ENA/USDT" })
            {
                var row = new ListBoxItem { DataContext = data };
                string? openedPair = null;
                string? bookPair = null;
                var menu = CoinInfoContextMenu.Create(row, pair => openedPair = pair, (pair, _) => bookPair = pair)!;
                Assert.Same(row, menu.PlacementTarget);
                //Assert.Equal(2, menu.Items.Count);
                var item = Assert.IsType<MenuItem>(menu.Items[0]);
                Assert.Equal("Info", item.Header);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("ENA/USDT", openedPair);
                var bookItem = Assert.IsType<MenuItem>(menu.Items[1]);
                Assert.Equal("Order book", bookItem.Header);
                bookItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("ENA/USDT", bookPair);
            }
        });
    }

    [Fact]
    public void InfoWindowLoadsBindingsAndKeepsLongDescriptionScrollableAtMinimumSize()
    {
        OnStaThread(() =>
        {
            var provider = Provider([new("ethena", "Ethena", "ENA", 39)]);
            provider.Setup(p => p.GetInfoAsync("ethena", It.IsAny<CancellationToken>())).ReturnsAsync(Info("ethena") with
            {
                Description = string.Join("\n\n", Enumerable.Repeat("A long project description with details about the token purpose and protocol.", 40))
            });
            var window = new CoinInfoWindow("ENA/USDT", provider.Object);
            using var vm = (CoinInfoViewModel)window.DataContext;
            vm.LoadAsync().GetAwaiter().GetResult();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(460, 320));
            content.Arrange(new Rect(0, 0, 460, 320));
            content.UpdateLayout();
            var description = (TextBox)window.FindName("DescriptionText");
            Assert.Equal(vm.Description, description.Text);
            Assert.Equal(ScrollBarVisibility.Auto, description.VerticalScrollBarVisibility);
            Assert.True(description.ActualHeight > 0);
            Assert.True(description.ExtentHeight > description.ViewportHeight);
            // Rendering verifies the generated WPF resources without opening a desktop window.
            var bitmap = new RenderTargetBitmap(460, 320, 96, 96, PixelFormats.Pbgra32);
            ((Grid)content).Background = window.Background;
            bitmap.Render(content);
            Assert.Equal(460, bitmap.PixelWidth);
            // Optional artifact for manual visual review of the smallest supported layout.
            if (Environment.GetEnvironmentVariable("BOTVIEW_COIN_INFO_PREVIEW") is { Length: > 0 } previewPath)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(previewPath);
                encoder.Save(output);
            }
        });
    }

    private static Mock<ICoinInfoProvider> Provider(IReadOnlyList<CoinSearchResult> matches)
    {
        var provider = new Mock<ICoinInfoProvider>();
        provider.Setup(p => p.SearchAsync("ENA", It.IsAny<CancellationToken>())).ReturnsAsync(matches);
        provider.Setup(p => p.GetInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken _) => Task.FromResult(Info(id)));
        return provider;
    }

    private static CoinInfo Info(string id) => new(id, "Ethena", "ENA", "Description", "en", "https://ethena.fi/", ["DeFi"]);

    private static void OnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF verification timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? ApiKey { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri!.AbsoluteUri;
            ApiKey = request.Headers.TryGetValues("x-cg-demo-api-key", out var keys) ? keys.Single() : null;
            return Task.FromResult(respond(request));
        }
    }
}
