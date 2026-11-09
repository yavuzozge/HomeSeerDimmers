using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using Ozy.HomeSeerDimmers.Apps.Dimmers.HomeSeerDevice;
using System;
using System.Collections.Generic;
using System.Reactive.Linq;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Monitors the Home Assistant entities that represent LED colors and blink states.
    /// </summary>
    public class LedInputMonitor : ILedInputMonitor
    {
        /// <summary>
        /// logger
        /// </summary>
        private readonly ILogger logger;

        /// <summary>
        /// Creates the input monitor without subscribing to Home Assistant events.
        /// </summary>
        /// <param name="ha">Home assistant context</param>
        /// <param name="logger">Logger</param>
        /// <param name="appConfig">App configuration</param>
        public LedInputMonitor(
            IHaContext ha,
            ILogger<LedInputMonitor> logger,
            IAppConfig<Config> appConfig)
        {
            ArgumentNullException.ThrowIfNull(ha);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(appConfig);

            this.logger = logger;

            logger.LogInformation("Setting up LED input monitoring table");

            if (string.IsNullOrWhiteSpace(appConfig.Value.DimmerLedColorEntityNamePattern))
            {
                throw new ArgumentException($"{nameof(Config.DimmerLedColorEntityNamePattern)} cannot be empty or whitespace", nameof(appConfig));
            }
            if (string.IsNullOrWhiteSpace(appConfig.Value.DimmerLedBlinkEntityNamePattern))
            {
                throw new ArgumentException($"{nameof(Config.DimmerLedBlinkEntityNamePattern)} cannot be empty or whitespace", nameof(appConfig));
            }
            if (string.Equals(appConfig.Value.DimmerLedColorEntityNamePattern, appConfig.Value.DimmerLedBlinkEntityNamePattern, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{nameof(Config.DimmerLedBlinkEntityNamePattern)} should have a different value than {nameof(Config.DimmerLedColorEntityNamePattern)}", nameof(appConfig));
            }

            Dictionary<Entity, (int Index, bool IsColor)> entityInputUpdateMap = [];
            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                string entityName = string.Format(appConfig.Value.DimmerLedColorEntityNamePattern, i + 1);
                entityInputUpdateMap.Add(new Entity(ha, entityName), (i, true));
                logger.LogInformation("Monitoring for color: {Name}", entityName);

                entityName = string.Format(appConfig.Value.DimmerLedBlinkEntityNamePattern, i + 1);
                entityInputUpdateMap.Add(new Entity(ha, entityName), (i, false));
                logger.LogInformation("Monitoring for blink: {Name}", entityName);
            }

            AllInputTableChanges = Observable.Create<LedInputTable>(observer =>
            {
                object inputStateLock = new();
                LedInputTable table = new();
                HashSet<Entity> updatedInputs = [];
                bool initialized = false;

                IDisposable subscription = ha.StateAllChanges().Subscribe(change =>
                {
                    if (!entityInputUpdateMap.TryGetValue(change.Entity, out (int Index, bool IsColor) input)
                        || string.Equals(change.Old?.State, change.New?.State, StringComparison.Ordinal))
                    {
                        return;
                    }

                    lock (inputStateLock)
                    {
                        logger.LogInformation("State change for {EntityId}, new state: {State}", change.Entity.EntityId, change.New?.State);
                        table = this.UpdateInputTable(table, input.Index, input.IsColor, change.New);
                        if (initialized)
                        {
                            observer.OnNext(table);
                        }
                        else
                        {
                            updatedInputs.Add(change.Entity);
                        }
                    }
                }, observer.OnError, observer.OnCompleted);

                try
                {
                    lock (inputStateLock)
                    {
                        foreach (KeyValuePair<Entity, (int Index, bool IsColor)> input in entityInputUpdateMap)
                        {
                            EntityState? state = input.Key.EntityState;
                            // An event received during initialization takes precedence over a cached state.
                            if (!updatedInputs.Contains(input.Key))
                            {
                                table = this.UpdateInputTable(table, input.Value.Index, input.Value.IsColor, state);
                            }
                        }

                        initialized = true;
                        observer.OnNext(table);
                    }
                }
                catch
                {
                    subscription.Dispose();
                    throw;
                }

                return subscription;
            });
        }

        /// <inheritdoc />
        public IObservable<LedInputTable> AllInputTableChanges { get; }

        /// <summary>
        /// Copies the table with one updated input so published snapshots remain unchanged.
        /// </summary>
        /// <param name="table">Current input table.</param>
        /// <param name="index">Zero-based LED index.</param>
        /// <param name="isColor">Whether the input represents a color rather than a blink state.</param>
        /// <param name="state">Current entity state, or null when the entity is missing.</param>
        /// <returns>The updated input table.</returns>
        private LedInputTable UpdateInputTable(LedInputTable table, int index, bool isColor, EntityState? state)
        {
            return isColor
                ? table.CreateWithColor(index, this.GetLedConfigStatusColor(state))
                : table.CreateWithBlink(index, GetLedConfigBlink(state));
        }

        /// <summary>
        /// Accepts supported color names and uses off for missing or invalid states so unsafe parameter values are not sent.
        /// </summary>
        /// <param name="state">Entity state</param>
        /// <returns>Result</returns>
        private LedStatusColor GetLedConfigStatusColor(EntityState? state)
        {
            if (state?.State == null || string.Equals(state.State, "unavailable", StringComparison.OrdinalIgnoreCase))
            {
                return LedStatusColor.Off;
            }

            string colorName = state.State.Trim();
            if (Enum.TryParse(colorName, ignoreCase: true, out LedStatusColor color)
                && Enum.IsDefined(color)
                && string.Equals(Enum.GetName(color), colorName, StringComparison.OrdinalIgnoreCase))
            {
                return color;
            }

            this.logger.LogWarning("Failed to parse LED status color entity {EntityId} with state {State}: ", state.EntityId, state.State);

            return LedStatusColor.Off;
        }

        /// <summary>
        /// Converts Home Assistant's on state to blinking and all other states to off so unavailable inputs do not blink.
        /// </summary>
        /// <param name="state">Entity state</param>
        /// <returns>Result</returns>
        private static LedBlink GetLedConfigBlink(EntityState? state)
        {
            return string.Equals(state?.State, "on", StringComparison.OrdinalIgnoreCase) ? LedBlink.On : LedBlink.Off;
        }
    }
}