using System;
using System.Collections.Generic;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;

namespace stoplicht_controller.Classes
{
    public class Communicator
    {
        // Publisher socket for sending messages
        private PublisherSocket publisher;
        // Address to publish messages to
        private readonly string publishAddress;
        // Address to subscribe for incoming messages
        private readonly string subscribeAddress;
        // Topics to listen for on the subscriber side
        private readonly string[] subscribeTopics;
        // Threads handling each topic subscription
        private readonly List<Thread> subscribeThreads = new();

        // Incoming message buffers, updated by subscription threads
        public string? LaneSensorData { get; set; }
        public string? SpecialSensorData { get; set; }
        public string? PriorityVehicleData { get; set; }
        public string? BridgeSensorData { get; set; }
        // Lock object to synchronize access to incoming data properties
        private readonly object _dataLock = new object();

        /// <summary>
        /// Creates a new Communicator, binding the publisher socket and storing subscription settings.
        /// </summary>
        /// <param name="subscribeAddress">Endpoint to connect subscriber sockets to.</param>
        /// <param name="publisherAddress">Endpoint to bind the publisher socket to.</param>
        /// <param name="subscribeTopics">Topics that this communicator will subscribe to.</param>
        public Communicator(string subscribeAddress, string publisherAddress, string[] subscribeTopics)
        {
            this.subscribeAddress = subscribeAddress;
            this.subscribeTopics = subscribeTopics;
            this.publishAddress = publisherAddress;

            // Initialize and bind the publisher socket
            publisher = new PublisherSocket();
            publisher.Bind(this.publishAddress);
        }

        /// <summary>
        /// Starts subscriber threads for each configured topic.
        /// </summary>
        public void StartSubscriber()
        {
            foreach (string topic in subscribeTopics)
            {
                StartSubscriber(topic);
            }
        }

        /// <summary>
        /// Spins up a background thread to listen for messages on a single topic.
        /// </summary>
        /// <param name="topic">The topic string to subscribe to.</param>
        private void StartSubscriber(string topic)
        {
            Thread subscribeThread = new Thread(() =>
            {
                try
                {
                    using (SubscriberSocket subscriber = new SubscriberSocket())
                    {
                        subscriber.Connect(subscribeAddress);
                        subscriber.Subscribe(topic);
                        Console.WriteLine($"Subscriber connected to {subscribeAddress}, listening on topic '{topic}'...");

                        while (true)
                        {
                            // First frame is the topic
                            string receivedTopic = subscriber.ReceiveFrameString().Trim();
                            // Second frame is the JSON payload
                            string message = subscriber.ReceiveFrameString().Trim();
                            ProcessMessage(receivedTopic, message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in subscriber for topic '{topic}': {ex.Message}");
                }
            });

            subscribeThreads.Add(subscribeThread);
            subscribeThread.Start();
        }

        /// <summary>
        /// Parses incoming messages and updates the corresponding data property.
        /// </summary>
        /// <param name="topic">Topic of the incoming message.</param>
        /// <param name="message">JSON string payload.</param>
        private void ProcessMessage(string topic, string message)
        {
            lock (_dataLock)
            {
                switch (topic)
                {
                    case "sensoren_rijbaan":
                        LaneSensorData = message;
                        break;
                    case "sensoren_speciaal":
                        SpecialSensorData = message;
                        break;
                    case "voorrangsvoertuig":
                        PriorityVehicleData = message;
                        Console.WriteLine(PriorityVehicleData);
                        break;
                    case "sensoren_bruggen":
                        BridgeSensorData = message;
                        break;
                    case "tijd":
                        // Optionally handle time synchronization messages here
                        break;
                    default:
                        Console.WriteLine($"Unknown topic received: {topic}");
                        break;
                }
            }
        }

        /// <summary>
        /// Publishes a payload object as a JSON-formatted message under the given topic.
        /// Automatically recreates the publisher socket if it has been disposed.
        /// </summary>
        /// <param name="topic">Topic under which to send the message.</param>
        /// <param name="payload">Object to serialize and send.</param>
        public void PublishMessage(string topic, object payload)
        {
            // If the socket was disposed, recreate and rebind it
            if (publisher.IsDisposed)
            {
                publisher = new PublisherSocket();
                publisher.Bind(this.publishAddress);
            }

            // Serialize the payload to indented JSON
            string jsonMessage = JsonConvert.SerializeObject(payload, Formatting.Indented);

            // Send the multipart message: topic frame, then JSON frame
            publisher.SendMoreFrame(topic).SendFrame(jsonMessage);
        }
    }
}
