namespace NServiceBus.Transport.AzureServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Transactions;
    using Logging;
    using Microsoft.ServiceBus.Messaging;
    using Settings;

    class OutgoingBatchRouter : IRouteOutgoingBatchesInternal
    {
        public OutgoingBatchRouter(BatchedOperationsToBrokeredMessagesConverter outgoingMessageConverter, MessageSenderLifeCycleManager sendersLifeCycleManager, ReadOnlySettings settings, IHandleOversizedBrokeredMessages oversizedMessageHandler)
        {
            this.outgoingMessageConverter = outgoingMessageConverter;
            this.sendersLifeCycleManager = sendersLifeCycleManager;
            this.oversizedMessageHandler = oversizedMessageHandler;

            sendViaFromConfiguration = settings.Get<bool>(WellKnownConfigurationKeys.Connectivity.SendViaReceiveQueue);
            transportTypeIsNetMessaging = settings.Get<TransportType>(WellKnownConfigurationKeys.Connectivity.TransportType) == TransportType.NetMessaging;

            backOffTimeOnThrottle = settings.Get<TimeSpan>(WellKnownConfigurationKeys.Connectivity.MessageSenders.BackOffTimeOnThrottle);
            maxRetryAttemptsOnThrottle = settings.Get<int>(WellKnownConfigurationKeys.Connectivity.MessageSenders.RetryAttemptsOnThrottle);
            maximuMessageSizeInKilobytes = settings.Get<int>(WellKnownConfigurationKeys.Connectivity.MessageSenders.MaximumMessageSizeInKilobytes);
        }

        public Task RouteBatches(IEnumerable<BatchInternal> outgoingBatches, ReceiveContextInternal context, DispatchConsistency consistency)
        {
            var pendingBatches = new List<Task>();
            foreach (var batch in outgoingBatches)
            {
                pendingBatches.Add(RouteBatch(batch, context as BrokeredMessageReceiveContextInternal, consistency));
            }
            return Task.WhenAll(pendingBatches);
        }

        internal async Task RouteBatch(BatchInternal batch, BrokeredMessageReceiveContextInternal context, DispatchConsistency consistency)
        {
            var outgoingBatches = batch.Operations;

            var passiveNamespaces = batch.Destinations.Namespaces.Where(n => n.Mode == NamespaceMode.Passive).ToList();
            var pendingSends = new List<Task>();

            foreach (var entity in batch.Destinations.Entities.Where(entity => entity.Namespace.Mode == NamespaceMode.Active))
            {
                var routingOptions = GetRoutingOptions(context, consistency);

                if (routingOptions.SendVia && !string.IsNullOrEmpty(routingOptions.ViaEntityPath))
                {
                    logger.DebugFormat("Routing {0} messages to {1} via {2}", outgoingBatches.Count, entity.Path, routingOptions.ViaEntityPath);
                }
                else
                {
                    logger.DebugFormat("Routing {0} messages to {1}", outgoingBatches.Count, entity.Path);
                }

                // don't use via on fallback, not supported across namespaces
                var fallbacks = new List<IMessageSenderInternal>(passiveNamespaces.Count);
                foreach (var passiveNamespace in passiveNamespaces)
                {
                    fallbacks.Add(await sendersLifeCycleManager.Get(entity.Path, null, passiveNamespace.Alias)
                        .ConfigureAwait(false));
                }

                var ns = entity.Namespace;
                // only use via if the destination and via namespace are the same
                var via = routingOptions.SendVia && ns.ConnectionString == routingOptions.ViaConnectionString ? routingOptions.ViaEntityPath : null;
                var suppressTransaction = via == null;
                var messageSender = await sendersLifeCycleManager.Get(entity.Path, via, ns.Alias)
                    .ConfigureAwait(false);

                routingOptions.DestinationEntityPath = entity.Path;
                routingOptions.DestinationNamespace = ns;

                var brokeredMessages = outgoingMessageConverter.Convert(outgoingBatches, routingOptions).ToList();

                pendingSends.Add(RouteOutBatchesWithFallbackAndLogExceptionsAsync(messageSender, fallbacks, brokeredMessages, suppressTransaction));
            }
            await Task.WhenAll(pendingSends)
                .ConfigureAwait(false);
        }

        RoutingOptionsInternal GetRoutingOptions(ReceiveContextInternal receiveContext, DispatchConsistency consistency)
        {
            var sendVia = false;
            var context = receiveContext as BrokeredMessageReceiveContextInternal;
            if (context?.Recovering == false) // avoid send via when recovering to prevent error message from rolling back
            {
                sendVia = sendViaFromConfiguration & transportTypeIsNetMessaging;
                sendVia &= consistency != DispatchConsistency.Isolated;
            }
            return new RoutingOptionsInternal
            {
                SendVia = sendVia,
                ViaEntityPath = GetViaEntityPathFor(context?.Entity),
                ViaConnectionString = context?.Entity.Namespace.ConnectionString,
                ViaPartitionKey = context?.IncomingBrokeredMessage.PartitionKey
            };
        }

        string GetViaEntityPathFor(EntityInfoInternal entity)
        {
            if (entity?.Type == EntityType.Queue)
            {
                return entity.Path;
            }
            if (entity?.Type == EntityType.Subscription)
            {
                var topicRelationship = entity.RelationShips.First(r => r.Type == EntityRelationShipTypeInternal.Subscription);
                return topicRelationship.Target.Path;
            }

            return null;
        }

        async Task RouteOutBatchesWithFallbackAndLogExceptionsAsync(IMessageSenderInternal messageSender, IList<IMessageSenderInternal> fallbacks, IList<BrokeredMessage> messagesToSend, bool suppressTransaction)
        {
            try
            {
                var scope = suppressTransaction && Transaction.Current != null ? new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled) : null;
                if (scope != null)
                {
                    using (scope)
                    {
                        await RouteBatchWithEnforcedBatchSizeAsync(messageSender, messagesToSend).ConfigureAwait(false);
                        scope?.Complete();
                    }
                }
                else
                {
                    await RouteBatchWithEnforcedBatchSizeAsync(messageSender, messagesToSend).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                // ASB team promissed to fix the issue with MessagingEntityNotFoundException (missing entity path) - verify that
                var message = "Failed to dispatch a batch with the following message IDs: " + string.Join(", ", messagesToSend.Select(x => x.MessageId));
                logger.Error(message, exception);

                // no need to try and send too large messages to another namespace, won't work
                if (exception is MessageTooLargeException)
                {
                    throw;
                }

                if (exception is TransactionSizeExceededException)
                {
                    throw new TransactionContainsTooManyMessages(exception);
                }

                var fallBackSucceeded = false;
                if (fallbacks.Any())
                {
                    foreach (var fallback in fallbacks)
                    {
                        var clones = messagesToSend.Select(x => x.Clone()).ToList();
                        try
                        {
                            using (var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                            {
                                await RouteBatchWithEnforcedBatchSizeAsync(fallback, clones).ConfigureAwait(false);
                                scope.Complete();
                            }
                            logger.Info("Successfully dispatched a batch with the following message IDs: " + string.Join(", ", clones.Select(x => x.MessageId) + " to fallback namespace"));
                            fallBackSucceeded = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.Error("Failed to dispatch batch to fallback namespace.", ex);
                        }
                    }
                }

                if (!fallBackSucceeded)
                {
                    throw;
                }
            }
        }

        async Task RouteBatchWithEnforcedBatchSizeAsync(IMessageSenderInternal messageSender, IEnumerable<BrokeredMessage> messagesToSend)
        {
            var chunk = new List<BrokeredMessage>();
            long batchSize = 0;
            var chunkNumber = 1;

            foreach (var message in messagesToSend)
            {
                if (await GuardMessageSize(message).ConfigureAwait(false))
                {
                    return;
                }

                var messageSize = message.EstimatedSize();

                if (batchSize + messageSize > maximuMessageSizeInKilobytes * 1024)
                {
                    if (chunk.Any())
                    {
                        logger.Debug($"Routing batched messages, chunk #{chunkNumber++}.");
                        var currentChunk = chunk;
                        await messageSender.RetryOnThrottleAsync(s => s.SendBatch(currentChunk), s => s.SendBatch(currentChunk.Select(x => x.Clone())), backOffTimeOnThrottle, maxRetryAttemptsOnThrottle).ConfigureAwait(false);
                    }

                    chunk = new List<BrokeredMessage>
                    {
                        message
                    };
                    batchSize = messageSize;
                }
                else
                {
                    chunk.Add(message);
                    batchSize += messageSize;
                }
            }

            if (chunk.Any())
            {
                logger.Debug($"Routing batched messages, chunk #{chunkNumber}.");
                await messageSender.RetryOnThrottleAsync(s => s.SendBatch(chunk), s => s.SendBatch(chunk.Select(x => x.Clone())), backOffTimeOnThrottle, maxRetryAttemptsOnThrottle).ConfigureAwait(false);
            }
        }

        async Task<bool> GuardMessageSize(BrokeredMessage brokeredMessage)
        {
            var estimatedSize = brokeredMessage.EstimatedSize();
            if (estimatedSize > maximuMessageSizeInKilobytes * 1024)
            {
                logger.Debug($"Detected an outgoing message that exceeds the maximum message size allowed by Azure ServiceBus. Estimated message size is {estimatedSize} bytes.");
                await oversizedMessageHandler.Handle(brokeredMessage).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        BatchedOperationsToBrokeredMessagesConverter outgoingMessageConverter;
        MessageSenderLifeCycleManager sendersLifeCycleManager;
        IHandleOversizedBrokeredMessages oversizedMessageHandler;

        bool sendViaFromConfiguration;

        int maxRetryAttemptsOnThrottle;
        TimeSpan backOffTimeOnThrottle;
        int maximuMessageSizeInKilobytes;

        static ILog logger = LogManager.GetLogger<OutgoingBatchRouter>();
        bool transportTypeIsNetMessaging;
    }
}