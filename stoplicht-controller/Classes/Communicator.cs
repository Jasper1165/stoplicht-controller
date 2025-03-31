using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;

namespace stoplicht_controller.Classes
{
    public class Communicator
    {
        private readonly PublisherSocket publisher;
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
            this.publisher = new PublisherSocket();
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
                            string message = Regex.Replace(subscriber.ReceiveFrameString(), @"^[^{]+", "").Trim();
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
                    Console.WriteLine($"PriorityVehicleData updated: {PriorityVehicleData}");
                    break;
                default:
                    Console.WriteLine($"Onbekend topic ontvangen: {topic}");
                    break;
            }
        }

        public void StartPublisher(string topic)
        {
            publisher.Bind(this.publishAddress);
            Console.WriteLine($"Publisher gestart op {this.publishAddress}...");

            new Thread(() =>
            {
                    int count = 1;
                    while (true)
                    {
                    // JSON structuur behouden met Dictionary
                    var statusUpdate = new Dictionary<string, object>
                    {
                        { "1.1", new { voor = false, achter = false } },
                        { "22.1", new { voor = true, achter = false } }
                    };

                    string jsonMessage = JsonSerializer.Serialize(statusUpdate);

                    publisher
                    .SendMoreFrame(topic)
                    .SendFrame(jsonMessage);

                    count++;
                    Thread.Sleep(100);
                }
            }).Start();
        }
    }
}
