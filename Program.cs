using System;
using System.Net.NetworkInformation;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using NModbus;

namespace ModbusSlave
{
    class Program
    {

        static byte[] rxBuffer = new byte[20];
        static int bufferIndex = 0;
        static float frequency;

        ////////////////////////////////
        /// Main entry point
        ////////////////////////////////
        static void Main(string[] args)
        {
            SerialPort? serialPort = null;

            try
            {

                if (args.Length < 4) throw new Exception("Usage: ModbusSlave [uart] [lan] [tcp port] [Slave ID]\ne.g. ModbusSlave serial0 eth0 1502 1");

                var uart = args[0];

                var nic = args[1];
                
                var tcpport = int .Parse(args[2]);

                var slaveId = byte.Parse(args[3]);

                var dev = $"/dev/{uart}";    

                // create and open serial port
                serialPort = new SerialPort(dev, 115200, Parity.None, 8, StopBits.One);

                serialPort.ReceivedBytesThreshold = 1;

                serialPort.DataReceived += DataReceivedHandler;

                serialPort.Open();

                Console.WriteLine();
                Console.WriteLine("--------");
                Console.WriteLine("NIC List");
                Console.WriteLine("--------");

                // create list of IPV4 network interface names and associated IP addresses
                Dictionary<string, IPAddress> nics = new();

                foreach(NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    Console.WriteLine(ni.Name);
                    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            nics.Add(ni.Name, ip.Address);
                        }
                    }
                } 

                // retrieve IP adrress for required interface
                var ipaddress = nics[nic];

                Console.WriteLine($"\n\nStarting slave on {nic} at IP address {ipaddress.ToString()}");

                // create and start the TCP slave
                var listener = new TcpListener(ipaddress, tcpport);
                listener.Start();

                // create nmodbus factory
                IModbusFactory factory = new ModbusFactory();

                // create network using TcpListener
                IModbusSlaveNetwork network = factory.CreateSlaveNetwork(listener);

                // create storage for modbus registers
                var dataStore = new SlaveStorage();

                IModbusSlave slave = factory.CreateSlave(slaveId, dataStore);
                // IModbusSlave slave2 = factory.CreateSlave(2);

                network.AddSlave(slave);
                // network.AddSlave(slave2);

                // create task to update holding registers
                Task.Run(() =>  
                {
                    ushort[] words = new ushort[2];
                    float lastfrequency = 0.0f;

                    while(true)
                    {
                        Thread.Sleep(250);

                        // dummy code to modify frequency
                        // frequency += 0.123f;

                        // only update if value has changed
                        if (frequency != lastfrequency)
                        {
                            // convert float to bytes
                            var bytes = BitConverter.GetBytes(frequency);

                            // extract lo and hi words
                            words[0] = BitConverter.ToUInt16(bytes, 0);
                            words[1] = BitConverter.ToUInt16(bytes, 2);

                            // write to holding regs
                            dataStore.HoldingRegisters.WritePoints(0, words);

                            // update lastfrequency value
                            lastfrequency = frequency;
                        }
                    }
                });

                // listen on network for requests - blocking
                network.ListenAsync().Wait();

            }
            catch (System.Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            serialPort?.Close();

        }

        ////////////////////////////////
        /// Handle incoming data on uart
        ////////////////////////////////
        private static void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                SerialPort port = (SerialPort)sender;

                int data = port.ReadByte();

                if (data == 10)
                {
                    rxBuffer[bufferIndex++] = 0;

                    var s = System.Text.Encoding.UTF8.GetString(rxBuffer).Trim('\0');

                    Console.WriteLine($"Data Received: {s}");

                    frequency = float.Parse(s);

                    resetRxBuffer();
                }
                else
                {
                    rxBuffer[bufferIndex++] = (byte)data;
                }

                // check for buffer overrun
                if (bufferIndex == rxBuffer.Length) bufferIndex = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
                resetRxBuffer();
            }
        }

        private static void resetRxBuffer()
        {
            bufferIndex = 0;
            Array.Fill<byte>(rxBuffer, 0);
        }
    }
}
