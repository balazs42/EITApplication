using System.Text.Json.Serialization;

namespace Utility.Classes.Configurations
{
    // Describes the Raw\config.json file structure that will be loaded for the ESP32Communicator
    public class SerialPortConfiguration
    {
        [JsonPropertyName("PortName")]
        public string? PortName { get; set; }

        [JsonPropertyName("BaudRate")]
        public string? BaudRate { get; set; }

        [JsonPropertyName("Parity")]
        public string? Parity { get; set; }

        [JsonPropertyName("DataBits")]
        public int? DataBits { get; set; }

        [JsonPropertyName("SerialWriteTimeOut")]
        public int? SerialWriteTimeOut { get; set; } = 1000;

        [JsonPropertyName("SerialReadTimeOut")]
        public int? SerialReadTimeOut { get; set; } = 5000;
    }
}
