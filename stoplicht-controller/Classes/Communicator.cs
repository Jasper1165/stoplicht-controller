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
        public string? BridgeSensorData { get; set; }
        private readonly object _dataLock = new object();

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
                            // Lees het eerste frame, dat de topic bevat
                            string receivedTopic = subscriber.ReceiveFrameString().Trim();
                            // Lees het tweede frame, dat de payload bevats
                            string message = subscriber.ReceiveFrameString().Trim();
                            if (receivedTopic == "voorrangsvoertuig")
                            {
                                Console.WriteLine($"Bericht ontvangen op topic '{receivedTopic}': {message}");

                            }
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
                        // Verwerk tijd bericht indien nodig
                        break;
                    default:
                        Console.WriteLine($"Onbekend topic ontvangen: {topic}");
                        break;
                }
            }
        }

        public void PublishMessage(string topic, object payload)
        {
            // Herstel publisher als de socket is disposed
            if (publisher.IsDisposed)
            {
                publisher = new PublisherSocket();
                publisher.Bind(this.publishAddress);
                // Console.WriteLine($"Publisher opnieuw verbonden met {publishAddress}");
            }

            // Verwerk JSON payload
            string jsonMessage = JsonConvert.SerializeObject(payload, Formatting.Indented);

            // Console.WriteLine(jsonMessage);

            // Verstuur topic + bericht als multipart
            publisher.SendMoreFrame(topic).SendFrame(jsonMessage);
            // Console.WriteLine($"Bericht verzonden naar topic '{topic}': {jsonMessage}");
        }
    }
}
