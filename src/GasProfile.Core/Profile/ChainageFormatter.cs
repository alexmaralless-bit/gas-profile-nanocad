using System;
using System.Globalization;

namespace GasProfile.Core.Profile
{
    public static class ChainageFormatter
    {
        public static string Format(double chainage)
        {
            var rounded = Math.Round(chainage, 2, MidpointRounding.AwayFromZero);
            var pk = (int)Math.Floor(rounded / 100.0);
            var meters = rounded - pk * 100.0;
            return string.Format(CultureInfo.InvariantCulture, "ПК{0}+{1:00.00}", pk, meters);
        }
    }
}
