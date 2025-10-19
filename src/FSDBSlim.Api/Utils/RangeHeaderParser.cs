namespace FSDBSlim.Utils;

using System;
using System.Linq;
using Microsoft.Net.Http.Headers;

public static class RangeHeaderParser
{
    public static (long From, long To)? ParseSingleRange(string? rangeHeaderValue, long resourceLength)
    {
        if (string.IsNullOrEmpty(rangeHeaderValue))
        {
            return null;
        }

        if (!RangeHeaderValue.TryParse(rangeHeaderValue, out var rangeHeader) || rangeHeader.Unit != "bytes")
        {
            return null;
        }

        var range = rangeHeader.Ranges.SingleOrDefault();
        if (range is null)
        {
            return null;
        }

        long start;
        long end;

        if (range.From.HasValue)
        {
            start = range.From.Value;
            end = range.To ?? (resourceLength - 1);
        }
        else
        {
            if (!range.To.HasValue || range.To.Value <= 0)
            {
                return null;
            }

            var suffixLength = Math.Min(range.To.Value, resourceLength);
            start = resourceLength - suffixLength;
            end = resourceLength - 1;
        }

        if (start < 0 || end < start || end >= resourceLength)
        {
            return null;
        }

        return (start, end);
    }
}
