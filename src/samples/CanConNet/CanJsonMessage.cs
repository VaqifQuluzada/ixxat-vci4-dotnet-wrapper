using System.Text.Json;

[Serializable]
public class CanJsonMessage
{
    public long Time { get; set; }
    public uint ID { get; set; }
    public int DLC { get; set; }
    public byte[]? Data { get; set; }

    // Serialize to JSON
    public string ToJson()
    {
        string jsonString = JsonSerializer.Serialize(this);

        return jsonString;
    }

    // Deserialize from JSON
    public static CanJsonMessage FromJson(string json)
    {
        CanJsonMessage canJsonMessage = JsonSerializer.Deserialize<CanJsonMessage>(json);

        return canJsonMessage;
    }
}
