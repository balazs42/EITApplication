using System.Text.Json.Serialization;

namespace Utility.Classes
{
    // Descirbes the Raw\config.json file structure that will be loaded for the ESP32Communicator
    public class SerialPortConfiguration
    {
        [JsonPropertyName("PortName")]
        public string? PortName { get; set; }

        [JsonPropertyName("BaudeRate")]
        public string? BaudeRate { get; set; }

        [JsonPropertyName("Parity")]
        public string? Parity { get; set; }

        [JsonPropertyName("DataBits")]
        public int? DataBits { get; set; }

        public int? SerialWriteTimeOut = 1000;
        public int? SerialReadTimeOut = 5000;
    }
}
