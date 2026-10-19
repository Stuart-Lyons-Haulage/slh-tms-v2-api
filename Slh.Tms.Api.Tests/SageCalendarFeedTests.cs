using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SageCalendarFeedTests
{
    private const string Ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261019\r\nDTEND;VALUE=DATE:20261024\r\nSUMMARY:Jennifer Banks - Holiday\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261020\r\nDTEND;VALUE=DATE:20261021\r\nSUMMARY:Ben Joshua Madge - Sick\r\n  leave\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261022\r\nDTEND;VALUE=DATE:20261023\r\nSUMMARY:Barry King - Non-working day\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261019\r\nDTEND;VALUE=DATE:20261020\r\nSUMMARY:Someone Else - Holiday\r\nEND:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    [Fact]
    public void Leave_and_non_working_days_are_matched_to_drivers_by_name_in_either_order()
    {
        var banks = Guid.NewGuid(); var madge = Guid.NewGuid(); var king = Guid.NewGuid();
        var drivers = new List<(Guid, string)> { (banks, "Banks Jennifer"), (madge, "Ben Madge"), (king, "Barry King") };

        var snapshot = SageCalendarFeed.Build(SageCalendarFeed.ParseEvents(Ics), drivers, new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(4, snapshot.Events);
        Assert.Equal(3, snapshot.Matched);
        Assert.Equal(5, snapshot.Days.Count(day => day.DriverId == banks && day.Kind == "Leave"));
        var sick = Assert.Single(snapshot.Days, day => day.DriverId == madge);
        Assert.Equal(new DateOnly(2026, 10, 20), sick.Date);
        Assert.Equal("Sick leave", sick.Type);
        Assert.Equal("NonWorking", Assert.Single(snapshot.Days, day => day.DriverId == king).Kind);
        Assert.Contains("Someone Else", snapshot.Unmatched);
    }

    [Fact]
    public void Real_sage_titles_birthdays_holidays_half_days_pending_and_archived()
    {
        var ezard = Guid.NewGuid(); var morgan = Guid.NewGuid(); var gavin = Guid.NewGuid(); var gone = Guid.NewGuid();
        var ics = "BEGIN:VCALENDAR\r\n" +
            Event("20261019", "20261020", "Robert Ezard - Rest Day, Full day") +
            Event("20261019", "20261020", "John Henry Morgan - Annual Leave WP, First part of the day,") +
            Event("20261021", "20261022", "Robert Ezard - Birthday") +
            Event("20261225", "20261226", "Christmas Day") +
            Event("20261022", "20261023", "PENDING APPROVAL - Gavin Tullet - Annual Leave ES, Full day") +
            Event("20261020", "20261021", "Old Driver - Rest Day, Full day") +
            "END:VCALENDAR\r\n";
        var snapshot = SageCalendarFeed.Build(SageCalendarFeed.ParseEvents(ics),
            [(ezard, "Robert Ezard"), (morgan, "John Morgan"), (gavin, "Gavin Tullett")],
            new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), [(gone, "Old Driver")]);

        Assert.Equal("Rest Day", Assert.Single(snapshot.Days, day => day.DriverId == ezard).Type);
        Assert.Equal("NonWorking", Assert.Single(snapshot.Days, day => day.DriverId == ezard).Kind);
        Assert.Equal("Annual Leave WP (morning)", Assert.Single(snapshot.Days, day => day.DriverId == morgan).Type);
        Assert.Equal("Annual Leave ES (pending approval)", Assert.Single(snapshot.Days, day => day.DriverId == gavin).Type);
        Assert.Equal(2, snapshot.Ignored);
        Assert.Empty(snapshot.Unmatched);
        Assert.Contains("Old Driver", snapshot.ArchivedDrivers!);
    }

    private static string Event(string start, string end, string summary) =>
        $"BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:{start}\r\nDTEND;VALUE=DATE:{end}\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\n";

    [Fact]
    public void Only_sage_hr_calendar_links_are_accepted()
    {
        Assert.Equal("https://x.sage.hr/calendar/sync/abc", SageCalendarFeed.Normalise("webcal://x.sage.hr/calendar/sync/abc"));
        Assert.Null(SageCalendarFeed.Normalise("https://example.com/calendar/sync/abc"));
    }
}
