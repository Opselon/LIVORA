namespace LIVORA.Application.Abstractions;

/// <summary>
/// Locale-aware presentation formatting. Domain data stays machine-readable (numeric/DateTime);
/// this layer owns Gregorian vs Jalali display, digits, durations and relative labels.
/// </summary>
public interface IFormatService
{
    /// <summary>"Thursday, September 11" / "پنجشنبه ۲۰ شهریور ۱۴۰۵"</summary>
    string LongDate(DateTime date);

    /// <summary>Short month-day label for compact rows.</summary>
    string ShortDate(DateTime date);

    /// <summary>Time-of-day only (e.g. 23:30).</summary>
    string Time(TimeSpan time);

    /// <summary>"7h 40m" / "۷ ساعت و ۴۰ دقیقه"</summary>
    string Duration(double hours, double minutes);

    /// <summary>Whole minutes to a duration label.</summary>
    string DurationFromMinutes(int totalMinutes);

    /// <summary>Locale digits ("1,200" / "۱٬۲۰۰").</summary>
    string Number(long value);

    /// <summary>0..1 fraction as percent with locale digits and percent sign.</summary>
    string Percent(double fraction);
}
