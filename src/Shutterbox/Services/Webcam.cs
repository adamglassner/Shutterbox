using Shutterbox.Library;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Shutterbox.Services;

/// <summary>"Photograph wrong guesses": a photo from the PC's camera, encrypted to the Locked album's public key.</summary>
public static class Webcam
{
    public static async Task TakeAndSaveIntruderPhotoAsync()
    {
        try
        {
            var jpeg = await TakePhotoAsync();
            if (jpeg != null) Vault.SaveIntruderPhoto(jpeg);
        }
        catch { /* no camera, or the camera is off in Windows privacy settings */ }
    }

    /// <summary>A JPEG from the default camera, without showing anything; null when there's no camera Shutterbox may use.</summary>
    public static async Task<byte[]> TakePhotoAsync()
    {
        using var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        });
        using var stream = new InMemoryRandomAccessStream();
        await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
