using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Architector.Editor.Agents
{
    public sealed class ArchitectorAgentResult
    {
        private ArchitectorAgentResult(bool success, string message, string code, object data)
        {
            Success = success;
            Message = message;
            Code = code;
            Data = data;
        }

        [JsonProperty("success")]
        public bool Success { get; }

        [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)]
        public string Message { get; }

        [JsonProperty("code", NullValueHandling = NullValueHandling.Ignore)]
        public string Code { get; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error => Code;

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public object Data { get; }

        public static ArchitectorAgentResult Ok(string message, object data = null) => new(true, message, null, data);
        public static ArchitectorAgentResult Fail(string code, object data = null) => new(false, null, code, data);
        public string ToJson() => JObject.FromObject(this).ToString(Formatting.None);
    }
}
