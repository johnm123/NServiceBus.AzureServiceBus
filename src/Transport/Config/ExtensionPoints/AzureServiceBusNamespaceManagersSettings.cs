﻿namespace NServiceBus
{
    using System;
    using Configuration.AdvancedExtensibility;
    using Microsoft.ServiceBus;
    using Settings;
    using Transport.AzureServiceBus;

    /// <summary>
    /// Namespace managers configuration settings.
    /// </summary>
    public class AzureServiceBusNamespaceManagersSettings : ExposeSettings
    {
        internal AzureServiceBusNamespaceManagersSettings(SettingsHolder settings) : base(settings)
        {
            this.settings = settings;
        }

        /// <summary>
        /// Customize <see cref="NamespaceManager" /> creation.
        /// </summary>
        public AzureServiceBusNamespaceManagersSettings NamespaceManagerSettingsFactory(Func<string, NamespaceManagerSettings> factory)
        {
            settings.Set(WellKnownConfigurationKeys.Connectivity.NamespaceManagers.NamespaceManagerSettingsFactory, factory);

            return this;
        }

        /// <summary>
        /// Customize the token provider.
        /// </summary>
        public AzureServiceBusNamespaceManagersSettings TokenProvider(Func<string, TokenProvider> factory)
        {
            settings.Set(WellKnownConfigurationKeys.Connectivity.NamespaceManagers.TokenProviderFactory, factory);

            return this;
        }

        /// <summary>
        /// Retry policy configured on Namespace Manager level.
        /// <remarks>Default is RetryPolicy.Default</remarks>
        /// </summary>
        public AzureServiceBusNamespaceManagersSettings RetryPolicy(RetryPolicy retryPolicy)
        {
            settings.Set(WellKnownConfigurationKeys.Connectivity.NamespaceManagers.RetryPolicy, retryPolicy);

            return this;
        }

        SettingsHolder settings;
    }
}