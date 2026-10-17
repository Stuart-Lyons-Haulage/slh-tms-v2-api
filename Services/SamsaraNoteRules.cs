namespace Slh.Tms.Api.Services;

public static class SamsaraNoteRules
{
    public const int MaximumJobNoteLength = 2000;

    public static string LimitJobNotes(string notes)
    {
        if (notes.Length <= MaximumJobNoteLength)
            return notes;

        const string suffix = "\n[Additional job notes omitted by SLH TMS: Samsara limit is 2,000 characters]";
        var retainedLength = MaximumJobNoteLength - suffix.Length;
        return notes[..retainedLength].TrimEnd() + suffix;
    }
}
