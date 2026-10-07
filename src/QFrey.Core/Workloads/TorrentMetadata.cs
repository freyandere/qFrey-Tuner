using System.Security.Cryptography;
using System.Text;

namespace QFrey.Core.Workloads;

public sealed record TorrentMetadataInfo(string Name, long TotalBytes, string V1InfoHash);

/// <summary>Strict, bounded parser for the v1 single-file torrent metadata used by workload tests.</summary>
public static class TorrentMetadata
{
    public const int MaximumBytes = 2_000_000;
    public const int MaximumDepth = 16;

    public static TorrentMetadataInfo Parse(ReadOnlySpan<byte> raw, string expectedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        if (raw.Length == 0 || raw.Length > MaximumBytes)
            throw new FormatException("TORRENT_METADATA_SIZE");

        var parser = new Parser(raw.ToArray());
        var root = parser.ReadValue(0);
        if (!parser.AtEnd || root is not Dictionary<byte[], object?> top ||
            !TryGet(top, "info"u8, out var infoValue) || infoValue is not Dictionary<byte[], object?> info)
            throw new FormatException("TORRENT_METADATA_ROOT");

        if (TryGet(info, "meta version"u8, out _))
            throw new NotSupportedException("TORRENT_V2_UNSUPPORTED");
        if (!TryGet(info, "name"u8, out var nameValue) || nameValue is not byte[] nameBytes ||
            !TryGet(info, "length"u8, out var lengthValue) || lengthValue is not long length || length < 0 ||
            TryGet(info, "files"u8, out _))
            throw new FormatException("TORRENT_NOT_SINGLE_FILE_V1");

        string name;
        try { name = new UTF8Encoding(false, true).GetString(nameBytes); }
        catch (DecoderFallbackException) { throw new FormatException("TORRENT_NAME_ENCODING"); }
        if (!IsSafeFileName(name) || !string.Equals(name, expectedName, StringComparison.Ordinal))
            throw new FormatException("TORRENT_NAME_MISMATCH");

        var infoSlice = raw[parser.InfoStart..parser.InfoEnd];
        return new TorrentMetadataInfo(name, length, Convert.ToHexStringLower(SHA1.HashData(infoSlice)));
    }

    private static bool IsSafeFileName(string value) => value.Length > 0 && value is not "." and not ".." &&
        value.IndexOfAny(['/', '\\', ':', '\0']) < 0 && !value.Any(char.IsControl);

    private static bool TryGet(Dictionary<byte[], object?> dictionary, ReadOnlySpan<byte> key, out object? value)
    {
        foreach (var pair in dictionary)
            if (pair.Key.AsSpan().SequenceEqual(key)) { value = pair.Value; return true; }
        value = null;
        return false;
    }

    private sealed class Parser(byte[] data)
    {
        private int position;
        public bool AtEnd => position == data.Length;
        public int InfoStart { get; private set; } = -1;
        public int InfoEnd { get; private set; } = -1;

        public object? ReadValue(int depth)
        {
            if (depth > MaximumDepth || position >= data.Length) throw new FormatException("TORRENT_METADATA_DEPTH_OR_TRUNCATED");
            return data[position] switch
            {
                (byte)'i' => ReadInteger(),
                (byte)'l' => ReadList(depth),
                (byte)'d' => ReadDictionary(depth),
                >= (byte)'0' and <= (byte)'9' => ReadBytes(),
                _ => throw new FormatException("TORRENT_METADATA_TOKEN")
            };
        }

        private long ReadInteger()
        {
            position++;
            var start = position;
            while (position < data.Length && data[position] != (byte)'e') position++;
            if (position == data.Length || position == start) throw new FormatException("TORRENT_INTEGER");
            var span = data.AsSpan(start, position - start);
            if (span.Length > 1 && (span[0] == (byte)'0' || span[0] == (byte)'-' && span[1] == (byte)'0') ||
                span[0] == (byte)'+' || !long.TryParse(Encoding.ASCII.GetString(span), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new FormatException("TORRENT_INTEGER");
            position++;
            return value;
        }

        private byte[] ReadBytes()
        {
            var start = position;
            while (position < data.Length && data[position] != (byte)':')
            {
                if (data[position] is < (byte)'0' or > (byte)'9') throw new FormatException("TORRENT_STRING_LENGTH");
                position++;
            }
            if (position == data.Length || position == start || position - start > 1 && data[start] == (byte)'0' ||
                !int.TryParse(Encoding.ASCII.GetString(data, start, position - start), out var size))
                throw new FormatException("TORRENT_STRING_LENGTH");
            position++;
            if (size < 0 || size > data.Length - position) throw new FormatException("TORRENT_STRING_TRUNCATED");
            var result = data.AsSpan(position, size).ToArray();
            position += size;
            return result;
        }

        private List<object?> ReadList(int depth)
        {
            position++;
            var result = new List<object?>();
            while (ReadTerminator() != (byte)'e') result.Add(ReadValue(depth + 1));
            position++;
            return result;
        }

        private Dictionary<byte[], object?> ReadDictionary(int depth)
        {
            position++;
            var result = new Dictionary<byte[], object?>(ByteArrayComparer.Instance);
            byte[]? previous = null;
            while (ReadTerminator() != (byte)'e')
            {
                if (data[position] is < (byte)'0' or > (byte)'9') throw new FormatException("TORRENT_DICTIONARY_KEY");
                var key = ReadBytes();
                if (previous is not null && ByteArrayComparer.Instance.Compare(previous, key) >= 0)
                    throw new FormatException("TORRENT_DUPLICATE_OR_UNSORTED_KEY");
                previous = key;
                var start = position;
                var value = ReadValue(depth + 1);
                result.Add(key, value);
                if (depth == 0 && key.AsSpan().SequenceEqual("info"u8))
                { InfoStart = start; InfoEnd = position; }
            }
            position++;
            return result;
        }

        private byte ReadTerminator()
        {
            if (position >= data.Length) throw new FormatException("TORRENT_CONTAINER_TRUNCATED");
            return data[position];
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y) || x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] value) { var hash = new HashCode(); foreach (var item in value) hash.Add(item); return hash.ToHashCode(); }
        public int Compare(byte[]? x, byte[]? y) => x is null ? y is null ? 0 : -1 : y is null ? 1 : x.AsSpan().SequenceCompareTo(y);
    }
}
