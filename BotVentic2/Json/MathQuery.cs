using Newtonsoft.Json;

namespace BotVentic2.Json
{
    public class MathQuery
    {
        [JsonProperty("status")] public int Status { get; set; }
        [JsonProperty("response")] public string Response { get; set; }
    }
}
