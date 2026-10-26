namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Rates as in the original Kyoshin report: a status time divided by the time a lamp was on
    /// (Run + Wait + Stop) minus the break time. Power-off time is not part of the base.
    /// Efficiency = Run rate.
    /// </summary>
    public static class ProductionRate
    {
        /// <summary>Base of the rates in seconds: Run + Wait + Stop − break (0 or less = no rate).</summary>
        public static double Base(double greenSec, double yellowSec, double redSec, double breakSec = 0) =>
            greenSec + yellowSec + redSec - breakSec;

        /// <summary><paramref name="partSec"/> as % of the base, 1 decimal; 0 when the base is empty.</summary>
        public static double Percent(double partSec, double greenSec, double yellowSec, double redSec, double breakSec = 0)
        {
            double b = Base(greenSec, yellowSec, redSec, breakSec);
            return b > 0 ? Math.Round(partSec / b * 100, 1) : 0;
        }

        public static double Efficiency(double greenSec, double yellowSec, double redSec, double breakSec = 0) =>
            Percent(greenSec, greenSec, yellowSec, redSec, breakSec);
    }
}
