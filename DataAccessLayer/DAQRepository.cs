using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using Utility.Classes.Configurations;
using Utility.Classes.Measurement;

namespace DataAccessLayer
{
    // Example measurement data V1:
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
        private readonly Task? _backgroundTask;

        // --- Event To Invoke When a Measurement is Received ---
        public event EventHandler<EITMeasurement>? MeasurementReceived;

        private const int _frameLength = 16;
        private const char _dataSeparator = ';';


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

        public EITMeasurement GetEITMeasurement()
            => ReadBlock() ?? throw new InvalidOperationException("No data received");

        private EITMeasurement? ReadBlock()
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
                List<double[]> frames = [];
                for (int row = 0; row < _frameLength; ++row)
                {
                    line = port.ReadLine();

                    // Convert read lines to 
                    var entries = line.Split(_dataSeparator);
                    double[] nums = new double[_frameLength];
                    for (int i = 0; i < nums.Length; i++)
                        nums[i] = Convert.ToDouble(entries[i]);

                    frames.Add(nums);
                }

                /*–– consume end marker –––––––––––––––––––––––––––––––––––*/
                line = port.ReadLine().Trim();
                if (!line.StartsWith("End of measurements", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Missing end marker.");

                return new EITMeasurement(frames);
            }
            catch (TimeoutException) { return null; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Serial error: {ex.Message}");
                return null;
            }
        }

        /*───────────────────────────────────────────────────────────────────
         *  Persist measurement to disk (jagged JSON)
         *──────────────────────────────────────────────────────────────────*/
        private static async Task SaveToJsonAsync(EITMeasurement m, CancellationToken ct)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Measurements");
            Directory.CreateDirectory(dir);

            string file = Path.Combine(dir,
                $"EIT_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");

            var jagged = new double[16][];
            for (int i = 0; i < 16; ++i)
            {
                //jagged[i] = new double[16];
                //SixteenElectrodeMeasurement measurement = m.GetMeasurement(i);
                //
                //for (int j = 0; j < 16; ++j)
                //    jagged[i][j] = measurement.Measurement[j];
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
            var cfg = JsonSerializer.Deserialize<SerialPortConfiguration>(json, opt) ?? throw new NullReferenceException("Configuration loading failed, check config.json file and calling code!");
            _portName = cfg.PortName ?? _portName;
            if (int.TryParse(cfg.BaudRate, out var parsedBaud) && parsedBaud > 0)
                _baudRate = parsedBaud;
            _parity = cfg.Parity?.Equals("Even", StringComparison.OrdinalIgnoreCase) == true
                         ? Parity.Even : Parity.None;
            _dataBits = cfg.DataBits ?? _dataBits;
            _writeTOms = cfg.SerialWriteTimeOut ?? _writeTOms;
            _readTOms = cfg.SerialReadTimeOut ?? _readTOms;
        }

        // TODO: do something with these like saving and loading from json files

        public void SaveEITMeasurement()
        {
        
        }

        public void LoadEITMeasurement(DateTime dateTime)
        {
        
        }

        public void LoadEITMeasurement(int id)
        {
        
        }

        public void DeleteEITMeasurement(int id)
        {
        
        }

        public void DeleteEITMeasurement(DateTime dateTime)
        {

        }




        /*───────────────────────────────────────────────────────────────────*/
        public void Dispose()
        {
            _cts.Cancel();
            try 
            {
                if(_backgroundTask != null)
                    _backgroundTask.Wait(); 
            } 
            catch { /* ignore */ }
            _cts.Dispose();
        }

        private static Dictionary<Int16, string> _errorCodes = new()
        {
            {0, "No error detected!"},
            {10, "Unknown error occured!"},

            // --- Multiplexer Circuit Related Errors ---
            {200, "Multiplexer errror occured!"},
            {201, "Multiplexer address setting error!"},
            {202, "Multiplexer enabple pin setting error!"},
            {203, "Multiplexer direction setting error!"},
            {204, "Multiplexer excitation setting out of range!"},

            // --- ADC Related Errors ---
            {300, "ADC error occured!"},
            {301, "ADC reset error occured!"},
            {302, "ADC start error occured!"},
            {303, "ADC invalid register address!"},
            {305, "No electrode or ADC overdrive detected!"},
            {306, "ADC Device id error!"},
            {307, "ADC Analog supply low-voltage!"},
            {308, "ADC Power on reset set."},
            {309, "ADC SPI CRC error."},
            {310, "ADC Register map error."},
            {311, "ADC internal error."},
            {312, "ADC register address error."},
            {313, "ADC SCLK count error."},
            {314, "ADC invalid channel configuration."},
            {315, "ADC status enable failed."},
            {316, "ADC Resolution setting failed!"},
            {317, "ADC Oscillator mode setting failed!"},
            {318, "ADC clock divider setting failed!"},
            {319, "ADC DCLK divider setting failed!"},
            {320, "ADC invalid clock divider value."},
            {321, "ADC invalid DCLK divider value."},
            {322, "ADC daisy chain setting failed!"},
            {323, "ADC Speed mode setting failed!"},
            {324, "ADC Speed mode value error."},
            {325, "ADC Start mode setting failed!"},
            {3260, "ADC Over sampling Ratio (OSR) CH0 setting failed!"},
            {3261, "ADC Over sampling Ratio (OSR) CH1 setting failed!"},
            {3262, "ADC Over sampling Ratio (OSR) CH2 setting failed!"},
            {3263, "ADC Over sampling Ratio (OSR) CH3 setting failed!"},
            {3264, "ADC Over sampling Ratio (OSR) CH4 setting failed!"},
            {3265, "ADC Over sampling Ratio (OSR) CH5 setting failed!"},
            {3266, "ADC Over sampling Ratio (OSR) CH6 setting failed!"},
            {3267, "ADC Over sampling Ratio (OSR) CH7 setting failed!"},
            {327, "ADC general config3 configuration failed!"},
            {328, "ADC Data port configuration failed!"},

            // --- Current Sense Related Errors ---
            {400, "Current sense error occured!"},

            // --- Serial Command Task Related Errors ---
            {50, "Serial command errror occured!"}
        };
    }
}
