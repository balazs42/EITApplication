using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text.Json;
using Utility.Classes;
using Utility.Classes.Measurement;

namespace DataAccessLayer
{
    // Example measurement data:
    /*     1.  |   2.   |    3.  |    4.  |    5.  |    6.  |    7.  |    8.  |    9.  |   10.  |   11.  |   12.  |   13.  |   14.  |   15.  |   16.  |
     *    NAN  |  NAN   | +0.014 | +0.012 | +0.012 | +0.012 | +0.013 | +0.010 | +0.013 | +0.012 | +0.012 | +0.014 | +0.014 | +0.014 | +0.013 |   NAN  |  
     *    NAN  |  NAN   |   NAN  | +0.014 | +0.012 | +0.011 | +0.013 | +0.012 | +0.010 | +0.014 | +0.013 | +0.013 | +0.012 | +0.012 | +0.012 | +0.014 | 
     *  +0.014 |  NAN   |   NAN  |   NAN  | +0.010 | +0.010 | +0.012 | +0.012 | +0.013 | +0.014 | +0.015 | +0.014 | +0.013 | +0.014 | +0.012 | +0.014 |
     *  +0.013 | +0.013 |   NAN  |   NAN  |   NAN  | +0.011 | +0.013 | +0.013 | +0.012 | +0.013 | +0.013 | +0.012 | +0.014 | +0.013 | +0.014 | +0.014 | 
     *  +0.014 | +0.014 | +0.013 |   NAN  |   NAN  |   NAN  | +0.010 | +0.013 | +0.011 | +0.013 | +0.013 | +0.013 | +0.013 | +0.013 | +0.012 | +0.011 |
     *  +0.014 | +0.012 | +0.014 | +0.013 |   NAN  |   NAN  |   NAN  | +0.011 | +0.013 | +0.012 | +0.015 | +0.013 | +0.014 | +0.013 | +0.014 | +0.015 | 
     *  +0.012 | +0.012 | +0.012 | +0.012 | +0.013 |   NAN  |   NAN  |   NAN  | +0.012 | +0.014 | +0.012 | +0.013 | +0.013 | +0.013 | +0.013 | +0.013 | 
     *  +0.013 | +0.013 | +0.012 | +0.010 | +0.011 | +0.014 |   NAN  |   NAN  |   NAN  | +0.013 | +0.012 | +0.016 | +0.015 | +0.012 | +0.013 | +0.014 |
     *  +0.013 | +0.012 | +0.014 | +0.013 | +0.013 | +0.012 | +0.012 |   NAN  |   NAN  |   NAN  | +0.015 | +0.014 | +0.014 | +0.013 | +0.012 | +0.014 | 
     *  +0.012 | +0.012 | +0.014 | +0.013 | +0.012 | +0.010 | +0.014 | +0.013 |   NAN  |   NAN  |   NAN  | +0.015 | +0.012 | +0.012 | +0.014 | +0.013 | 
     *  +0.011 | +0.012 | +0.011 | +0.013 | +0.015 | +0.010 | +0.012 | +0.015 | +0.014 |   NAN  |   NAN  |   NAN  | +0.013 | +0.013 | +0.012 | +0.012 |
     *  +0.011 | +0.012 | +0.012 | +0.012 | +0.014 | +0.013 | +0.013 | +0.013 | +0.012 | +0.014 |   NAN  |   NAN  |   NAN  | +0.013 | +0.013 | +0.012 | 
     *  +0.012 | +0.011 | +0.013 | +0.012 | +0.010 | +0.016 | +0.013 | +0.014 | +0.013 | +0.015 | +0.013 |   NAN  |   NAN  |   NAN  | +0.012 | +0.014 | 
     *  +0.013 | +0.011 | +0.012 | +0.015 | +0.013 | +0.014 | +0.012 | +0.014 | +0.013 | +0.014 | +0.014 | +0.013 |   NAN  |   NAN  |   NAN  | +0.013 |
     *  +0.013 | +0.013 | +0.014 | +0.012 | +0.013 | +0.014 | +0.012 | +0.014 | +0.012 | +0.013 | +0.013 | +0.014 | +0.012 |   NAN  |   NAN  |   NAN  |  
     *
     *  1. = NaN & 2. = NaN means 1 = GND & 2 = VCC -> 3. means measurement between 3.-4. electodes. 4. meash measurement between 4.-5. ... 
     *  15. means measurement between 15.-16. 16. = NaN since 16. = 16.-1. and 1. is used for excitation.
     */

    public class DAQRepository : IDAQRepository
    {
        // --- Serial Port Configuration ---
        private static string   _portName =     "COM3";
        private static int      _baudRate =     115_200;
        private static Parity   _parity =       Parity.None;
        private static int      _dataBits =     8;
        private static StopBits _stopBits =     StopBits.One;
        private static int      _writeTOms =    1_000;
        private static int      _readTOms =     5_000;

