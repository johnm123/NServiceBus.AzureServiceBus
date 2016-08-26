﻿namespace NServiceBus.Transport.AzureServiceBus
{
    using System;
    using System.Linq;
    using Settings;

    class DefaultConnectionStringToNamespaceAliasMapper : ICanMapConnectionStringToNamespaceAlias
    {
        ReadOnlySettings settings;

        public DefaultConnectionStringToNamespaceAliasMapper(ReadOnlySettings settings)
        {
            this.settings = settings;
        }

        public EntityAddress Map(EntityAddress value)
        {
            if (!value.HasConnectionString)
            {
                return value;
            }

            var namespaces = settings.Get<NamespaceConfigurations>(WellKnownConfigurationKeys.Topology.Addressing.Namespaces);

            var namespaceInfo = namespaces.SingleOrDefault(x => x.ConnectionString == value.Suffix);
            if (namespaceInfo != null)
            {
                return new EntityAddress($"{value.Name}@{namespaceInfo.Alias}");
            }

            var namespaceName = new ConnectionString(value.Suffix).NamespaceName;
            throw new InvalidOperationException($"Connection string for {namespaceName} hasn't been configured. {Environment.NewLine}" +
                                                "Use `AddNamespace` configuration API to map connection string to namespace alias.");
        }
    }
}