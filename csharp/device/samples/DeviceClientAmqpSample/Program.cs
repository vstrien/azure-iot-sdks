// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Devices.Client.Samples
{
    class Program
    {

        
        // String containing Hostname, Device Id & Device Key in one of the following formats:
        //  "HostName=<iothub_host_name>;DeviceId=<device_id>;SharedAccessKey=<device_key>"
        //  "HostName=<iothub_host_name>;CredentialType=SharedAccessSignature;DeviceId=<device_id>;SharedAccessSignature=SharedAccessSignature sr=<iot_host>/devices/<device_id>&sig=<token>&se=<expiry_time>";
        private const string DeviceConnectionString = "FILL THIS IN";
        private const string q_temp_current = "{\"cmd\": \"query\", \"params\": {\"SensorType\": \"TempC\", \"ValueType\": \"current\"}}\n";
        private const string q_humidity_current = "{\"cmd\": \"query\", \"params\": {\"SensorType\": \"Humidity\", \"ValueType\": \"current\"}}\n";
        private const int buffer_size = 16;
        private static volatile bool keepRunning = true;

        static void Main(string[] args)
        {
            Guid serviceClass = BluetoothService.SerialPort;
            BluetoothAddress addr = BluetoothAddress.Parse("INSERT BLUETOOTH ADDRESS");

            byte[] recvBuffer = new byte[buffer_size];

            BluetoothEndPoint ep = new BluetoothEndPoint(addr, serviceClass);
            using (BluetoothClient cli = new BluetoothClient())
            {
                try
                {
                    cli.Connect(ep);
                }
                catch (SocketException e)
                {
                    Console.WriteLine("SocketException. Probably the Bluetooth sensor is not in range.");
                    Console.WriteLine(e.Message);
                    Console.WriteLine("Press Enter to exit...");
                    Console.Read();
                    return;
                }

                try
                {
                    DeviceClient deviceClient = DeviceClient.CreateFromConnectionString(DeviceConnectionString);

                    if (deviceClient == null)
                    {
                        Console.WriteLine("Failed to create DeviceClient!");
                    }
                    else
                    {
                        using (Stream peerStream = cli.GetStream())
                        {
                            // Better would be to have this in another thread, with the main thread doing UI. But hey, it's only a PoC and it works! :-)
                            while (keepRunning)
                            {
                                Thread.Sleep(5000);
                                Console.WriteLine(DateTime.Now);

                                Console.WriteLine("Querying: " + q_temp_current);
                                SendBuffered(q_temp_current, buffer_size, peerStream);
                                string tempresult = ReadLine(peerStream, buffer_size);
                                Console.WriteLine("Received: {0}", tempresult);


                                Console.WriteLine("Querying: " + q_humidity_current);
                                SendBuffered(q_humidity_current, buffer_size, peerStream);
                                string humidityresult = ReadLine(peerStream, buffer_size);
                                Console.WriteLine("Received: {0}", humidityresult);


                                SendEventToCloud(deviceClient, tempresult).Wait();
                                SendEventToCloud(deviceClient, humidityresult).Wait();

                                while(Console.KeyAvailable)
                                {
                                    ConsoleKeyInfo key = Console.ReadKey(true);
                                    switch(key.Key)
                                    {
                                        case ConsoleKey.Escape:
                                            Console.WriteLine("You pressed ESC - process will shutdown now.");
                                            keepRunning = false;
                                            break;
                                        default:
                                            Console.WriteLine("Press ESC to shutdown");
                                            break;
                                    }
                                }

                            }
                        }
                    }

                    Console.WriteLine("Press Enter to exit...");
                    Console.Read();
                    Console.WriteLine("Exited!\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error in sample: {0}", ex.Message);
                }

            }
        }

        static async Task SendEventToCloud(DeviceClient deviceClient, string message)
        {
            Message eventMessage = new Message(Encoding.UTF8.GetBytes(message));
            await deviceClient.SendEventAsync(eventMessage);
        }

        static async Task ReceiveCommands(DeviceClient deviceClient)
        {
            Console.WriteLine("\nDevice waiting for commands from IoTHub...\n");
            Message receivedMessage;
            string messageData;

            while (true)
            {
                receivedMessage = await deviceClient.ReceiveAsync(TimeSpan.FromSeconds(1));
                
                if (receivedMessage != null)
                {
                    messageData = Encoding.ASCII.GetString(receivedMessage.GetBytes());
                    Console.WriteLine("\t{0}> Received message: {1}", DateTime.Now.ToLocalTime(), messageData);

                    await deviceClient.CompleteAsync(receivedMessage);
                }
            }
        }


        static void SendBuffered(string msg, int buffer_size, Stream d)
        {
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(msg);
            for (int i = 0; i < msg.Length; i += Math.Min(buffer_size, msg.Length))
            {

                d.Write(buffer, i, Math.Min(buffer_size, buffer.Length - i));
                ReadAndCheckForAck(d);
            }
        }

        public static void ReadAndCheckForAck(Stream stream)
        {
            int offset = 0;
            int remaining = 5;
            byte[] data = new byte[remaining];

            while (remaining > 0)
            {
                int read = stream.Read(data, offset, remaining);
                if (read <= 0)
                    throw new EndOfStreamException
                        (String.Format("End of stream reached with {0} bytes left to read", remaining));
                remaining -= read;
                offset += read;
            }
            string result = System.Text.Encoding.UTF8.GetString(data);
            if (result.Substring(0, 3) != "ACK")
            {
                throw new Exception("Didn't receive expected ACK");
            }

        }

        /// <summary>
        /// Reads data from a stream until the end is reached. The
        /// data is returned as a byte array. An IOException is
        /// thrown if any of the underlying IO calls fail.
        /// </summary>
        /// <param name="stream">The stream to read data from</param>
        public static string ReadLine(Stream stream, int buffer_size)
        {
            string result = "";
            int bufint;
            byte[] buffer = new byte[1];
            int finishedState = 0;

            while (finishedState < 2)
            {
                bufint = stream.ReadByte();

                if (bufint < 0)
                    throw new EndOfStreamException(String.Format("End of stream reached without encountering \\r\\n"));

                buffer[0] = (byte)bufint;
                result += System.Text.Encoding.UTF8.GetString(buffer);
                if (System.Text.Encoding.UTF8.GetChars(buffer)[0] == '\r')
                {
                    finishedState += 1;
                }
                else if (System.Text.Encoding.UTF8.GetChars(buffer)[0] == '\n' && finishedState == 1)
                {
                    finishedState += 1;
                }
                else if (finishedState > 0)
                {
                    throw new Exception("\\n was not followed by \\r");
                }
            }

            return result;
        }
    }
}
