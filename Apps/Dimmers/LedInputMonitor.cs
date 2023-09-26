using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;

namespace Ozy.HomeSeerDimmers.Apps.Dimmers
{
    /// <summary>
    /// Monitors state changes of "input" entitites (the entities that are used in Home Assistant 
    /// to represent what a LED color and blink should be)
    /// </summary>
    public class LedInputMonitor : ILedInputMonitor
    {
        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="ha">Home assistant context</param>
        /// <param name="logger">Logger</param>
        /// <param name="appConfig">App configuration</param>
        public LedInputMonitor(IHaContext ha, ILogger<LedInputMonitor> logger, IAppConfig<Config> appConfig)
        {
            ArgumentNullException.ThrowIfNull(ha);
            ArgumentNullException.ThrowIfNull(logger);

            LedInputTable table = new();

            // Create entities and a lookup table that would update the table above
            Dictionary<Entity, (int Index, Action<int, EntityState> UpdateCallback)> entityInputUpdateMap = new();
            for (int i = 0; i < LedInputTable.NumberOfLeds; ++i)
            {
                string entityName = string.Format(appConfig.Value.DimmerLedColorEntityNamePattern, i + 1);
                entityInputUpdateMap.Add(new Entity(ha, entityName), (i, (idx, es) => table = table.CreateWithColor(idx, GetLedConfigStatusColor(es))));
                logger.LogInformation("Monitoring for color: {Name}", entityName);

                entityName = string.Format(appConfig.Value.DimmerLedBlinkEntityNamePattern, i + 1);
                entityInputUpdateMap.Add(new Entity(ha, entityName), (i, (idx, es) => table = table.CreateWithBlink(idx, GetLedConfigBlink(es))));
                logger.LogInformation("Monitored for blink: {Name}", entityName);
            };

            // Get initial values from entities
            foreach (KeyValuePair<Entity, (int Index, Action<int, EntityState> UpdateCallback)> kvp in entityInputUpdateMap)
            {
                EntityState? state = kvp.Key.EntityState;
                if (state != null)
                {
                    kvp.Value.UpdateCallback(kvp.Value.Index, state);
                }
            }

            // Always return the latest value on subscribe
            IObservable<LedInputTable> latest = Observable.Return(table);

            IObservable<LedInputTable> followUps = Observable
                .Merge(entityInputUpdateMap.Keys.Select(entity => entity.StateChanges()))
                .Select(sc =>
                {
                    logger.LogInformation("State change for {EntityId}, new state:{State}", sc?.Entity?.EntityId, sc?.New?.State);

                    if (sc?.New != null && entityInputUpdateMap.TryGetValue(sc.Entity, out (int Index, Action<int, EntityState> UpdateCallback) r))
                    {
                        r.UpdateCallback(r.Index, sc?.New!);
                    }
                    else
                    {
                        logger.LogWarning("Entity not found: {EntityId}", sc?.Entity?.EntityId);
                    }

                    return table;
                });

            // Set the observable
            AllInputTableChanges = latest.Merge(followUps);
        }

        /// <inheritdoc />
        public IObservable<LedInputTable> AllInputTableChanges { get; }

        /// <summary>
        /// Helper that parses the entity state to a LED status color
        /// </summary>
        /// <param name="state">Entity state</param>
        /// <returns>Result</returns>
        private static LedStatusColor GetLedConfigStatusColor(EntityState state)
        {
            return Enum.Parse<LedStatusColor>(state.State ?? "off", ignoreCase: true);
        }

        /// <summary>
        /// Helper that parses the entity state to a LED blink
        /// </summary>
        /// <param name="state">Entity state</param>
        /// <returns>Result</returns>
        private static LedBlink GetLedConfigBlink(EntityState state)
        {
            return string.Equals(state.State, "off", StringComparison.InvariantCultureIgnoreCase) ? LedBlink.Off : LedBlink.On;
        }
    };
}
