namespace NServiceBus.Transport.AzureServiceBus
{
    using System;
    using System.Threading.Tasks;

    interface INotifyIncomingMessagesInternal
    {
        bool IsRunning { get; }
        int RefCount { get; set; }

        void Initialize(EntityInfoInternal entity, Func<IncomingMessageDetailsInternal, ReceiveContextInternal, Task> callback, Func<Exception, Task> errorCallback, Action<Exception> onCritical, Func<ErrorContext, Task<ErrorHandleResult>> processingFailureCallback, int maximumConcurrency);

        void Start();
        Task Stop();
    }
}