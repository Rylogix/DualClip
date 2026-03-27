using DualClip.Core.Models;
using NAudio.CoreAudioApi;

namespace DualClip.Capture;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceDescriptor> GetMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

        return devices
            .Select(device => new AudioDeviceDescriptor
            {
                Id = device.ID,
                FriendlyName = device.FriendlyName,
            })
            .OrderBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AudioDeviceDescriptor? GetDefaultMicrophone()
    {
        using var enumerator = new MMDeviceEnumerator();

        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return new AudioDeviceDescriptor
            {
                Id = device.ID,
                FriendlyName = device.FriendlyName,
            };
        }
        catch
        {
            return null;
        }
    }
}