        // --- CancellationToken for the Background Sampling Task ---
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _backgroundTask;

        // --- Event To Invoke When a Measurement is Received ---
        public event EventHandler<EITMeasurements>? MeasurementReceived;

        public DAQRepository()
        {
            // Load Serial Port Configuration from default path
            _ = LoadConfigurationFromJson();

            // TODO: Exit condition
            //_backgroundTask = Task.Run(() => SerialLoopAsync(_cts.Token));
        }

        /* ===================================================================
         *  BACKGROUND LOOP
         * ===================================================================*/
        private async Task SerialLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var meas = ReadBlock();
                    if (meas == null) 
                        continue;

                    for(int i = 0; i < 16; i++)
                        await SaveToJsonAsync(meas, token);

                    MeasurementReceived?.Invoke(this, meas);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DAQ loop error: {ex.Message}");
                    await Task.Delay(1_000, token);          // simple back-off
                }
            }
        }

        public EITMeasurements GetEITMeasurement()
            => ReadBlock() ?? throw new InvalidOperationException("No data received");

        private EITMeasurements? ReadBlock()
        {
            using var port = new SerialPort(_portName, _baudRate, _parity,
                                            _dataBits, _stopBits)
            {
                ReadTimeout = _readTOms,
                WriteTimeout = _writeTOms,
                NewLine = "\n"
            };

            try
            {
                port.Open();

                /*–– wait for header –––––––––––––––––––––––––––––––––––––––*/
                string line;
                do { line = port.ReadLine().Trim(); }
                while (!line.StartsWith("Measurements", StringComparison.OrdinalIgnoreCase));

                /*–– read 16 data rows –––––––––––––––––––––––––––––––––––––*/
                var vals = new double[16, 13];
                for (int row = 0; row < 16; ++row)
                {
                    line = port.ReadLine();                         // ← includes NANs
                    double[] nums = ParseRow(line);                 // 13 numbers
                    for (int col = 0; col < 13; ++col) vals[row, col] = nums[col];
                }

                /*–– consume end marker –––––––––––––––––––––––––––––––––––*/
                line = port.ReadLine().Trim();
                if (!line.StartsWith("End of measurements", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Missing end marker.");

                return new EITMeasurements(vals);
            }
            catch (TimeoutException) { return null; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Serial error: {ex.Message}");
                return null;
            }
        }

        /*───────────────────────────────────────────────────────────────────
         *  "NAN-aware" row parser –> 13 doubles
         *──────────────────────────────────────────────────────────────────*/
        private static double[] ParseRow(string line, int expected = 13)
        {
            var tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            var list = new List<double>(expected);
            foreach (var t in tokens)
            {
                if (t.Equals("nan", StringComparison.OrdinalIgnoreCase)) continue;

                list.Add(double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture));
            }

            if (list.Count != expected)
                throw new FormatException($"Expected {expected} numbers, got {list.Count}");

            return list.ToArray();
        }

        /*───────────────────────────────────────────────────────────────────
         *  Persist measurement to disk (jagged JSON)
         *──────────────────────────────────────────────────────────────────*/
        private static async Task SaveToJsonAsync(EITMeasurements m, CancellationToken ct)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Measurements");
            Directory.CreateDirectory(dir);

            string file = Path.Combine(dir,
                $"EIT_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");

            var jagged = new double[16][];
            for (int i = 0; i < 16; ++i)
            {
                jagged[i] = new double[16];
                SixteenElectrodeMeasurement measurement = m.GetMeasurement(i);
                
                for (int j = 0; j < 16; ++j)
                    jagged[i][j] = measurement.Measurement[j];
            }

            var opts = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(jagged, opts), ct);
        }

        /*───────────────────────────────────────────────────────────────────
         *  Serial-port config loader
         *──────────────────────────────────────────────────────────────────*/
        public async Task LoadConfigurationFromJson(string path = "config.json")
        {
            if (!File.Exists(path))
            {
                Debug.WriteLine("config.json not found – defaults in use.");
                return;
            }

            string json = await File.ReadAllTextAsync(path);
            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cfg = JsonSerializer.Deserialize<SerialPortConfiguration>(json, opt);
            if (cfg == null) return;

            _portName = cfg.PortName ?? _portName;
            _baudRate = Int32.Parse(cfg.BaudeRate) > 0 ? Int32.Parse(cfg.BaudeRate) : _baudRate;
            _parity = cfg.Parity?.Equals("Even", StringComparison.OrdinalIgnoreCase) == true
                         ? Parity.Even : Parity.None;
            _dataBits = cfg.DataBits ?? _dataBits;
            _writeTOms = cfg.SerialWriteTimeOut ?? _writeTOms;
            _readTOms = cfg.SerialReadTimeOut ?? _readTOms;
        }

        /*───────────────────────────────────────────────────────────────────*/
        public void Dispose()
        {
            _cts.Cancel();
            try { _backgroundTask.Wait(); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
