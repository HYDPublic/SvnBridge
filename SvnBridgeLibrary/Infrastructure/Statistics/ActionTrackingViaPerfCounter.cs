using System;
using System.Collections.Generic;
using System.Diagnostics;
using SvnBridge.Handlers;

namespace SvnBridge.Infrastructure.Statistics
{
    public class ActionTrackingViaPerfCounter
    {
        private static readonly IDictionary<Type, PerformanceCounter> performanceCounters =
            new Dictionary<Type, PerformanceCounter>();

        public static bool Enabled
        {
            get { return enabled; }
        }

        private static bool enabled = true;

        public static void CreatePerfCounters()
        {
            CreatePerfCounters(GatherAllCounters());
        }

        static ActionTrackingViaPerfCounter()
        {
            var handlers = GatherAllCounters();

            TryCreatePerfCounters(handlers);
            if (enabled)
            {
                foreach (Type type in handlers)
                {
                    string handlerName = type.Name.Replace("Handler", "");
                    performanceCounters[type] = new PerformanceCounter("SvnBridge", handlerName, false);
                }
            }
        }

        private static List<Type> GatherAllCounters()
        {
            var types = typeof(ActionTrackingViaPerfCounter).Assembly.GetTypes();
            var handlers = new List<Type>(types.Length + 1);
            foreach (Type type in types)
            {
                if (typeof(RequestHandlerBase).IsAssignableFrom(type) == false
                    || type.IsAbstract)
                    continue;
                handlers.Add(type);
            }
            handlers.Add(typeof(Errors));
            return handlers;
        }

        private static void TryCreatePerfCounters(List<Type> handlers)
        {
            try
            {
                CreatePerfCounters(handlers);
            }
            catch (Exception e)
            {
                enabled = false;
                if (!Configuration.PerfCountersMandatory)
                    return;
                throw new InvalidOperationException("Could not create performance counters for SvnBridge. Please run the SvnBridge.PerfCounter.Installer.exe program to install them." + Environment.NewLine +
                    "You can also make them optional by turning off the 'PerfCountersAreMandatory' setting in the application configuration file.", e);
            }
        }

        private static void CreatePerfCounters(List<Type> handlers)
        {
            if (PerformanceCounterCategory.Exists("SvnBridge") == false)
            {
                var creationDataCollection = new CounterCreationDataCollection();
                foreach (Type type in handlers)
                {
                    string handlerName = type.Name.Replace("Handler", "");
                    var item = new CounterCreationData(handlerName, "Track the number of " + handlerName,
                                                       PerformanceCounterType.NumberOfItems64);

                    creationDataCollection.Add(item);
                }
                PerformanceCounterCategory.Create("SvnBridge", "Performance counters for Svn Bridge",
                                                  PerformanceCounterCategoryType.SingleInstance, creationDataCollection);
            }
        }

        public virtual void Request(RequestHandlerBase handler)
        {
            if (!enabled)
                return;
            performanceCounters[handler.GetType()].Increment();
        }

        public virtual void Error()
        {
            if (!enabled)
                return;
            performanceCounters[typeof(Errors)].Increment();
        }

        public virtual IDictionary<string, long> GetStatistics()
        {
            Dictionary<string, long> stats = new Dictionary<string, long>(performanceCounters.Values.Count);
            foreach (var counter in performanceCounters.Values)
            {
                stats[counter.CounterName] = counter.RawValue;
            }
            return stats;
        }

        #region Nested type: Errors

        private class Errors
        {
        }

        #endregion
    }
}
