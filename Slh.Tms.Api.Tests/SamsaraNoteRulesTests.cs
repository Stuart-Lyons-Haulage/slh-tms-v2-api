using System.Text;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SamsaraNoteRulesTests
{
    [Fact]
    public void LimitJobNotes_keeps_short_notes_unchanged()
    {
        const string notes = "Job: ABC123 · NWF · 12 pallets\nDelivery window: 06:00-07:00";
        Assert.Equal(notes, SamsaraNoteRules.LimitJobNotes(notes));
    }

    [Fact]
    public void LimitJobNotes_caps_by_utf8_bytes_not_characters()
    {
        // 1,500 middle-dots = 1,500 .NET chars but 3,000 UTF-8 bytes. A character-based
        // cap would wrongly pass this straight through to Samsara, which counts bytes.
        var notes = string.Concat(Enumerable.Repeat("· ", 1500));
        var limited = SamsaraNoteRules.LimitJobNotes(notes);

        Assert.True(
            Encoding.UTF8.GetByteCount(limited) <= SamsaraNoteRules.MaximumJobNoteBytes,
            $"expected <= {SamsaraNoteRules.MaximumJobNoteBytes} bytes, got {Encoding.UTF8.GetByteCount(limited)}");
    }

    [Fact]
    public void LimitJobNotes_does_not_split_a_multibyte_character()
    {
        var notes = new string('·', 2000);
        var limited = SamsaraNoteRules.LimitJobNotes(notes);

        // A clean UTF-8 round-trip proves no half-character was left at the cut point.
        Assert.Equal(limited, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(limited)));
        Assert.DoesNotContain('�', limited);
        Assert.True(Encoding.UTF8.GetByteCount(limited) <= SamsaraNoteRules.MaximumJobNoteBytes);
    }
}
