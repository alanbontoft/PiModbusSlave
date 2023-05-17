using System.Net.NetworkInformation;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using NModbus;

namespace ModbusSlave
{
    class Program
    {

        enum HOLDINGREGS
        {
            FREQUENCY = 0,
            FLOWRATE = 2,
            FILLTIME = 4
        }

        static byte[] rxBuffer = new byte[20];
        static int bufferIndex = 0;
        static float frequency, flowrate, pulsesperlitre, filltime;



        ////////////////////////////////
        /// Main entry point
        ////////////////////////////////
        static void Main(string[] args)
        {
            SerialPort? serialPort = null;

            Stopwatch stopwatch = new();

            try
            {

                if (args.Length < 5) throw new Exception("Usage: ModbusSlave [uart] [lan] [tcp port] [Slave ID] [Pulses per litre]\ne.g. ModbusSlave serial0 eth0 1502 1 1200");

                var uart = args[0];

                var nic = args[1];
                
                var tcpport = int .Parse(args[2]);

                var slaveId = byte.Parse(args[3]);

                pulsesperlitre = float.Parse(args[4]);

                var dev = $"/dev/{uart}";    

                // create and open serial port
                serialPort = new SerialPort(dev, 115200, Parity.None, 8, StopBits.One);
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

                Console.WriteLine($"\nStarting slave at address {slaveId} on {nic} at IP address {ipaddress.ToString()}\n");

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
                        // only update frequency/flowrate if value has changed
                        if (frequency != lastfrequency)
                        {
                            // convert float to bytes
                            var bytes = BitConverter.GetBytes(frequency);

                            // extract lo and hi words
                            words[0] = BitConverter.ToUInt16(bytes, 0);
                            words[1] = BitConverter.ToUInt16(bytes, 2);

                            // write to holding regs
                            dataStore.HoldingRegisters.WritePoints((ushort)HOLDINGREGS.FREQUENCY, words);

                            // calculate flow rate
                            flowrate = frequency * 60.0F / pulsesperlitre;

                            bytes = BitConverter.GetBytes(flowrate);

                            // extract lo and hi words
                            words[0] = BitConverter.ToUInt16(bytes, 0);
                            words[1] = BitConverter.ToUInt16(bytes, 2);

                            // write to holding regs
                            dataStore.HoldingRegisters.WritePoints((ushort)HOLDINGREGS.FLOWRATE, words);

                            // update lastfrequency value
                            lastfrequency = frequency;
                        }

                        // update fill time on every pass
                        if (frequency == 0.0)
                        {
                            stopwatch.Reset();
                            filltime = 0.0F;
                        }
                        else
                        {
                            if (stopwatch.IsRunning)
                            {
                                filltime = (float)stopwatch.ElapsedMilliseconds / 1000.0F;
                            }
                            else
                            {
                                stopwatch.Restart();
                            }
                        }

                        var fillbytes = BitConverter.GetBytes(filltime);

                        // extract lo and hi words
                        words[0] = BitConverter.ToUInt16(fillbytes, 0);
                        words[1] = BitConverter.ToUInt16(fillbytes, 2);

                        // write to holding regs
                        dataStore.HoldingRegisters.WritePoints((ushort)HOLDINGREGS.FILLTIME, words);

                        Thread.Sleep(100);
                    }
                });

                // create task to read serial port input
                Task.Run(() =>  
                {
                    string uartData;

                    while(true)
                    {
                        try
                        {
                            if (serialPort.BytesToRead > 0)
                            {
                                
                                uartData = serialPort.ReadExisting();

                                foreach (char data in uartData)
                                {
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

                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Exception: {ex.Message}");
                            resetRxBuffer();
                        }

                        Thread.Sleep(100);
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

        private static void resetRxBuffer()
        {
            bufferIndex = 0;
            Array.Fill<byte>(rxBuffer, 0);
        }
    }
}
