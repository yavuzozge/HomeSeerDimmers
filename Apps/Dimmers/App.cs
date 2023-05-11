using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Integration;
using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// The app class
    /// </summary>
    [NetDaemonApp]
    public class App
    {
        private readonly IHaContext ha;
        private readonly ILogger<App> logger;
        private readonly ILedInputMonitor ledInputMonitor;
        private readonly IDimmerSyncManager dimmerDeviceManager;
        private readonly IZWavePingManager zwavePingManager;

        /// <summary>
        /// Used to serialize asynchronous task executions so that we can make sure that 
        /// the calls to Z-Wave devices are done one operation group at a time
        /// </summary>
        private readonly Subject<Func<Task>> zwaveOprationsSerialExecutor = new();

        /// <summary>
        /// Subject that connects to LED input monitor to receive input table updates
        /// as well as allowing the replay of the last input update for LED syncs
        /// </summary>
        private readonly Subject<LedInputTable> inputTableSubject = new();

        /// <summary>
        /// Last LED input table that will be used for LED syncs
        /// </summary>
        private LedInputTable lastInputTable = LedInputTable.CreateEmpty();

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="ha"></param>
        /// <param name="scheduler"></param>
        /// <param name="logger"></param>
        /// <param name="ledInputMonitor"></param>
        /// <param name="dimmerDeviceManager"></param>
        /// <param name="zwavePingManager"></param>
        /// <param name="appConfig"></param>
        public App(
            IHaContext ha,
            IScheduler scheduler,
            ILogger<App> logger,
            ILedInputMonitor ledInputMonitor,
            IDimmerSyncManager dimmerDeviceManager,
            IZWavePingManager zwavePingManager,
            IAppConfig<Config> appConfig)
        {
            ArgumentNullException.ThrowIfNull(ha, nameof(ha));
            ArgumentNullException.ThrowIfNull(scheduler, nameof(scheduler));
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentNullException.ThrowIfNull(ledInputMonitor, nameof(ledInputMonitor));
            ArgumentNullException.ThrowIfNull(dimmerDeviceManager, nameof(dimmerDeviceManager));
            ArgumentNullException.ThrowIfNull(zwavePingManager, nameof(zwavePingManager));
            ArgumentNullException.ThrowIfNull(appConfig, nameof(appConfig));

            this.ha = ha;
            this.logger = logger;
            this.ledInputMonitor = ledInputMonitor;
            this.dimmerDeviceManager = dimmerDeviceManager;
            this.zwavePingManager = zwavePingManager;

            _ = this.zwaveOprationsSerialExecutor.SubscribeAsync(cb => cb(), this.logger);

            _ = this.inputTableSubject.Subscribe(this.ScheduleLedSync);

            _ = this.ledInputMonitor.AllInputTableChanges.Subscribe(this.inputTableSubject);

            if (appConfig.Value.LedSyncInterval > TimeSpan.Zero)
            {
                logger.LogInformation("Scheduling periodic LED sync with interval: {Interval}", appConfig.Value.LedSyncInterval);
                _ = scheduler.SchedulePeriodic(appConfig.Value.LedSyncInterval, this.ScheduleLedReSync);
            }

            if (appConfig.Value.ZWavePingInterval > TimeSpan.Zero)
            {
                logger.LogInformation("Scheduling periodic ZWave pings with interval: {Interval}", appConfig.Value.ZWavePingInterval);
                _ = scheduler.SchedulePeriodic(appConfig.Value.ZWavePingInterval, this.ScheduleZWavePing);
            }

            this.ha.RegisterServiceCallBack<SynchronizeDimmersData>("synchronize_dimmers", _ => this.ScheduleLedReSync());
        }

        /// <summary>
        /// Helper method that schedules a Z-Wave related task to be executed serially
        /// </summary>
        /// <param name="createTask">Delegate that creates the Z-Wave operation task</param>
        private void ScheduleZwaveOperation(Func<Task> createTask)
        {
            this.zwaveOprationsSerialExecutor.OnNext(createTask);
        }

        /// <summary>
        /// Sets the last input table and schedules all the LEDs to be synced
        /// </summary>
        private void ScheduleLedSync(LedInputTable inputTable)
        {
            this.lastInputTable = inputTable;
            this.ScheduleZwaveOperation(() => this.dimmerDeviceManager.SyncDimmersAsync(this.lastInputTable, CancellationToken.None));
        }

        /// <summary>
        /// Schedules all the LEDs to be resynced using the last input
        /// This is primarily to support to recover from the cases when the Z-Wave updates were failed
        /// (due to ZWAve network congestion ...etc.)
        /// </summary>
        private void ScheduleLedReSync()
        {
            this.ScheduleZwaveOperation(() => this.dimmerDeviceManager.SyncDimmersAsync(this.lastInputTable, CancellationToken.None));
        }

        /// <summary>
        /// Schedules a ping to Z-Wave devices, set in Z-Wave ping manager
        /// </summary>
        private void ScheduleZWavePing()
        {
            this.ScheduleZwaveOperation(() => this.zwavePingManager.PingDevicesAsync(CancellationToken.None));
        }
    }
}