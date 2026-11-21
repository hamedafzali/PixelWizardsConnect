namespace PixelWizard.Media.H264;

public static class AnnexB
{
    /// <summary>The NAL unit types (header byte &amp; 0x1F) present in an Annex-B buffer.</summary>
    public static List<int> NalTypes(ReadOnlySpan<byte> data)
    {
        var types = new List<int>();
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                types.Add(data[i + 3] & 0x1F);
                i += 2;
            }
        }
        return types;
    }

    public static bool HasSpsPpsIdr(ReadOnlySpan<byte> data)
    {
        var t = NalTypes(data);
        return t.Contains(7) && t.Contains(8) && t.Contains(5);
    }
}
