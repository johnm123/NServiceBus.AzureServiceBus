namespace NServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Linq;
    using Settings;
    using Transport.AzureServiceBus;

    /// <summary>
    /// Strategy utilizing multiple namespaces in a Round-Robin manner. 
    /// <remarks>Assumes all entities are found in all namespaces.</remarks>
    /// </summary>
    public class RoundRobinNamespacePartitioning : INamespacePartitioningStrategy
    {
        internal RoundRobinNamespacePartitioning(ReadOnlySettings settings)
        {
            if (!settings.TryGet<NamespaceConfigurations>(WellKnownConfigurationKeys.Topology.Addressing.Namespaces, out var namespaces))
            {
                throw new ConfigurationErrorsException($"The '{nameof(RoundRobinNamespacePartitioning)}' strategy requires more than one namespace, please use {nameof(AzureServiceBusTransportExtensions.NamespacePartitioning)}().{nameof(AzureServiceBusNamespacePartitioningSettings.AddNamespace)}() to register multiple namespaces");
            }

            namespaces = new NamespaceConfigurations(namespaces.Where(n => n.Purpose == NamespacePurpose.Partitioning).ToList());

            if (namespaces.Count <= 1)
            {
                throw new ConfigurationErrorsException($"The '{nameof(RoundRobinNamespacePartitioning)}' strategy requires more than one namespace for the purpose of partitioning, found {namespaces.Count}. , please use {nameof(AzureServiceBusTransportExtensions.NamespacePartitioning)}().{nameof(AzureServiceBusNamespacePartitioningSettings.AddNamespace)}() to register additional namespaces");
            }

            this.namespaces = new CircularBuffer<NamespaceInfo>(namespaces.Count);
            Array.ForEach(namespaces.ToArray(), x => this.namespaces.Put(x));

            SendingNamespacesCanBeCached = false;
        }

        /// <summary>
        /// Gets whether the information returned by the strategy for <see cref="PartitioningIntent.Sending"/> is cache-able.
        /// </summary>
        public bool SendingNamespacesCanBeCached { get; }

        /// <summary>
        /// Return a set of namespaces required by strategy for <see cref="PartitioningIntent"/>.
        /// </summary>
        public IEnumerable<RuntimeNamespaceInfo> GetNamespaces(PartitioningIntent partitioningIntent)
        {
            if (partitioningIntent == PartitioningIntent.Sending)
            {
                var @namespace = namespaces.Get();
                yield return new RuntimeNamespaceInfo(@namespace.Alias, @namespace.Connection, @namespace.Purpose, NamespaceMode.Active);
            }

            if (partitioningIntent == PartitioningIntent.Receiving || partitioningIntent == PartitioningIntent.Creating)
            {
                var mode = NamespaceMode.Active;
                for (var i = 0; i < namespaces.Size; i++)
                {
                    var @namespace = namespaces.Get();
                    yield return new RuntimeNamespaceInfo(@namespace.Alias, @namespace.Connection, @namespace.Purpose, mode);
                    mode = NamespaceMode.Passive;
                }
            }
        }

        CircularBuffer<NamespaceInfo> namespaces;
    }
}