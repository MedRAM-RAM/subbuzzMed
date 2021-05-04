using System;

namespace subbuzz.Parser.Qualities
{
    public class QualityModel : IEquatable<QualityModel>
    {
        public Quality Quality { get; set; }

        public Revision Revision { get; set; }

        public string HardcodedSubs { get; set; }

        public string AudioCodec { get; set; }
        public string VideoCodec { get; set; }

        public QualityDetectionSource SourceDetectionSource { get; set; }

        public QualityDetectionSource ResolutionDetectionSource { get; set; }

        public QualityDetectionSource ModifierDetectionSource { get; set; }

        public QualityDetectionSource RevisionDetectionSource { get; set; }

        public QualityModel()
            : this(Quality.Unknown, new Revision())
        {
        }

        public QualityModel(Quality quality, Revision revision = null)
        {
            Quality = quality;
            Revision = revision ?? new Revision();
        }

        public override string ToString()
        {
            return string.Format("{0} {1}", Quality, Revision);
        }

        public override int GetHashCode()
        {
            // Overflow is fine, just wrap
            unchecked
            {
                int hash = 17;
                hash = (hash * 23) + Revision.GetHashCode();
                hash = (hash * 23) + Quality.GetHashCode();
                return hash;
            }
        }

        public bool Equals(QualityModel other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other.Quality.Id.Equals(Quality.Id) && other.Revision.Equals(Revision);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return Equals(obj as QualityModel);
        }

        public static bool operator ==(QualityModel left, QualityModel right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(QualityModel left, QualityModel right)
        {
            return !Equals(left, right);
        }
    }
}
