namespace WinWaechter.Core.Update;

internal static class BoundedRead
{
    // Copies at most max bytes; throws if more arrive. Reports progress in 0..1 when total is known.
    public static async Task CopyAsync(Stream src, Stream dst, long max, long? total, IProgress<double>? p, CancellationToken ct)
    {
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            done += n;
            if (done > max) throw new UpdateDownloadException("Die Datei ist größer als erlaubt.");
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            if (p is not null && total is > 0) p.Report(Math.Min(1.0, (double)done / total.Value));
        }
    }
}
