using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using Ozy.HomeSeerDimmers.Apps.Dimmers;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace HomeSeerDimmerUnitTests
{
    /// <summary>
    /// Unit tests for <see cref="LedInputMonitor"/>
    /// </summary>
    [TestClass]
    public class LedInputMonitorTests
    {
        private readonly Mock<IHaContext> mockHaContext = new();
        private readonly Mock<ILogger<LedInputMonitor>> mockLogger = new();
        private readonly Mock<IAppConfig<Config>> mockAppConfig = new();
        private readonly Config config = new();

        [TestInitialize]
        public void Initialize()
        {
            this.mockHaContext.Reset();
            this.mockLogger.Reset();
            this.mockAppConfig.Reset();

            this.mockAppConfig.Setup(m => m.Value).Returns(this.config);
        }

        [TestMethod]
        public void CtorThrowsWhenConfigDimmerLedColorEntityNamePatternIsEmpty()
        {
            // Act & Assert
            Assert.ThrowsException<ArgumentException>(() => new LedInputMonitor(this.mockHaContext.Object, this.mockLogger.Object, this.mockAppConfig.Object));
        }

        [TestMethod]
        public void CtorThrowsWhenConfigDimmerLedBlinkEntityNamePatternIsEmpty()
        {
            // Arrange
            this.config.DimmerLedColorEntityNamePattern = "sensor.dimmer_led_{0}_color";

            // Act & Assert
            Assert.ThrowsException<ArgumentException>(() => new LedInputMonitor(this.mockHaContext.Object, this.mockLogger.Object, this.mockAppConfig.Object));
        }

        [TestMethod]
        public void CtorThrowsWhenConfigDimmerLedPatternsAreSame()
        {
            // Arrange
            this.config.DimmerLedColorEntityNamePattern = "sensor.dimmer_led_{0}_color";
            this.config.DimmerLedBlinkEntityNamePattern = "sensor.dimmer_led_{0}_color";

            // Act & Assert
            Assert.ThrowsException<ArgumentException>(() => new LedInputMonitor(this.mockHaContext.Object, this.mockLogger.Object, this.mockAppConfig.Object));
        }

        [TestMethod]
        public void CtorSucceedsAndSetsUpInitialValues()
        {
            // Arrange & Act
            List<LedInputTable> capture = this.TestAllInputTableChanges(stateChanges =>
            {
                // Don't emit anything since we are only testing ctor
            });

            // Assert
            capture.Should().BeEquivalentTo(new[] 
            { 
                new LedInputTable(Enumerable.Repeat(LedStatusColor.Off, 7).ToImmutableArray(), Enumerable.Repeat(LedBlink.Off, 7).ToImmutableArray())
            });
        }

        [TestMethod]
        public void AllInputTableChangesCapturesChangesInStatesOfMonitoredEntities()
        {
            // Arrange
            List<LedInputTable> capture = this.TestAllInputTableChanges(stateChanges =>
            {
                // Act
                // Emit new values
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"sensor.dimmer_led_6_color"), @old: null, @new: new EntityState { State = "Red" }));
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"binary_sensor.dimmer_led_6_blink"), @old: null, @new: new EntityState { State = "on" }));
            });

            // Assert
            capture.Should().HaveCount(3);
            capture.Last().Should().BeEquivalentTo(new LedInputTable(
                ImmutableArray.Create(new LedStatusColor[] { LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Red, LedStatusColor.Off }),
                ImmutableArray.Create(new[] { LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.On, LedBlink.Off })));
        }

        [TestMethod]
        public void AllInputTableChangesHandlesInvalidAndUnknownValuesByFallingBackToOff()
        {
            // Arrange
            List<LedInputTable> capture = this.TestAllInputTableChanges(stateChanges =>
            {
                // Emit good values
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"sensor.dimmer_led_0_color"), @old: null, @new: new EntityState { State = "Red" }));
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"binary_sensor.dimmer_led_0_blink"), @old: null, @new: new EntityState { State = "on" }));

                // Act
                // Emit unknown values
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"sensor.dimmer_led_0_color"), @old: null, @new: new EntityState { State = "unavailable" }));
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"binary_sensor.dimmer_led_0_blink"), @old: null, @new: new EntityState { State = "unavailable" }));

                // Emit good values
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"sensor.dimmer_led_1_color"), @old: null, @new: new EntityState { State = "Red" }));
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"binary_sensor.dimmer_led_1_blink"), @old: null, @new: new EntityState { State = "on" }));

                // Emit bad values
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"sensor.dimmer_led_1_color"), @old: null, @new: new EntityState { State = "Some garbage" }));
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"binary_sensor.dimmer_led_1_blink"), @old: null, @new: new EntityState { State = "Other garbage" }));
            });

            // Assert
            capture.Should().HaveCount(5);
            capture.Last().Should().BeEquivalentTo(new LedInputTable(
                ImmutableArray.Create(new LedStatusColor[] { LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off, LedStatusColor.Off }),
                ImmutableArray.Create(new[] { LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.Off, LedBlink.Off })));
        }

        private List<LedInputTable> TestAllInputTableChanges(Action<IObserver<StateChange>> doEmits)
        {
            this.config.DimmerLedColorEntityNamePattern = "sensor.dimmer_led_{0}_color";
            this.config.DimmerLedBlinkEntityNamePattern = "binary_sensor.dimmer_led_{0}_blink";

            Subject<StateChange> stateChanges = new();
            this.mockHaContext.Setup(m => m.StateAllChanges()).Returns(stateChanges);

            // Setup initial values
            for (int i = 0; i < 7; ++i)
            {
                this.mockHaContext.Setup(m => m.GetState($"sensor.dimmer_led_{i}_color")).Returns(new EntityState());
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"sensor.dimmer_led_{i}_color"), @old: null, @new: new EntityState { State = "off" }));

                this.mockHaContext.Setup(m => m.GetState($"binary_sensor.dimmer_led_{i}_blink")).Returns(new EntityState());
                stateChanges.OnNext(new StateChange(new Entity(this.mockHaContext.Object, $"binary_sensor.dimmer_led_{i}_blink"), @old: null, @new: new EntityState { State = "off" }));
            }

            LedInputMonitor monitor = new(this.mockHaContext.Object, this.mockLogger.Object, this.mockAppConfig.Object);

            List<LedInputTable> capture = new();
            using (monitor.AllInputTableChanges.Subscribe(t => capture.Add(t)))
            {
                doEmits(stateChanges);
            }

            return capture;
        }
    }
}
