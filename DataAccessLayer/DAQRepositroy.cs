using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using Utility.Classes;

namespace DataAccessLayer
{
    public class DAQRepository : IDAQRepository
    {
        // --- Serial Port Configuration ---
        private static string _serialPortName = "COM3";
        private static int _baudRate = 115200;
        private static Parity _parity = Parity.None;
        private static int _dataBits = 8;
        private static readonly StopBits _stopBits = StopBits.One;
        private static int _serialWriteTimeout = 1000;
        private static int _serialReadTimeout = 5000;

        public DAQRepository()
        {
            // Load Serial Port Configuration from default path
            _ = LoadConfigurationFromJson();
        }

        public EITMeasurement GetEITMeasurement()
        {
            // TODO: Serial Communication
            EITMeasurement? measurement = ReadMeasurementFromSerialPort();

            if (measurement == null)
                throw new ArgumentNullException(nameof(measurement), "Measurement reading returned null, check code, and device communication!");

            return measurement;
        }

        private EITMeasurement? ReadMeasurementFromSerialPort()
        {
            using (SerialPort serialPort = new SerialPort(_serialPortName, _baudRate, _parity, _dataBits, _stopBits))
            {
                try
                {
                    double[,] measurements = new double[16, 16];

                    // TODO: read serial 

                    return new EITMeasurement(measurements);
                }
                catch (TimeoutException tex)
                {
                    Debug.WriteLine($"Serial Error on port {_serialPortName}: Timeout - {tex.Message}");
                    return null;
                }
                catch (UnauthorizedAccessException uaex)
                {
                    Debug.WriteLine($"Serial Error on port {_serialPortName}: Access Denied (is port open elsewhere?) - {uaex.Message}");
                    return null;
                }
                catch (InvalidOperationException ioex)
                {
                    Debug.WriteLine($"Serial Error on port {_serialPortName}: Port may already be open - {ioex.Message}");
                    return null;
                }
                catch (System.IO.IOException sioex)
                {
                    Debug.WriteLine($"Serial Error on port {_serialPortName}: IO Error (port disconnected?) - {sioex.Message}");
                    return null;
                }
                catch (Exception ex)
                {
                    // Catch other potential exceptions (e.g., port not found)
                    Debug.WriteLine($"Serial Error on port {_serialPortName}: {ex.GetType().Name} - {ex.Message}");
                    return null;
                }
            }
        }

        public async Task LoadConfigurationFromJson(string path = "config.json")
        {
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine("Cannot find config.json!");
                return;
            }

            string jsonString = await System.IO.File.ReadAllTextAsync(path);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            SerialPortConfiguration? config = JsonSerializer.Deserialize<SerialPortConfiguration>(jsonString, options);

            if (config != null)
            {
                _serialPortName = (config.PortName) ?? "COM1";
                _baudRate = (config.BaudeRate == null) ? 115200 : Convert.ToInt32(config.BaudeRate);
                _parity = (config.Parity == null) ? Parity.None : config.Parity == "None" ? Parity.None : Parity.Even;
                _dataBits = (config.DataBits) ?? 8;
                _serialWriteTimeout = (config.SerialWriteTimeOut) ?? 1000;
                _serialReadTimeout = (config.SerialReadTimeOut) ?? 5000;
            }
            else
            {
                Debug.WriteLine(nameof(config) + "was null, didnt read anything during ESP32Communicator initialization, check file!");
                throw new ArgumentNullException(nameof(config));
            }
        }
    }
}
