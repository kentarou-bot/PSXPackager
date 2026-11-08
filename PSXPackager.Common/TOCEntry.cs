namespace PSXPackager.Common
{
    public class TOCEntry
    {
        public TrackTypeEnum TrackType { get; set; }
        public int TrackNo { get; set; }
        public int Minutes { get; set; }
        public int Seconds { get; set; }
        public int Frames { get; set; }

        /// <summary>Bytes 3-5. The pregap / index-00 MSF.</summary>
        public int PregapMinutes { get; set; }
        public int PregapSeconds { get; set; }
        public int PregapFrames { get; set; }

        /// <summary>Bytes 7-9. The user-data / index-01 MSF.</summary>
        public int DataMinutes { get; set; }
        public int DataSeconds { get; set; }
        public int DataFrames { get; set; }

        /// <summary>The 10 raw TOC bytes, when read from a PBP.</summary>
        public byte[] RawRecord { get; set; }
    }


}