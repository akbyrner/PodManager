namespace PodManager.Core;

/// <summary>Writes valid, silent MP3 files (for tests and the demo library).</summary>
public static class SilentMp3
{
    // MPEG-1 Layer III, 128 kbps, 44.1 kHz, joint stereo; 144 * 128000 / 44100 = 417 bytes per frame.
    // All-zero side info decodes as silence.
    private static readonly byte[] Frame = CreateFrame();

    private static byte[] CreateFrame()
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x64;
        return frame;
    }

    public static void Write(string path, double seconds = 1)
    {
        var frames = (int)Math.Ceiling(seconds * 44100 / 1152.0);
        using var stream = File.Create(path);
        for (var i = 0; i < frames; i++) stream.Write(Frame);
    }
}
