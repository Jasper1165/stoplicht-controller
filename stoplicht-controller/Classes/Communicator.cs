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
        private PublisherSocket publisher;
        private readonly string publishAddress;
        private readonly string subscribeAddress;
        private readonly string[] subscribeTopics;
        private readonly List<Thread> subscribeThreads = new();

        public string? LaneSensorData { get; set; }
        public string? SpecialSensorData { get; set; }
        public string? PriorityVehicleData { get; set; }

        public Communicator(string subscribeAddress, string publisherAddress, string[] subscribeTopics)
        {
            this.subscribeAddress = subscribeAddress;
            this.subscribeTopics = subscribeTopics;
            this.publishAddress = publisherAddress;

            publisher = new PublisherSocket();
            publisher.Bind(this.publishAddress); // Bind de publisher aan het adres
        }

        public void StartSubscriber()
        {
            foreach (string topic in subscribeTopics)
            {
                StartSubscriber(topic);
            }
        }

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
                        Console.WriteLine($"Subscriber verbonden met {subscribeAddress}, luistert naar topic '{topic}'...");

                        while (true)
                        {
                            string receivedTopic = topic;
                            string message = subscriber.ReceiveFrameString().Trim();
                            Console.WriteLine($"Bericht ontvangen op topic '{receivedTopic}': {message}");
                            ProcessMessage(receivedTopic, message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fout in Subscriber voor topic '{topic}': {ex.Message}");
                }
            });

            subscribeThreads.Add(subscribeThread);
            subscribeThread.Start();
        }

        private void ProcessMessage(string topic, string message)
        {
            switch (topic)
            {
                case "sensoren_rijbaan":
                    LaneSensorData = message;
                    break;
                case "sensoren_speciaal":
                    SpecialSensorData = message;
                    Console.WriteLine($"SpecialSensorData updated: {SpecialSensorData}");
                    break;
                case "voorrangsvoertuig":
                    PriorityVehicleData = message;
                    break;
                case "tijd":
                    // Console.WriteLine($"Tijd bericht ontvangen: {message}");
                    // Process any time-related message if needed
                    break;
                default:
                    Console.WriteLine($"Onbekend topic ontvangen: {topic}");
                    break;
            }
        }

        public void PublishMessage(string topic, object payload)
        {
            // Herstel publisher als de socket is disposed
            if (publisher.IsDisposed)
            {
                publisher = new PublisherSocket();
                publisher.Bind(this.publishAddress);
                Console.WriteLine($"Publisher opnieuw verbonden met {publishAddress}");
            }

            // Verwerk JSON payload
            string jsonMessage = JsonConvert.SerializeObject(payload, Formatting.Indented);

            // Verstuur topic + bericht als multipart
            publisher.SendMoreFrame(topic).SendFrame(jsonMessage);
            // Console.WriteLine($"Bericht verzonden naar topic '{topic}': {jsonMessage}");
        }
    }
}
