using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotView.Services.Commands;
using BotView.Windows;

namespace BotView.Tests;

public sealed class BookCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "commands-" + Guid.NewGuid().ToString("N"));
    private static readonly InstrumentKey Future = new("MOEX", "futures", "SPBFUT", "SAME");
    private static readonly InstrumentKey Share = new("MOEX", "shares", "TQBR", "SAME");

    /// <summary>Creates an isolated preference store without accessing the user's LocalAppData.</summary>
    private BookCommandSettingsStore Store() => new(Path.Combine(_directory, "commands.json"));

    /// <summary>The complete registry has unique defaults and explicitly disabled reversal/unassigned actions.</summary>
    [Fact]
    public void RegistryAndDefaultsAreComplete()
    {
        Assert.Equal(Enum.GetValues<BookCommand>().Length, BookCommandCatalog.All.Count);
        var defaults = BookCommandCatalog.Defaults();
        BookCommandSettingsStore.Validate(new BookCommandDocument(1, defaults, []));
        Assert.False(defaults.Single(b => b.Command == BookCommand.ReversePosition).Enabled);
        Assert.All(defaults.Where(b => b.Key == Key.None), b => Assert.False(b.Enabled));
        Assert.Equal(5, BookCommandCatalog.All.Count(d => d.Fixed));
        Assert.DoesNotContain(defaults, b => b.Key is >= Key.NumPad0 and <= Key.NumPad9);
    }

    /// <summary>Every executable command reaches only a stub with full account/listing/gesture context.</summary>
    [Fact]
    public void EveryCommandCanBeInvokedWithoutATradingSession()
    {
        var store = Store();
        foreach (var definition in BookCommandCatalog.All.Where(d => !d.Fixed && d.Id is not (BookCommand.InstrumentSettings or BookCommand.QuantityUnits)))
        {
            var bindings = BookCommandCatalog.Defaults().Select(b => b with { Enabled = b.Command == definition.Id ||
                BookCommandCatalog.All.Single(d => d.Id == b.Command).Fixed }).ToArray();
            var index = Array.FindIndex(bindings, b => b.Command == definition.Id);
            bindings[index] = bindings[index] with { Key = definition.Key == Key.None ? Key.F12 : definition.Key, Enabled = true };
            store.Save(new(1, bindings, []));
            var handler = new RecordingHandler();
            var profile = Guid.NewGuid();
            var controller = new BookCommandController("quik:one", Future, store, handler, () => (profile, "account-one"));

            Assert.True(controller.KeyDown(bindings[index].Key));
            switch (definition.Gesture)
            {
                case CommandGesture.HoldClick:
                case CommandGesture.ClickOnce:
                    Assert.True(controller.Click(101.25m, MouseButton.Right));
                    break;
                case CommandGesture.HoldWheel:
                    Assert.True(controller.Wheel(-120));
                    break;
                case CommandGesture.TwoPrices:
                    controller.Click(100, MouseButton.Left);
                    Assert.Empty(handler.Calls);
                    controller.Click(102, MouseButton.Left);
                    break;
            }

            var invocation = Assert.Single(handler.Calls);
            Assert.Equal(definition.Id, invocation.Command);
            Assert.Equal(Future, invocation.Instrument);
            Assert.Equal(profile, invocation.ProfileId);
            Assert.Equal("account-one", invocation.AccountId);
            Assert.Equal("quik:one", invocation.Connection);
            Assert.Null(invocation.Volume);
            Assert.Equal(InvocationStatus.Stub, controller.LastResult!.Status);
            Assert.Contains("не выполнено", controller.LastResult.Message);
        }
    }

    /// <summary>Held, one-shot and ruler modes swallow invalid/double input and reset on release or cancellation.</summary>
    [Fact]
    public void GesturesDoNotLeakClicksOrRepeatInvocations()
    {
        var handler = new RecordingHandler();
        var controller = new BookCommandController("quik:one", Future, Store(), handler);
        controller.KeyDown(Key.C);
        Assert.True(controller.Click(null, MouseButton.Left));
        Assert.True(controller.Click(100, MouseButton.Left, 2));
        Assert.True(controller.Wheel(120));
        Assert.Empty(handler.Calls);
        controller.KeyUp(Key.C);
        Assert.False(controller.Click(100, MouseButton.Left));
        controller.KeyDown(Key.V);
        controller.KeyDown(Key.V);
        Assert.False(controller.Click(100, MouseButton.Left));
        controller.KeyDown(Key.G);
        controller.Click(99, MouseButton.Left);
        controller.KeyDown(Key.Escape);
        Assert.False(controller.Click(101, MouseButton.Left));
        controller.KeyDown(Key.T);
        controller.KeyDown(Key.T, true);
        Assert.Single(handler.Calls);
        controller.KeyDown(Key.H);
        controller.KeyDown(Key.L);
        controller.KeyUp(Key.H);
        Assert.NotNull(controller.ActiveMode);
        controller.KeyDown(Key.Q);
        Assert.Null(controller.ActiveMode);
        Assert.Equal(BookCommand.Interrupt, handler.Calls.Last().Command);
    }

    /// <summary>Presets and monetary mode change only preferences; stubs preserve nullable unknown parameters.</summary>
    [Fact]
    public void PresetsArePersistentDetachedAndScopedByConnectionAndListing()
    {
        var store = Store();
        store.Put("one", Future, new() { Quantities = [1, 2, 3, 4, 5], Budgets = [10, 20, 30, 40, 50] });
        var controller = new BookCommandController("one", Future, store, new StubCommandInvocationHandler());
        controller.KeyDown(Key.D3);
        controller.KeyDown(Key.M);
        controller.KeyDown(Key.T);
        Assert.Equal(30, controller.LastResult!.Invocation.Volume);
        Assert.Null(controller.LastResult.Invocation.Price);
        Assert.Null(controller.LastResult.Invocation.AccountId);
        controller.Settings.Budgets[2] = 999;
        Assert.Equal(30, controller.LastResult.Invocation.Settings.Budgets[2]);
        Assert.Equal(30, store.Get("one", Future).Budgets[2]);
        Assert.Null(store.Get("two", Future).Budgets[2]);
        Assert.Null(store.Get("one", Share).Budgets[2]);
        var restored = new BookCommandController("one", Future, store, new StubCommandInvocationHandler());
        Assert.Equal(2, restored.Settings.SelectedVolume);
        Assert.True(restored.Settings.Monetary);
        Assert.False(restored.KeyDown(Key.NumPad3));
    }

    /// <summary>Closing and reversal are not dispatched for equities even when their bindings are enabled.</summary>
    [Fact]
    public void FuturesOnlyCommandsAndSettingsHaveExplicitBehavior()
    {
        var store = Store();
        var document = store.Load();
        store.Save(document with { Bindings = document.Bindings.Select(b => b with { Enabled = b.Key != Key.None }).ToArray() });
        var handler = new RecordingHandler();
        var controller = new BookCommandController("one", Share, store, handler);
        controller.KeyDown(Key.D);
        controller.KeyDown(Key.R);
        Assert.Empty(handler.Calls);
        var settings = 0;
        controller.SettingsRequested += () => settings++;
        controller.KeyDown(Key.F4);
        Assert.Equal(1, settings);
        Assert.Empty(handler.Calls);
    }

    /// <summary>Protection, stop, trailing and auxiliary parameters round-trip as one validated document.</summary>
    [Fact]
    public void SettingsRoundTripAndInvalidImportsLeaveTheFileUnchanged()
    {
        var store = Store();
        var settings = new InstrumentCommandSettings
        {
            StopLoss = new() { Enabled = true, Distance = 5, Placement = ProtectionPlacement.Automatic, Location = ExecutionLocation.Server },
            TakeProfit = new() { Enabled = true, Distance = 2, Unit = DistanceUnit.Percent, Volume = VolumeSource.WorkingVolume },
            Trailing = new() { Enabled = true, Distance = 4, Step = 1, Activation = 0 },
            Stop = new() { Execution = StopExecution.Limit, Side = OrderSide.Sell, TriggerPrice = 100, Offset = 2 },
            LevelLabel = "уровень", LevelColor = "#123456", SignalNotification = false,
            FastScrollSteps = 90, StepIncrement = 10, RulerUnits = RulerUnit.Volume,
            MarketDataSource = "source", MarketDataScope = MarketDataScope.AllConnections
        };
        store.Put("one", Future, settings);
        var restored = store.Get("one", Future);
        Assert.Equal(settings.StopLoss, restored.StopLoss);
        Assert.Equal(settings.TakeProfit, restored.TakeProfit);
        Assert.Equal(settings.Trailing, restored.Trailing);
        Assert.Equal(settings.Stop, restored.Stop);
        Assert.Equal("source", restored.MarketDataSource);
        Assert.Equal(90, restored.FastScrollSteps);
        Assert.Equal(RulerUnit.Volume, restored.RulerUnits);
        var export = Path.Combine(_directory, "export.json");
        store.Export(export);
        var previous = File.ReadAllText(Path.Combine(_directory, "commands.json"));
        File.WriteAllText(export, previous.Replace("\"Version\": 1", "\"Version\": 999"));
        Assert.Throws<InvalidDataException>(() => store.Import(export));
        Assert.Equal(previous, File.ReadAllText(Path.Combine(_directory, "commands.json")));
        var bindings = store.Load().Bindings;
        bindings[0] = bindings[0] with { Key = Key.D1 };
        Assert.Throws<InvalidDataException>(() => store.Save(store.Load() with { Bindings = bindings }));
        Assert.Throws<InvalidDataException>(() => store.Put("one", Future, settings with { Trailing = new() { Enabled = true } }));
    }

    /// <summary>Builds every settings page on STA and optionally exports a visual preview without opening a live terminal.</summary>
    [Fact]
    public void SettingsDialogsRenderAndExposeAllParameterPages()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var store = Store();
                var window = new InstrumentCommandSettingsWindow(store, "quik:one", new(Future, "SPBFUT:SAME", "Future", "RUB",
                    TradingRules: new(QuantityUnit.Contracts, PriceQuotation.Points, SettlementCurrency: "RUB")));
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(650, 650));
                root.Arrange(new Rect(0, 0, 650, 650));
                root.UpdateLayout();
                var tabs = ((DockPanel)root).Children.OfType<TabControl>().Single();
                Assert.Equal(5, tabs.Items.Count);
                foreach (TabItem tab in tabs.Items)
                {
                    tabs.SelectedItem = tab;
                    root.UpdateLayout();
                    Assert.True(((ScrollViewer)tab.Content).ActualWidth > 100);
                }
                tabs.SelectedIndex = 1;
                var protectionPanel = (StackPanel)((ScrollViewer)((TabItem)tabs.Items[1]).Content).Content;
                var enable = protectionPanel.Children.OfType<Grid>().First(g => ((TextBlock)g.Children[0]).Text == "Включён в настройках");
                ((CheckBox)enable.Children[1]).IsChecked = true;
                var distance = protectionPanel.Children.OfType<Grid>().First(g => ((TextBlock)g.Children[0]).Text == "Расстояние");
                ((TextBox)distance.Children[1]).Text = "5";
                var placement = protectionPanel.Children.OfType<Grid>().First(g => ((TextBlock)g.Children[0]).Text == "Способ установки");
                ((ComboBox)placement.Children[1]).SelectedValue = ProtectionPlacement.Automatic;
                Assert.True(window.TrySave());
                Assert.True(store.Get("quik:one", Future).StopLoss.Enabled);
                Assert.Equal(5, store.Get("quik:one", Future).StopLoss.Distance);
                Assert.Equal(ProtectionPlacement.Automatic, store.Get("quik:one", Future).StopLoss.Placement);
                ((TextBox)distance.Children[1]).Text = "";
                Assert.False(window.TrySave());
                Assert.Equal(5, store.Get("quik:one", Future).StopLoss.Distance);
                ((TextBox)distance.Children[1]).Text = "5";
                tabs.SelectedIndex = 1;
                root.UpdateLayout();
                if (Environment.GetEnvironmentVariable("BOTVIEW_COMMAND_PREVIEW") is { Length: > 0 } path)
                {
                    var bitmap = new RenderTargetBitmap(650, 650, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(path);
                    encoder.Save(output);
                }
                window.Close();
                var hotkeys = new HotkeysSettingsWindow(store);
                var panel = (FrameworkElement)hotkeys.Content;
                panel.Measure(new Size(760, 650));
                panel.Arrange(new Rect(0, 0, 760, 650));
                panel.UpdateLayout();
                Assert.True(panel.ActualHeight > 400);
                hotkeys.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RecordingHandler : ICommandInvocationHandler
    {
        public List<CommandInvocation> Calls { get; } = [];

        /// <summary>Records intent while delegating results to the same transport-free stub used by the UI.</summary>
        public CommandInvocationResult Invoke(CommandInvocation invocation)
        {
            Calls.Add(invocation);
            return new StubCommandInvocationHandler().Invoke(invocation);
        }
    }

    /// <summary>Removes only this test instance's generated directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
