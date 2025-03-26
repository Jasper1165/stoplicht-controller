using System;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;

namespace stoplicht_controller.Classes
{
    public class Communicator
    {
        private readonly PublisherSocket publisher;
        private readonly SubscriberSocket subscriber;
        public String? LaneSensorData {get; set;}
        public String? SpecialSensorData {get; set;}
        public String? PriorityVehicleData {get; set;}
        private readonly string subscribeAddress;
        private readonly string[] subscribeTopics; // Array van topics
        private readonly List<Thread> subscribeThreads = new();
        private readonly List<SubscriberSocket> subscribers = new();

        public Communicator(string subscribeAddress, string[] subscribeTopics)
        {
            this.subscribeAddress = subscribeAddress;
            this.subscribeTopics = subscribeTopics;
        }

        public void Start()
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
                            string receivedTopic = subscriber.ReceiveFrameString();
                            string message = subscriber.ReceiveFrameString();
                            // process message and update properties
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
        // Process message and update properties
        private void ProcessMessage(string topic, string message)
        {
            switch (topic)
            {
                case "sensoren_rijbaan":
                    LaneSensorData = message;
                    Console.WriteLine($"LaneSensorData updated: {LaneSensorData}");
                    // Deserialize JSON en verwerk data
                    break;
                case "sensoren_speciaal":
                    SpecialSensorData = message;
                    Console.WriteLine($"SpecialSensorData updated: {SpecialSensorData}");
                    // Deserialize JSON en verwerk data
                    break;
                case "voorrangsvoertuig":
                    PriorityVehicleData = message;
                    Console.WriteLine($"PriorityVehicleData updated: {PriorityVehicleData}");
                    // Deserialize JSON en verwerk data
                    break;
                default:
                    Console.WriteLine($"Onbekend topic ontvangen: {topic}");
                    break;
            }
        }

        public void Stop()
        {
            // stop all threads
            foreach (Thread thread in subscribeThreads)
            {
                thread.Abort();
            }
        }

    }
}
