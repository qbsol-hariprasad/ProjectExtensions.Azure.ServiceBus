﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using ProjectExtensions.Azure.ServiceBus.Serialization;
using NLog;
using Microsoft.AzureCAT.Samples.TransientFaultHandling.ServiceBus;
using Microsoft.AzureCAT.Samples.TransientFaultHandling;
using System.Diagnostics;
using System.Threading;

namespace ProjectExtensions.Azure.ServiceBus {

    /// <summary>
    /// Sender class that publishes messages to the bus
    /// </summary>
    class AzureBusSender : AzureSenderReceiverBase, IAzureBusSender {
        static Logger logger = LogManager.GetCurrentClassLogger();
        TopicClient client;

        public AzureBusSender(BusConfiguration configuration)
            : base(configuration) {
            client = factory.CreateTopicClient(topic.Path);
        }

        public void Close() {
            if (client != null) {
                client.Close();
                client = null;
            }
        }

        public void Send<T>(T obj, IDictionary<string, object> metadata) {
            Send<T>(obj, metadata, configuration.DefaultSerializer.Create());
        }

        public void Send<T>(T obj, IDictionary<string, object> metadata, IServiceBusSerializer serializer) {

            // Declare a wait object that will be used for synchronization.
            var waitObject = new ManualResetEvent(false);
            
            // Declare a timeout value during which the messages are expected to be sent.
            var sentTimeout = TimeSpan.FromMinutes(2);

            Exception failureException = null;
            BrokeredMessage message = null;

            // Use a retry policy to execute the Send action in an asynchronous and reliable fashion.
            retryPolicy.ExecuteAction
            (
                (cb) => {
                    // A new BrokeredMessage instance must be created each time we send it. Reusing the original BrokeredMessage instance may not 
                    // work as the state of its BodyStream cannot be guaranteed to be readable from the beginning.
                    message = new BrokeredMessage(serializer.Serialize(obj), false);

                    message.MessageId = Guid.NewGuid().ToString();
                    message.Properties.Add(TYPE_HEADER_NAME, obj.GetType().FullName.Replace('.', '_'));

                    if (metadata != null) {
                        foreach (var item in metadata) {
                            message.Properties.Add(item.Key, item.Value);
                        }
                    }

                    logger.Log(LogLevel.Info, "Send Type={0} Serializer={1} MessageId={2}", obj.GetType().FullName, serializer.GetType().FullName, message.MessageId);

                    // Send the event asynchronously.
                    client.BeginSend(message, cb, null);
                },
                (ar) => {
                    try {
                        // Complete the asynchronous operation. This may throw an exception that will be handled internally by the retry policy.
                        client.EndSend(ar);
                    }
                    finally {
                        // Ensure that any resources allocated by a BrokeredMessage instance are released.
                        if (message != null) {
                            message.Dispose();
                            message = null;
                        }
                        if (serializer != null) {
                            serializer.Dispose();
                            serializer = null;
                        }
                        waitObject.Set();
                    }
                },
                (ex) => {
                    // Always dispose the BrokeredMessage instance even if the send operation has completed unsuccessfully.
                    if (message != null) {
                        message.Dispose();
                        message = null;
                    }
                    if (serializer != null) {
                        serializer.Dispose();
                        serializer = null;
                    }
                    failureException = ex;
                    // Always log exceptions.
                    logger.Error<Exception>("Send failed {0}", ex);
                }
            );

            // Wait until the messaging operations are completed.
            bool completed = waitObject.WaitOne(sentTimeout);
            waitObject.Dispose();

            if (completed) {
                //DO Nothing
            }
            else {
                if (failureException != null) {
                    throw failureException;
                }
                throw new Exception("Failed to Send Message for Unknown Reason.");
            }
        }

        public override void Dispose(bool disposing) {
            Close();
        }
    }
}
