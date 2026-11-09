using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Covers input initialization, subscriptions, and supported LED states.
    /// </summary>
    [TestClass]
    public class LedInputMonitorTests
    {
        /// <summary>
        /// Supplies cached states and events so the monitor can be tested without Home Assistant.
        /// </summary>
        private readonly Mock<IHaContext> mockHaContext = new();

        /// <summary>
        /// Accepts monitor log messages so input parsing tests do not require a real logging provider.
        /// </summary>
        private readonly Mock<ILogger<LedInputMonitor>> mockLogger = new();

        /// <summary>
        /// Supplies editable sensor patterns so valid and invalid configuration can be tested.
        /// </summary>
        private readonly Mock<IAppConfig<Config>> mockAppConfig = new();

        /// <summary>
        /// Represents Home Assistant's state cache so subscriptions can read the latest sensor values.
        /// </summary>
        private readonly Dictionary<string, EntityState> states = [];

        /// <summary>
        /// Emits controlled state changes so event ordering and subscription cleanup can be tested.
        /// </summary>
        private readonly Subject<StateChange> stateChanges = new();

        /// <summary>
        /// Holds sensor name patterns that tests can change to exercise constructor validation.
        /// </summary>
        private readonly Config config = new()
        {
            DimmerLedColorEntityNamePattern = "sensor.dimmer_led_{0}_color",
            DimmerLedBlinkEntityNamePattern = "binary_sensor.dimmer_led_{0}_blink"
        };

        [TestInitialize]
        public void Initialize()
        {
            _ = this.mockAppConfig.Setup(m => m.Value).Returns(this.config);
            _ = this.mockHaContext.Setup(m => m.StateAllChanges()).Returns(this.stateChanges);
            _ = this.mockHaContext.Setup(m => m.GetState(It.IsAny<string>()))
                .Returns((string id) => this.states.GetValueOrDefault(id));
            for (int i = 1; i <= LedInputTable.NumberOfLeds; ++i)
            {
                this.states[$"sensor.dimmer_led_{i}_color"] = new EntityState { State = "Off" };
                this.states[$"binary_sensor.dimmer_led_{i}_blink"] = new EntityState { State = "off" };
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            this.stateChanges.Dispose();
        }

        [TestMethod]
        [DataRow("", "binary_sensor.dimmer_led_{0}_blink")]
        [DataRow(" ", "binary_sensor.dimmer_led_{0}_blink")]
        [DataRow("sensor.dimmer_led_{0}_color", "")]
        [DataRow("sensor.dimmer_led_{0}_color", " ")]
        [DataRow("sensor.dimmer_led_{0}_color", "sensor.dimmer_led_{0}_color")]
        [DataRow("sensor.dimmer_led_{0}_color", "SENSOR.DIMMER_LED_{0}_COLOR")]
        public void ConstructorRejectsInvalidPatterns(string colorPattern, string blinkPattern)
        {
            this.config.DimmerLedColorEntityNamePattern = colorPattern;
            this.config.DimmerLedBlinkEntityNamePattern = blinkPattern;

            _ = Assert.Throws<ArgumentException>(() => this.CreateMonitor());
        }

        [TestMethod]
        public void SubscriptionReadsInitialValuesIncludingTheFirstAndLastLeds()
        {
            this.states["sensor.dimmer_led_1_color"] = new EntityState { State = "Red" };
            this.states["sensor.dimmer_led_7_color"] = new EntityState { State = "Green" };
            this.states["binary_sensor.dimmer_led_7_blink"] = new EntityState { State = "on" };
            LedInputMonitor monitor = this.CreateMonitor();
            _ = this.stateChanges.HasObservers.Should().BeFalse();
            List<LedInputTable> capture = [];

            using IDisposable subscription = monitor.AllInputTableChanges.Subscribe(capture.Add);

            _ = capture.Should().ContainSingle().Which.Should().BeEquivalentTo(new LedInputTable()
                .CreateWithColor(0, LedStatusColor.Red)
                .CreateWithColor(6, LedStatusColor.Green)
                .CreateWithBlink(6, LedBlink.On), options => options.WithStrictOrdering());
        }

        [TestMethod]
        public void FirstSubscriptionIncludesChangesMadeAfterConstruction()
        {
            LedInputMonitor monitor = this.CreateMonitor();
            this.SetState("sensor.dimmer_led_1_color", "Green");
            List<LedInputTable> capture = [];

            using IDisposable subscription = monitor.AllInputTableChanges.Subscribe(capture.Add);
            this.SetState("binary_sensor.dimmer_led_2_blink", "on");

            _ = capture.Should().HaveCount(2);
            _ = capture.Should().OnlyContain(table => table.Colors[0] == LedStatusColor.Green);
            _ = capture.Last().Blinks[1].Should().Be(LedBlink.On);
        }

        [TestMethod]
        public void LaterSubscribersReceiveCurrentValuesWithoutChangingExistingSubscribers()
        {
            LedInputMonitor monitor = this.CreateMonitor();
            List<LedInputTable> first = [];
            List<LedInputTable> second = [];
            using IDisposable firstSubscription = monitor.AllInputTableChanges.Subscribe(first.Add);
            this.SetState("sensor.dimmer_led_1_color", "Red");

            using IDisposable secondSubscription = monitor.AllInputTableChanges.Subscribe(second.Add);

            _ = first.Should().HaveCount(2);
            _ = second.Should().ContainSingle().Which.Colors[0].Should().Be(LedStatusColor.Red);
            this.SetState("binary_sensor.dimmer_led_7_blink", "on");
            _ = first.Last().Should().BeEquivalentTo(second.Last(), options => options.WithStrictOrdering());
            _ = first.Should().HaveCount(3);
            _ = second.Should().HaveCount(2);
        }

        [TestMethod]
        public void ResubscriptionReconcilesChangesMadeWhileThereWereNoObservers()
        {
            LedInputMonitor monitor = this.CreateMonitor();
            using (monitor.AllInputTableChanges.Subscribe(_ => { }))
            {
                this.SetState("sensor.dimmer_led_1_color", "Red");
            }
            _ = this.stateChanges.HasObservers.Should().BeFalse();
            this.SetState("sensor.dimmer_led_1_color", "Blue");
            List<LedInputTable> capture = [];

            using IDisposable subscription = monitor.AllInputTableChanges.Subscribe(capture.Add);

            _ = capture.Should().ContainSingle().Which.Colors[0].Should().Be(LedStatusColor.Blue);
        }

        [TestMethod]
        public void AnEventDuringInitializationTakesPrecedenceOverAnOlderCachedState()
        {
            _ = this.mockHaContext.Setup(m => m.GetState("sensor.dimmer_led_1_color")).Returns(() =>
            {
                this.SetState("sensor.dimmer_led_1_color", "Red");
                return new EntityState { State = "Off" };
            });
            List<LedInputTable> capture = [];

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            _ = capture.Should().ContainSingle().Which.Colors[0].Should().Be(LedStatusColor.Red);
        }

        [TestMethod]
        public void AnUnchangedEventDuringInitializationStillTakesPrecedenceOverTheCache()
        {
            _ = this.mockHaContext.Setup(m => m.GetState("sensor.dimmer_led_1_color")).Returns(() =>
            {
                this.SetState("sensor.dimmer_led_1_color", "Off");
                return new EntityState { State = "Red" };
            });
            List<LedInputTable> capture = [];

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            _ = capture.Should().ContainSingle().Which.Should().BeEquivalentTo(new LedInputTable(),
                options => options.WithStrictOrdering());
        }

        [TestMethod]
        public void EventsReceivedWhileSubscribingAreIncludedWithoutReadingTheirStaleCachedState()
        {
            _ = this.mockHaContext.Setup(m => m.StateAllChanges()).Returns(Observable.Create<StateChange>(observer =>
            {
                observer.OnNext(this.CreateChange("sensor.dimmer_led_1_color", "Red"));
                return this.stateChanges.Subscribe(observer);
            }));
            _ = this.mockHaContext.Setup(m => m.GetState("sensor.dimmer_led_1_color"))
                .Throws(new InvalidOperationException("The event already supplies the current state"));
            List<LedInputTable> capture = [];

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            _ = capture.Should().ContainSingle().Which.Should().BeEquivalentTo(
                new LedInputTable().CreateWithColor(0, LedStatusColor.Red), options => options.WithStrictOrdering());
            this.SetState("binary_sensor.dimmer_led_7_blink", "on");
            _ = capture.Should().HaveCount(2);
            _ = capture.Last().Should().BeEquivalentTo(new LedInputTable()
                .CreateWithColor(0, LedStatusColor.Red).CreateWithBlink(6, LedBlink.On),
                options => options.WithStrictOrdering());
        }

        [TestMethod]
        [DataRow("Off", LedStatusColor.Off)]
        [DataRow("red", LedStatusColor.Red)]
        [DataRow("GREEN", LedStatusColor.Green)]
        [DataRow("Blue", LedStatusColor.Blue)]
        [DataRow("Magenta", LedStatusColor.Magenta)]
        [DataRow("Yellow", LedStatusColor.Yellow)]
        [DataRow("Cyan", LedStatusColor.Cyan)]
        [DataRow(" White ", LedStatusColor.White)]
        public void SupportedSingleColorNamesAreAccepted(string state, LedStatusColor expected)
        {
            LedInputTable? latest = null;
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(table => latest = table);

            this.SetState("sensor.dimmer_led_7_color", state);

            _ = latest!.Colors[6].Should().Be(expected);
        }

        [TestMethod]
        [DataRow("unavailable")]
        [DataRow("unknown")]
        [DataRow("Some garbage")]
        [DataRow("8")]
        [DataRow("-1")]
        [DataRow("255")]
        [DataRow("2")]
        [DataRow("Red, Green")]
        [DataRow("")]
        public void InvalidOrUnavailableColorsClearAnActiveMonitoredLed(string state)
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);
            this.SetState("sensor.dimmer_led_1_color", "Red");
            _ = capture.Last().Colors[0].Should().Be(LedStatusColor.Red);

            this.SetState("sensor.dimmer_led_1_color", state);

            _ = capture.Should().HaveCount(3);
            _ = capture.Last().Colors[0].Should().Be(LedStatusColor.Off);
        }

        [TestMethod]
        [DataRow("unavailable")]
        [DataRow("unknown")]
        [DataRow("Other garbage")]
        [DataRow("off")]
        public void InvalidOrUnavailableBlinkStatesClearAnActiveMonitoredLed(string state)
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);
            this.SetState("binary_sensor.dimmer_led_7_blink", "on");
            _ = capture.Last().Blinks[6].Should().Be(LedBlink.On);

            this.SetState("binary_sensor.dimmer_led_7_blink", state);

            _ = capture.Should().HaveCount(3);
            _ = capture.Last().Blinks[6].Should().Be(LedBlink.Off);
        }

        [TestMethod]
        public void MissingAndRemovedInputsAreOff()
        {
            _ = this.states.Remove("sensor.dimmer_led_7_color");
            LedInputTable? latest = null;
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(table => latest = table);
            _ = latest!.Colors[6].Should().Be(LedStatusColor.Off);
            this.SetState("sensor.dimmer_led_7_color", "Red");
            this.SetState("binary_sensor.dimmer_led_7_blink", "on");

            this.SetState("sensor.dimmer_led_7_color", null);
            this.SetState("binary_sensor.dimmer_led_7_blink", null);

            _ = latest.Should().BeEquivalentTo(new LedInputTable(), options => options.WithStrictOrdering());
        }

        [TestMethod]
        public void UnmonitoredEntitiesDoNotProduceUpdates()
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            this.SetState("sensor.dimmer_led_0_color", "Red");
            this.SetState("binary_sensor.dimmer_led_8_blink", "on");
            this.SetState("sensor.unrelated", "Green");

            _ = capture.Should().ContainSingle();
        }

        [TestMethod]
        public void AttributeOnlyChangesDoNotProduceLedUpdates()
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            this.stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, "sensor.dimmer_led_1_color"),
                new EntityState { State = "Off" }, new EntityState { State = "Off" }));

            _ = capture.Should().ContainSingle();
        }

        [TestMethod]
        [DataRow("sensor.dimmer_led_1_color", "Red", "RED")]
        [DataRow("sensor.dimmer_led_1_color", "Red", " Red ")]
        [DataRow("sensor.dimmer_led_1_color", "unknown", "Some garbage")]
        [DataRow("sensor.dimmer_led_1_color", "unavailable", "UNAVAILABLE")]
        [DataRow("sensor.dimmer_led_1_color", "unknown", null)]
        [DataRow("binary_sensor.dimmer_led_7_blink", "on", "ON")]
        [DataRow("binary_sensor.dimmer_led_7_blink", "off", "OFF")]
        [DataRow("binary_sensor.dimmer_led_7_blink", "unavailable", "unknown")]
        [DataRow("binary_sensor.dimmer_led_7_blink", "unknown", null)]
        public void EquivalentParsedStatesDoNotPublishRedundantSnapshots(string entityId, string firstState, string? equivalentState)
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);
            this.SetState(entityId, firstState);
            LedInputTable current = capture.Last();
            int count = capture.Count;

            this.SetState(entityId, equivalentState);

            _ = capture.Should().HaveCount(count);
            _ = capture.Last().Should().BeSameAs(current);
            _ = capture[0].Should().BeEquivalentTo(new LedInputTable(), options => options.WithStrictOrdering());
        }

        [TestMethod]
        public void PublishedSnapshotsRemainUnchangedAfterFurtherUpdates()
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            this.SetState("sensor.dimmer_led_1_color", "Red");
            this.SetState("binary_sensor.dimmer_led_7_blink", "on");

            _ = capture.Should().HaveCount(3);
            _ = capture[0].Should().BeEquivalentTo(new LedInputTable(), options => options.WithStrictOrdering());
            _ = capture[1].Should().BeEquivalentTo(new LedInputTable().CreateWithColor(0, LedStatusColor.Red),
                options => options.WithStrictOrdering());
            _ = capture[2].Should().BeEquivalentTo(new LedInputTable()
                .CreateWithColor(0, LedStatusColor.Red).CreateWithBlink(6, LedBlink.On),
                options => options.WithStrictOrdering());
        }

        [TestMethod]
        public async Task ConcurrentEventsSerializeNotificationsAndPreserveBothLedUpdates()
        {
            using ManualResetEventSlim firstNotificationEntered = new();
            using ManualResetEventSlim releaseFirstNotification = new();
            TaskCompletionSource<Thread> secondEventStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ConcurrentQueue<LedInputTable> capture = new();
            int activeNotifications = 0;
            int overlappingNotifications = 0;
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(table =>
            {
                if (Interlocked.Increment(ref activeNotifications) > 1)
                {
                    _ = Interlocked.Increment(ref overlappingNotifications);
                }
                try
                {
                    capture.Enqueue(table);
                    if (table.Colors[0] == LedStatusColor.Red && table.Colors[6] == LedStatusColor.Off)
                    {
                        firstNotificationEntered.Set();
                        _ = releaseFirstNotification.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    }
                }
                finally
                {
                    _ = Interlocked.Decrement(ref activeNotifications);
                }
            });
            Task firstEvent = Task.Factory.StartNew(
                () => this.stateChanges.OnNext(this.CreateChange("sensor.dimmer_led_1_color", "Red")),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task secondEvent = Task.CompletedTask;
            try
            {
                _ = firstNotificationEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                secondEvent = Task.Factory.StartNew(() =>
                {
                    secondEventStarted.SetResult(Thread.CurrentThread);
                    this.stateChanges.OnNext(this.CreateChange("sensor.dimmer_led_7_color", "Green"));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await WaitForContentionAsync(secondEvent, secondEventStarted.Task);
                _ = overlappingNotifications.Should().Be(0);
            }
            finally
            {
                releaseFirstNotification.Set();
                await Task.WhenAll(firstEvent, secondEvent).WaitAsync(TimeSpan.FromSeconds(5));
            }

            _ = capture.Should().HaveCount(3);
            _ = capture.Last().Should().BeEquivalentTo(new LedInputTable()
                .CreateWithColor(0, LedStatusColor.Red).CreateWithColor(6, LedStatusColor.Green), options => options.WithStrictOrdering());
            _ = overlappingNotifications.Should().Be(0);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ConcurrentTerminationWaitsForTheActiveNotification(bool fail)
        {
            using ManualResetEventSlim notificationEntered = new();
            using ManualResetEventSlim releaseNotification = new();
            TaskCompletionSource<Thread> terminalEventStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            InvalidOperationException failure = new("State stream failed");
            Exception? observed = null;
            bool completed = false;
            bool terminalOverlapped = false;
            int activeNotifications = 0;
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(table =>
            {
                _ = Interlocked.Increment(ref activeNotifications);
                try
                {
                    if (table.Colors[0] == LedStatusColor.Red)
                    {
                        notificationEntered.Set();
                        _ = releaseNotification.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    }
                }
                finally
                {
                    _ = Interlocked.Decrement(ref activeNotifications);
                }
            }, error =>
            {
                terminalOverlapped = Volatile.Read(ref activeNotifications) != 0;
                observed = error;
            }, () =>
            {
                terminalOverlapped = Volatile.Read(ref activeNotifications) != 0;
                completed = true;
            });
            Task inputEvent = Task.Factory.StartNew(
                () => this.stateChanges.OnNext(this.CreateChange("sensor.dimmer_led_1_color", "Red")),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task terminalEvent = Task.CompletedTask;
            try
            {
                _ = notificationEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                terminalEvent = Task.Factory.StartNew(() =>
                {
                    terminalEventStarted.SetResult(Thread.CurrentThread);
                    if (fail)
                    {
                        this.stateChanges.OnError(failure);
                    }
                    else
                    {
                        this.stateChanges.OnCompleted();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                await WaitForContentionAsync(terminalEvent, terminalEventStarted.Task);
            }
            finally
            {
                releaseNotification.Set();
                await Task.WhenAll(inputEvent, terminalEvent).WaitAsync(TimeSpan.FromSeconds(5));
            }

            _ = terminalOverlapped.Should().BeFalse();
            _ = completed.Should().Be(!fail);
            _ = observed.Should().BeSameAs(fail ? failure : null);
            _ = this.stateChanges.HasObservers.Should().BeFalse();
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ReentrantEventsAndTerminationWaitForTheCurrentNotification(bool fail)
        {
            List<LedInputTable> capture = [];
            InvalidOperationException failure = new("State stream failed");
            Exception? observed = null;
            bool completed = false;
            bool terminalOverlapped = false;
            int activeNotifications = 0;
            int maximumNotifications = 0;
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(table =>
            {
                ++activeNotifications;
                maximumNotifications = Math.Max(maximumNotifications, activeNotifications);
                try
                {
                    capture.Add(table);
                    if (table.Colors[0] == LedStatusColor.Red && table.Colors[6] == LedStatusColor.Off)
                    {
                        this.SetState("sensor.dimmer_led_7_color", "Green");
                        if (fail)
                        {
                            this.stateChanges.OnError(failure);
                        }
                        else
                        {
                            this.stateChanges.OnCompleted();
                        }
                    }
                }
                finally
                {
                    --activeNotifications;
                }
            }, error =>
            {
                terminalOverlapped = activeNotifications != 0;
                observed = error;
            }, () =>
            {
                terminalOverlapped = activeNotifications != 0;
                completed = true;
            });

            this.SetState("sensor.dimmer_led_1_color", "Red");

            _ = maximumNotifications.Should().Be(1);
            _ = terminalOverlapped.Should().BeFalse();
            _ = completed.Should().Be(!fail);
            _ = observed.Should().BeSameAs(fail ? failure : null);
            _ = capture.Should().HaveCount(3);
            _ = capture[1].Should().BeEquivalentTo(new LedInputTable().CreateWithColor(0, LedStatusColor.Red),
                options => options.WithStrictOrdering());
            _ = capture[2].Should().BeEquivalentTo(new LedInputTable()
                .CreateWithColor(0, LedStatusColor.Red).CreateWithColor(6, LedStatusColor.Green),
                options => options.WithStrictOrdering());
            _ = this.stateChanges.HasObservers.Should().BeFalse();
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SynchronousTerminationDoesNotReadCachedStatesOrPublishSnapshots(bool fail)
        {
            InvalidOperationException failure = new("State stream failed");
            _ = this.mockHaContext.Setup(m => m.StateAllChanges()).Returns(fail
                ? Observable.Throw<StateChange>(failure)
                : Observable.Empty<StateChange>());
            List<LedInputTable> capture = [];
            Exception? observed = null;
            bool completed = false;

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add,
                error => observed = error, () => completed = true);

            _ = capture.Should().BeEmpty();
            _ = completed.Should().Be(!fail);
            _ = observed.Should().BeSameAs(fail ? failure : null);
            this.mockHaContext.Verify(m => m.GetState(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TerminationDuringInitializationStopsReadingCachedStates(bool fail)
        {
            InvalidOperationException failure = new("State stream failed");
            _ = this.mockHaContext.Setup(m => m.GetState("sensor.dimmer_led_1_color")).Returns(() =>
            {
                if (fail)
                {
                    this.stateChanges.OnError(failure);
                }
                else
                {
                    this.stateChanges.OnCompleted();
                }
                return new EntityState { State = "Red" };
            });
            List<LedInputTable> capture = [];
            Exception? observed = null;
            bool completed = false;

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add,
                error => observed = error, () => completed = true);

            _ = capture.Should().BeEmpty();
            _ = completed.Should().Be(!fail);
            _ = observed.Should().BeSameAs(fail ? failure : null);
            this.mockHaContext.Verify(m => m.GetState(It.IsAny<string>()), Times.Once);
            _ = this.stateChanges.HasObservers.Should().BeFalse();
        }

        [TestMethod]
        public void TakingTheInitialSnapshotReleasesTheEventSubscription()
        {
            List<LedInputTable> capture = [];

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Take(1).Subscribe(capture.Add);

            _ = capture.Should().ContainSingle();
            _ = this.stateChanges.HasObservers.Should().BeFalse();
        }

        [TestMethod]
        public void FailedInitializationReleasesTheEventSubscription()
        {
            InvalidOperationException failure = new("Unable to read state");
            _ = this.mockHaContext.Setup(m => m.GetState(It.IsAny<string>())).Throws(failure);
            Exception? observed = null;

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(_ => { }, error => observed = error);

            _ = observed.Should().BeSameAs(failure);
            _ = this.stateChanges.HasObservers.Should().BeFalse();
        }

        /// <summary>
        /// Waits for a producer to block behind an active notification rather than relying on timing guesses.
        /// </summary>
        /// <param name="eventTask">Producer attempting to notify the monitor.</param>
        /// <param name="eventStarted">Thread running the producer.</param>
        /// <returns>A task that completes after contention has been verified.</returns>
        private static async Task WaitForContentionAsync(Task eventTask, Task<Thread> eventStarted)
        {
            Thread eventThread = await eventStarted.WaitAsync(TimeSpan.FromSeconds(5));
            _ = SpinWait.SpinUntil(() => eventTask.IsCompleted
                || (eventThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
            _ = eventTask.IsCompleted.Should().BeFalse("the producer must wait while the current notification is active");
        }

        /// <summary>
        /// Creates a monitor with the current test settings so subscription scenarios share one setup.
        /// </summary>
        /// <returns>A monitor connected to the test's state cache and event stream.</returns>
        private LedInputMonitor CreateMonitor()
        {
            return new LedInputMonitor(this.mockHaContext.Object, this.mockLogger.Object, this.mockAppConfig.Object);
        }

        /// <summary>
        /// Updates the cache and emits the matching change so tests reproduce Home Assistant's normal update flow.
        /// </summary>
        /// <param name="entityId">Entity whose cached value is changed.</param>
        /// <param name="state">New state, or null to remove the entity.</param>
        private void SetState(string entityId, string? state)
        {
            EntityState? oldState = this.states.GetValueOrDefault(entityId);
            if (state == null)
            {
                _ = this.states.Remove(entityId);
            }
            else
            {
                this.states[entityId] = new EntityState { EntityId = entityId, State = state };
            }
            this.stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, entityId), oldState,
                this.states.GetValueOrDefault(entityId)));
        }

        /// <summary>
        /// Builds an event without changing the cache so tests can control event ordering independently.
        /// </summary>
        /// <param name="entityId">Entity identified by the event.</param>
        /// <param name="state">New state, or null for a missing entity.</param>
        /// <returns>A state-change event with no previous state.</returns>
        private StateChange CreateChange(string entityId, string? state)
        {
            return new StateChange(new Entity(this.mockHaContext.Object, entityId), null,
                state == null ? null : new EntityState { EntityId = entityId, State = state });
        }
    }
}