using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Covers serial execution, request coalescing, periodic work, and application shutdown.
    /// </summary>
    [TestClass]
    public class AppTests
    {
        /// <summary>
        /// Simulates Home Assistant so service callbacks can be tested without a server.
        /// </summary>
        private readonly Mock<IHaContext> ha = new();

        /// <summary>
        /// Records log messages so failure reports and quiet cancellation can be checked.
        /// </summary>
        private readonly Mock<ILogger<App>> logger = new();

        /// <summary>
        /// Supplies controlled LED inputs so scheduling can be tested without real sensors.
        /// </summary>
        private readonly Mock<ILedInputMonitor> monitor = new();

        /// <summary>
        /// Controls sync completion and failures so queue behavior can be checked without device traffic.
        /// </summary>
        private readonly Mock<IDimmerSyncManager> dimmers = new();

        /// <summary>
        /// Records refresh requests so their order and cancellation can be checked without pinging devices.
        /// </summary>
        private readonly Mock<IZWavePingManager> pings = new();

        /// <summary>
        /// Supplies the test configuration so each scenario can choose its own timer intervals.
        /// </summary>
        private readonly Mock<IAppConfig<Config>> appConfig = new();

        /// <summary>
        /// Advances timer time on demand so periodic operations can be tested without delays.
        /// </summary>
        private readonly HistoricalScheduler scheduler = new();

        /// <summary>
        /// Emits LED tables on demand so tests can reproduce input bursts and subscription cleanup.
        /// </summary>
        private readonly Subject<LedInputTable> inputs = new();

        /// <summary>
        /// Emits Home Assistant events so tests can call the registered service and check cleanup.
        /// </summary>
        private readonly Subject<Event> events = new();

        /// <summary>
        /// Holds timer settings that each test can adjust to exercise enabled and disabled scheduling.
        /// </summary>
        private readonly Config config = new();

        [TestInitialize]
        public void Initialize()
        {
            this.ha.SetupGet(h => h.Events).Returns(this.events);
            this.monitor.SetupGet(m => m.AllInputTableChanges).Returns(this.inputs);
            this.appConfig.SetupGet(c => c.Value).Returns(this.config);
            this.dimmers.Setup(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            this.pings.Setup(p => p.PingDevicesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        }

        [TestCleanup]
        public void Cleanup()
        {
            this.inputs.Dispose();
            this.events.Dispose();
        }

        [TestMethod]
        public async Task InputBurstsCoalesceIntoOnePendingSyncUsingTheLatestTable()
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.config.ZWavePingInterval = TimeSpan.FromSeconds(1);
            this.pings.Setup(p => p.PingDevicesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => drained.SetResult()).Returns(Task.CompletedTask);
            int calls = 0;
            LedInputTable? secondInput = null;
            this.dimmers.Setup(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>()))
                .Returns(async (LedInputTable table, CancellationToken token) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        entered.SetResult();
                        await release.Task.WaitAsync(token);
                    }
                    else
                    {
                        secondInput = table;
                    }
                });
            await using App app = this.CreateApp();
            this.inputs.OnNext(new LedInputTable());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 99; ++i)
            {
                this.inputs.OnNext(new LedInputTable()
                    .CreateWithColor(6, (LedStatusColor)(1 + (i % 7)))
                    .CreateWithBlink(6, i % 2 == 0 ? LedBlink.On : LedBlink.Off));
            }
            LedInputTable latest = new LedInputTable()
                .CreateWithColor(0, LedStatusColor.Blue)
                .CreateWithColor(6, LedStatusColor.White)
                .CreateWithBlink(6, LedBlink.On);
            this.inputs.OnNext(latest);
            this.scheduler.AdvanceBy(TimeSpan.FromSeconds(1));

            release.SetResult();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));

            calls.Should().Be(2);
            secondInput.Should().BeEquivalentTo(latest, options => options.WithStrictOrdering());
        }

        [TestMethod]
        public async Task SyncAndPingTimersRemainSerialAndCoalesceWhileWorkIsBlocked()
        {
            this.config.LedSyncInterval = TimeSpan.FromSeconds(1);
            this.config.ZWavePingInterval = TimeSpan.FromSeconds(1);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource pingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            bool syncActive = false;
            this.dimmers.Setup(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>()))
                .Returns(async (LedInputTable _, CancellationToken token) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        syncActive = true;
                        entered.SetResult();
                        await release.Task.WaitAsync(token);
                        syncActive = false;
                    }
                });
            this.pings.Setup(p => p.PingDevicesAsync(It.IsAny<CancellationToken>())).Returns(() =>
            {
                syncActive.Should().BeFalse();
                pingEntered.SetResult();
                return Task.CompletedTask;
            });
            await using App app = this.CreateApp();
            this.inputs.OnNext(new LedInputTable());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            this.scheduler.AdvanceBy(TimeSpan.FromSeconds(10));
            this.pings.Verify(p => p.PingDevicesAsync(It.IsAny<CancellationToken>()), Times.Never);
            release.SetResult();
            await pingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            calls.Should().Be(2);
            this.pings.Verify(p => p.PingDevicesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task DisposalCancelsActiveWorkAndDiscardsAllPendingAndFutureWork()
        {
            this.config.LedSyncInterval = TimeSpan.FromSeconds(1);
            this.config.ZWavePingInterval = TimeSpan.FromSeconds(1);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken activeToken = default;
            this.dimmers.Setup(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>()))
                .Returns(async (LedInputTable _, CancellationToken token) =>
                {
                    activeToken = token;
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            await using App app = this.CreateApp();
            this.inputs.OnNext(new LedInputTable());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            this.scheduler.AdvanceBy(TimeSpan.FromSeconds(10));

            Task firstDisposal = app.DisposeAsync().AsTask();
            Task secondDisposal = app.DisposeAsync().AsTask();
            await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(TimeSpan.FromSeconds(5));
            this.inputs.OnNext(new LedInputTable());
            this.CallSyncService();
            this.scheduler.AdvanceBy(TimeSpan.FromSeconds(10));

            secondDisposal.Should().BeSameAs(firstDisposal);
            activeToken.IsCancellationRequested.Should().BeTrue();
            this.inputs.HasObservers.Should().BeFalse();
            this.events.HasObservers.Should().BeFalse();
            this.dimmers.Verify(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>()), Times.Once);
            this.pings.Verify(p => p.PingDevicesAsync(It.IsAny<CancellationToken>()), Times.Never);
            this.logger.Invocations.Should().NotContain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Error));
        }

        [TestMethod]
        [DataRow("command")]
        [DataRow("timeout")]
        [DataRow("json")]
        [DataRow("cancellation")]
        public async Task RecoverableFailuresDoNotStopSubsequentRequests(string failureType)
        {
            Exception failure = failureType switch
            {
                "timeout" => new TimeoutException("Request timed out"),
                "json" => new JsonException("Invalid response"),
                "cancellation" => new OperationCanceledException("Request was cancelled independently"),
                _ => new InvalidOperationException("Temporary failure")
            };
            TaskCompletionSource failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource succeeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            this.dimmers.Setup(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        failed.SetResult();
                        return Task.FromException(failure);
                    }
                    succeeded.SetResult();
                    return Task.CompletedTask;
                });
            await using App app = this.CreateApp();
            this.inputs.OnNext(new LedInputTable());
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            this.inputs.OnNext(new LedInputTable().CreateWithColor(0, LedStatusColor.Red));
            await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));

            this.logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Error));
        }

        [TestMethod]
        public async Task UnexpectedWorkerFailuresAreLoggedAndObservedDuringDisposal()
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource operation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.dimmers.Setup(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>()))
                .Callback(() => entered.SetResult()).Returns(operation.Task);
            App app = this.CreateApp();
            this.inputs.OnNext(new LedInputTable());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            operation.SetException(new NotSupportedException("Unexpected implementation failure"));
            await Assert.ThrowsAsync<NotSupportedException>(() => app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

            this.inputs.HasObservers.Should().BeFalse();
            this.events.HasObservers.Should().BeFalse();
            this.logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && Equals(invocation.Arguments[0], LogLevel.Error));
        }

        [TestMethod]
        public async Task TheRegisteredServiceSchedulesSynchronization()
        {
            TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.dimmers.Setup(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(), It.IsAny<CancellationToken>()))
                .Callback(() => synced.SetResult()).Returns(Task.CompletedTask);
            await using App app = this.CreateApp();

            this.CallSyncService();
            await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));

            this.dimmers.Verify(d => d.SyncDimmersToAsync(It.IsAny<LedInputTable>(),
                It.Is<CancellationToken>(token => token.CanBeCanceled)), Times.Once);
        }

        [TestMethod]
        public async Task ZeroIntervalsDoNotSchedulePeriodicWork()
        {
            await using App app = this.CreateApp();

            this.scheduler.AdvanceBy(TimeSpan.FromDays(1));

            this.dimmers.VerifyNoOtherCalls();
            this.pings.VerifyNoOtherCalls();
        }

        [TestMethod]
        public void FailedConstructionReleasesInputAndTimerSubscriptions()
        {
            this.config.LedSyncInterval = TimeSpan.FromSeconds(1);
            this.ha.Setup(h => h.CallService(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<object>()))
                .Throws(new InvalidOperationException("Unable to register service"));

            Assert.Throws<InvalidOperationException>(() => this.CreateApp());
            this.inputs.OnNext(new LedInputTable());
            this.scheduler.AdvanceBy(TimeSpan.FromSeconds(10));

            this.inputs.HasObservers.Should().BeFalse();
            this.dimmers.VerifyNoOtherCalls();
        }

        /// <summary>
        /// Creates the app with the test's controlled services so every scenario uses the same setup.
        /// </summary>
        /// <returns>The app whose work and subscriptions the test must dispose.</returns>
        private App CreateApp()
        {
            return new App(this.ha.Object, this.scheduler, this.logger.Object, this.monitor.Object,
                this.dimmers.Object, this.pings.Object, this.appConfig.Object);
        }

        /// <summary>
        /// Emits the service-call event expected by NetDaemon so the real callback path is exercised.
        /// </summary>
        private void CallSyncService()
        {
            this.events.OnNext(new Event
            {
                EventType = "call_service",
                DataElement = JsonSerializer.SerializeToElement(new
                {
                    domain = "netdaemon",
                    service = "synchronize_dimmers",
                    service_data = new { }
                })
            });
        }
    }
}