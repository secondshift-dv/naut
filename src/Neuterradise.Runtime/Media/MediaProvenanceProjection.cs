namespace Neuterradise.App.Media;

public static class MediaProvenanceProjection
{
    public static IReadOnlyList<MediaFileDetailGroup> Build(IReadOnlyList<MediaFileDetailGroup> details)
    {
        var sections = new Dictionary<string, List<MediaFileDetailRow>>(StringComparer.Ordinal)
        {
            ["Who"] = [], ["Where"] = [], ["When"] = [], ["How"] = [],
        };
        foreach (var group in details)
        {
            foreach (var row in group.Rows.Where(IsUseful))
            {
                var section = group.GroupName switch
                {
                    "Source Evidence" or "Capture Location" => "Where",
                    "Dates" when row.Label is "Captured" or "Recorded" => "When",
                    "Camera" => "How",
                    "Metadata" when row.Label is "Location" => "Where",
                    "Metadata" when row.Label is "Device Make" or "Device Model" or "Software" or "Encoder" => "How",
                    "Raw Metadata" => ClassifyEmbeddedTag(row.Label),
                    _ => null,
                };
                if (section is not null) sections[section].Add(row);
            }
        }

        return sections.Where(pair => pair.Value.Count > 0)
            .Select(pair => new MediaFileDetailGroup(pair.Key,
                pair.Value.DistinctBy(row => $"{row.Label}\u001f{row.Value}", StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    private static bool IsUseful(MediaFileDetailRow row) =>
        !string.IsNullOrWhiteSpace(row.Value) && row.Value is not "—" and not "-";

    private static string? ClassifyEmbeddedTag(string label)
    {
        // Classify embedded evidence only. Catalog owners, filesystem dates and managed paths are not provenance.
        var key = new string(label.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return key switch
        {
            "artist" or "author" or "creator" or "copyright" or "photographer" or "byline"
                or "ifd0artist" or "ifd0copyright" or "dccreator" or "dcrights"
                or "exifcameraownername" or "cameraownername" or "comapplequicktimeauthor" => "Who",
            "source" or "sourceurl" or "originalurl" or "webstatement" or "dcsource"
                or "location" or "locationeng" or "gpslatitude" or "gpslongitude"
                or "comapplequicktimelocationiso6709" or "comapplequicktimelocationname" => "Where",
            "datetimeoriginal" or "exifdatetimeoriginal" or "datetimedigitized" or "exifdatetimedigitized"
                or "creationtime" or "createdate" or "recordingtime" or "date"
                or "comapplequicktimecreationdate" or "xmpcreatedate" or "xmpdatetimeoriginal"
                or "ifd0datetime" => "When",
            "make" or "model" or "ifd0make" or "ifd0model" or "software" or "ifd0software"
                or "encoder" or "lensmodel" or "exiflensmodel" or "exifexposuretime"
                or "exiffnumber" or "exifiso" or "exifexposureprogram" or "exifexposurebias"
                or "exifflash" or "exiffocallength" or "comapplequicktimemake"
                or "comapplequicktimemodel" or "comapplequicktimesoftware" or "generator" => "How",
            _ => null,
        };
    }
}
