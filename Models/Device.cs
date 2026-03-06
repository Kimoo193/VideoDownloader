namespace VideoDownloader.Models;

public class Device
{
    public int Id { get; set; }
    public string Serial { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string OS { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool IsBlocked { get; set; }
    public int RequestCount { get; set; }  
}