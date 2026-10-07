using System.Buffers.Binary;
using QFrey.Desktop.Platform;
using Xunit;

namespace QFrey.Tests;

public sealed class WindowsProbeBoundsTests
{
    [Fact]
    public void Processor_topology_parser_counts_efficiency_classes_and_skips_other_records()
    {
        var data = new byte[8 + 48 + 48];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 8);
        AddCore(data.AsSpan(8, 48), 0);
        AddCore(data.AsSpan(56, 48), 2);

        Assert.True(WindowsPlatformProbe.TryParseProcessorCoreEfficiencyClasses(data, out var classes));
        Assert.Equal(1, classes[0]);
        Assert.Equal(1, classes[2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(int.MaxValue)]
    public void Processor_topology_parser_rejects_truncated_or_overflowing_records(int recordSize)
    {
        var data = new byte[48];
        AddCore(data, 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), recordSize);

        Assert.False(WindowsPlatformProbe.TryParseProcessorCoreEfficiencyClasses(data, out _));
    }

    [Fact]
    public void Processor_topology_parser_rejects_group_masks_outside_record()
    {
        var data = new byte[48];
        AddCore(data, 1, groups: 2);
        Assert.False(WindowsPlatformProbe.TryParseProcessorCoreEfficiencyClasses(data, out _));
    }

    [Fact]
    public void Seek_penalty_parser_checks_the_returned_size_field()
    {
        var descriptor = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0, 4), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4, 4), 9);
        descriptor[8] = 1;

        Assert.True(WindowsPlatformProbe.TryReadSeekPenalty(descriptor, out var incursPenalty));
        Assert.True(incursPenalty);

        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4, 4), 8);
        Assert.False(WindowsPlatformProbe.TryReadSeekPenalty(descriptor, out _));
    }

    private static void AddCore(Span<byte> record, byte efficiencyClass, ushort groups = 1)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record[..4], 0);
        BinaryPrimitives.WriteInt32LittleEndian(record.Slice(4, 4), record.Length);
        record[9] = efficiencyClass;
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(30, 2), groups);
    }
}
