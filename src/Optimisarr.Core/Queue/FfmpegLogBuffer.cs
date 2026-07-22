using System.Text;

namespace Optimisarr.Core.Queue;

/// <summary>
/// Accumulates the lines worth keeping from an ffmpeg run — its stream mapping, warnings, and the
/// error that ended it — bounded so a long run can never grow without limit. Machine-readable
/// progress is carried on a separate pipe, leaving every stderr line available here. The first
/// <c>headLimit</c> and last <c>tailLimit</c> lines are kept and the middle is elided, so both the
/// command/stream setup and the final failure survive. Pure and unit tested.
/// </summary>
public sealed class FfmpegLogBuffer(int headLimit = 200, int tailLimit = 200)
{
    private readonly List<string> _head = new();
    private readonly Queue<string> _tail = new();
    private int _total;

    public void Append(string line)
    {
        _total++;
        if (_head.Count < headLimit)
        {
            _head.Add(line);
            return;
        }

        _tail.Enqueue(line);
        if (_tail.Count > tailLimit)
        {
            _tail.Dequeue();
        }
    }

    /// <summary>The captured log, or <c>null</c> when nothing was appended.</summary>
    public string? ToLog()
    {
        if (_total == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendJoin('\n', _head);

        var elided = _total - _head.Count - _tail.Count;
        if (elided > 0)
        {
            builder.Append('\n').Append("… ").Append(elided).Append(" lines elided …");
        }

        if (_tail.Count > 0)
        {
            builder.Append('\n').AppendJoin('\n', _tail);
        }

        return builder.ToString();
    }
}
