namespace Patchboard.Services;

/// <summary>
/// Decoded clips held in memory, under a fixed budget, least recently used evicted first.
///
/// Decoding once and keeping the result is what makes a second press instant, but an
/// unbounded cache is not a cache, it is a leak with a nice name. Measured against the
/// real library this replaced: 203 playable clips, 150 minutes of audio, which at 48kHz
/// stereo float is 3.3GB resident if every button is pressed once, and none of it ever
/// released. The median clip is under ten seconds, so a small budget holds everything
/// anyone actually presses in a session and the long tail costs one re-decode.
///
/// Not thread safe. It is touched only from the UI thread, which is where every press,
/// preview and hotkey arrives.
/// </summary>
public sealed class SoundCache
{
    /// <summary>
    /// Resident ceiling. 256MB is roughly eleven minutes of audio, comfortably more than
    /// a session's worth of clips at the observed median length, and small enough that
    /// the app never becomes the reason a game stutters.
    /// </summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    private readonly long _budgetBytes;

    /// <summary>Path to entry. Case insensitive, because Windows paths are.</summary>
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most recently used at the front, so eviction always takes from the back.</summary>
    private readonly LinkedList<Entry> _order = new();

    private long _bytes;

    public SoundCache(long budgetBytes = DefaultBudgetBytes) => _budgetBytes = Math.Max(budgetBytes, 1);

    private sealed record Entry(string Path, CachedSound Sound, long Bytes);

    /// <summary>Resident bytes right now.</summary>
    public long Bytes => _bytes;

    /// <summary>Clips currently held.</summary>
    public int Count => _entries.Count;

    /// <summary>How many times a clip had to be decoded again after being evicted.</summary>
    public int Evictions { get; private set; }

    /// <summary>
    /// Return the decoded clip for <paramref name="path"/>, decoding it if it is not
    /// resident. Throws whatever <see cref="CachedSound.Load"/> throws, so the caller can
    /// still tell the user why a file will not play.
    /// </summary>
    public CachedSound GetOrLoad(string path)
    {
        if (_entries.TryGetValue(path, out var existing))
        {
            // Touch it, so the clips he actually uses are the ones that survive.
            _order.Remove(existing);
            _order.AddFirst(existing);
            return existing.Value.Sound;
        }

        var sound = CachedSound.Load(path);
        var bytes = (long)sound.AudioData.Length * sizeof(float);

        // A single clip larger than the whole budget is handed back without being stored,
        // rather than evicting everything else to make room for something that then has
        // to be evicted itself. It still plays; it just decodes again next time.
        if (bytes > _budgetBytes) return sound;

        var node = _order.AddFirst(new Entry(path, sound, bytes));
        _entries[path] = node;
        _bytes += bytes;

        Trim();
        return sound;
    }

    /// <summary>True when this clip is decoded and resident right now.</summary>
    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <summary>
    /// Return an already decoded clip without touching the disk.
    ///
    /// This is the fast path a soundboard lives on. It matters that it is separate from
    /// <see cref="GetOrLoad"/>: decoding now happens on a background thread, and going
    /// through a thread hop for a clip already in memory would put a scheduler round trip
    /// between the click and the sound.
    /// </summary>
    public bool TryGet(string path, out CachedSound sound)
    {
        if (_entries.TryGetValue(path, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            sound = node.Value.Sound;
            return true;
        }

        sound = null!;
        return false;
    }

    /// <summary>
    /// Take a clip decoded elsewhere, typically on a background thread, into the cache.
    /// Same budget rules as <see cref="GetOrLoad"/>.
    /// </summary>
    public void Add(string path, CachedSound sound)
    {
        if (_entries.ContainsKey(path)) return;

        var bytes = (long)sound.AudioData.Length * sizeof(float);
        if (bytes > _budgetBytes) return;

        var node = _order.AddFirst(new Entry(path, sound, bytes));
        _entries[path] = node;
        _bytes += bytes;

        Trim();
    }

    /// <summary>Forget one clip, so a file that changed on disk is decoded again.</summary>
    public void Remove(string path)
    {
        if (!_entries.Remove(path, out var node)) return;
        _bytes -= node.Value.Bytes;
        _order.Remove(node);
    }

    public void Clear()
    {
        _entries.Clear();
        _order.Clear();
        _bytes = 0;
    }

    private void Trim()
    {
        while (_bytes > _budgetBytes && _order.Last is { } oldest)
        {
            _entries.Remove(oldest.Value.Path);
            _order.RemoveLast();
            _bytes -= oldest.Value.Bytes;
            Evictions++;
        }
    }
}
