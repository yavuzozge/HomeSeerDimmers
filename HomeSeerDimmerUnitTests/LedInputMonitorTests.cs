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
            this.mockAppConfig.Setup(m => m.Value).Returns(this.config);
            this.mockHaContext.Setup(m => m.StateAllChanges()).Returns(this.stateChanges);
            this.mockHaContext.Setup(m => m.GetState(It.IsAny<string>()))
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

            Assert.Throws<ArgumentException>(() => this.CreateMonitor());
        }

        [TestMethod]
        public void SubscriptionReadsInitialValuesIncludingTheFirstAndLastLeds()
        {
            this.states["sensor.dimmer_led_1_color"] = new EntityState { State = "Red" };
            this.states["sensor.dimmer_led_7_color"] = new EntityState { State = "Green" };
            this.states["binary_sensor.dimmer_led_7_blink"] = new EntityState { State = "on" };
            LedInputMonitor monitor = this.CreateMonitor();
            this.stateChanges.HasObservers.Should().BeFalse();
            List<LedInputTable> capture = [];

            using IDisposable subscription = monitor.AllInputTableChanges.Subscribe(capture.Add);

            capture.Should().ContainSingle().Which.Should().BeEquivalentTo(new LedInputTable()
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

            capture.Should().HaveCount(2);
            capture.Should().OnlyContain(table => table.Colors[0] == LedStatusColor.Green);
            capture.Last().Blinks[1].Should().Be(LedBlink.On);
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

            first.Should().HaveCount(2);
            second.Should().ContainSingle().Which.Colors[0].Should().Be(LedStatusColor.Red);
            this.SetState("binary_sensor.dimmer_led_7_blink", "on");
            first.Last().Should().BeEquivalentTo(second.Last(), options => options.WithStrictOrdering());
            first.Should().HaveCount(3);
            second.Should().HaveCount(2);
        }

        [TestMethod]
        public void ResubscriptionReconcilesChangesMadeWhileThereWereNoObservers()
        {
            LedInputMonitor monitor = this.CreateMonitor();
            using (monitor.AllInputTableChanges.Subscribe(_ => { }))
            {
                this.SetState("sensor.dimmer_led_1_color", "Red");
            }
            this.stateChanges.HasObservers.Should().BeFalse();
            this.SetState("sensor.dimmer_led_1_color", "Blue");
            List<LedInputTable> capture = [];

            using IDisposable subscription = monitor.AllInputTableChanges.Subscribe(capture.Add);

            capture.Should().ContainSingle().Which.Colors[0].Should().Be(LedStatusColor.Blue);
        }

        [TestMethod]
        public void AnEventDuringInitializationTakesPrecedenceOverAnOlderCachedState()
        {
            this.mockHaContext.Setup(m => m.GetState("sensor.dimmer_led_1_color")).Returns(() =>
            {
                this.SetState("sensor.dimmer_led_1_color", "Red");
                return new EntityState { State = "Off" };
            });
            List<LedInputTable> capture = [];

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            capture.Should().ContainSingle().Which.Colors[0].Should().Be(LedStatusColor.Red);
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

            latest!.Colors[6].Should().Be(expected);
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
            capture.Last().Colors[0].Should().Be(LedStatusColor.Red);

            this.SetState("sensor.dimmer_led_1_color", state);

            capture.Should().HaveCount(3);
            capture.Last().Colors[0].Should().Be(LedStatusColor.Off);
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
            capture.Last().Blinks[6].Should().Be(LedBlink.On);

            this.SetState("binary_sensor.dimmer_led_7_blink", state);

            capture.Should().HaveCount(3);
            capture.Last().Blinks[6].Should().Be(LedBlink.Off);
        }

        [TestMethod]
        public void MissingAndRemovedInputsAreOff()
        {
            this.states.Remove("sensor.dimmer_led_7_color");
            LedInputTable? latest = null;
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(table => latest = table);
            latest!.Colors[6].Should().Be(LedStatusColor.Off);
            this.SetState("sensor.dimmer_led_7_color", "Red");
            this.SetState("binary_sensor.dimmer_led_7_blink", "on");

            this.SetState("sensor.dimmer_led_7_color", null);
            this.SetState("binary_sensor.dimmer_led_7_blink", null);

            latest.Should().BeEquivalentTo(new LedInputTable(), options => options.WithStrictOrdering());
        }

        [TestMethod]
        public void UnmonitoredEntitiesDoNotProduceUpdates()
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            this.SetState("sensor.dimmer_led_0_color", "Red");
            this.SetState("binary_sensor.dimmer_led_8_blink", "on");
            this.SetState("sensor.unrelated", "Green");

            capture.Should().ContainSingle();
        }

        [TestMethod]
        public void AttributeOnlyChangesDoNotProduceLedUpdates()
        {
            List<LedInputTable> capture = [];
            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(capture.Add);

            this.stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, "sensor.dimmer_led_1_color"),
                new EntityState { State = "Off" }, new EntityState { State = "Off" }));

            capture.Should().ContainSingle();
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
                    Interlocked.Increment(ref overlappingNotifications);
                }
                try
                {
                    capture.Enqueue(table);
                    if (table.Colors[0] == LedStatusColor.Red && table.Colors[6] == LedStatusColor.Off)
                    {
                        firstNotificationEntered.Set();
                        releaseFirstNotification.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeNotifications);
                }
            });
            Task firstEvent = Task.Factory.StartNew(
                () => this.stateChanges.OnNext(this.CreateChange("sensor.dimmer_led_1_color", "Red")),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task secondEvent = Task.CompletedTask;
            try
            {
                firstNotificationEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                secondEvent = Task.Factory.StartNew(() =>
                {
                    secondEventStarted.SetResult(Thread.CurrentThread);
                    this.stateChanges.OnNext(this.CreateChange("sensor.dimmer_led_7_color", "Green"));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Thread secondThread = await secondEventStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Wait for actual contention rather than guessing how long the second producer needs to run.
                SpinWait.SpinUntil(() => secondEvent.IsCompleted
                    || (secondThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                secondEvent.IsCompleted.Should().BeFalse("the second event must wait while the first notification is active");
                overlappingNotifications.Should().Be(0);
            }
            finally
            {
                releaseFirstNotification.Set();
                await Task.WhenAll(firstEvent, secondEvent).WaitAsync(TimeSpan.FromSeconds(5));
            }

            capture.Should().HaveCount(3);
            capture.Last().Should().BeEquivalentTo(new LedInputTable()
                .CreateWithColor(0, LedStatusColor.Red).CreateWithColor(6, LedStatusColor.Green), options => options.WithStrictOrdering());
            overlappingNotifications.Should().Be(0);
        }

        [TestMethod]
        public void FailedInitializationReleasesTheEventSubscription()
        {
            InvalidOperationException failure = new("Unable to read state");
            this.mockHaContext.Setup(m => m.GetState(It.IsAny<string>())).Throws(failure);
            Exception? observed = null;

            using IDisposable subscription = this.CreateMonitor().AllInputTableChanges.Subscribe(_ => { }, error => observed = error);

            observed.Should().BeSameAs(failure);
            this.stateChanges.HasObservers.Should().BeFalse();
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
                this.states.Remove(entityId);
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