namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Error names shown to users, as in the original Kyoshin app. Full names on chart headings and
    /// tooltips, short names in table columns and legends. Pages read them in JS as ERROR_NAMES (_Layout).
    /// </summary>
    public static class ErrorNames
    {
        public static string Short(ErrorType type) => type switch
        {
            ErrorType.Out => "Báo thoát",
            ErrorType.Trash => "Báo rác",
            _ => "Sai bước"
        };

        public static string Full(ErrorType type) => type switch
        {
            ErrorType.Out => "Báo thoát Misfeed PS-101T",
            ErrorType.Trash => "Báo rác Die height PS-642/464",
            _ => "Sai bước"
        };

        /// <summary>{ out: { short, full }, trash: {...}, other: {...} } for the pages' scripts.</summary>
        public static object ForScript() => new
        {
            @out = new { @short = Short(ErrorType.Out), full = Full(ErrorType.Out) },
            trash = new { @short = Short(ErrorType.Trash), full = Full(ErrorType.Trash) },
            other = new { @short = Short(ErrorType.Other), full = Full(ErrorType.Other) }
        };
    }
}
