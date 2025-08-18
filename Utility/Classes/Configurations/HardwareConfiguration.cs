using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Utility.Classes.Configurations
{
    public class HardwareConfiguration
    {
        [JsonPropertyName("HardwareVersion")]
        public string? HardwareVersion { get; set; } = "V1";

        [JsonPropertyName("ElectrodeNum")]
        public int? ElectrodeNum { get; set; } = 16;

        [JsonPropertyName("ChannelOffsets")]
        public List<double>? ChannelOffsets { get; set; } = [];

        [JsonPropertyName("ChannelGains")]
        public List<double>? ChannelGains { get; set; } = [];

        public bool Initialized { get; set; } = false;    

        public async void ReadConfigJsonFile(string jsonFile = "HardwareConfiguration.json")
        {
            if (!File.Exists(jsonFile))
            {
                Debug.WriteLine("config.json not found – defaults in use.");
                return;
            }

            string json = await File.ReadAllTextAsync(jsonFile);
            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cfg = JsonSerializer.Deserialize<HardwareConfiguration>(json, opt) ?? throw new NullReferenceException("Configuration loading failed, check config.json file and calling code!");

            HardwareVersion = cfg.HardwareVersion;
            ElectrodeNum = cfg.ElectrodeNum;
            ChannelOffsets = cfg.ChannelOffsets;
            ChannelGains = cfg.ChannelGains;
        }

        public  void Initialize(int electrodeNum = 16)
        {
            ReadConfigJsonFile();

            ElectrodeNum = electrodeNum;

            if(ChannelOffsets == null)
                ResetChannelOffsets();
           
            if(ChannelGains == null)
                ResetChannelGains();

            Initialized = true;
        }

        private  void ResetChannelOffsets(double setValue = 1.0)
        {
            if(ChannelOffsets == null)
                ChannelOffsets = new List<double>((int)(ElectrodeNum ?? throw new NullReferenceException()));

            for (int i = 0; i < ElectrodeNum; i++)
                ChannelOffsets[i] = setValue;
        }

        private  void ResetChannelGains(double setValue = 1.0) 
        {
            if (ChannelGains == null)
                ChannelGains = new List<double>((int)(ElectrodeNum ?? throw new NullReferenceException()));

            for (int i = 0; i < ElectrodeNum; i++)
                ChannelGains[i] = setValue;
        }
    }
}
