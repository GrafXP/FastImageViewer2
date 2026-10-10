namespace Fiv.Comparing;

/// <summary>
/// Which images of a compare set are being looked at: the active one, and the one it is compared
/// with, which is simply the one that was active before. The modes that show two images show
/// that pair, in the order of the set unless swapped, so that flicking between two images never
/// makes them trade places on screen.
/// </summary>
public sealed class CompareSelection
{
    int _peekKey = -1, _peekActive, _peekOther;
    long _peekStarted;

    public int Count { get; private set; }

    /// <summary>The image the user is at: what the single view shows, what the readouts and file actions refer to.</summary>
    public int Active { get; private set; }

    /// <summary>The image the active one is compared with. Differs from <see cref="Active"/> whenever there are two images.</summary>
    public int Other { get; private set; }

    /// <summary>Shows the pair in reverse order.</summary>
    public bool Swapped { get; set; }

    /// <summary>The two images compared, in the order they are shown: left and right, or below and on top.</summary>
    public (int First, int Second) Pair
    {
        get
        {
            int low = Math.Min(Active, Other), high = Math.Max(Active, Other);
            return Swapped ? (high, low) : (low, high);
        }
    }

    /// <summary>Starts over with a set of <paramref name="count"/> images, at <paramref name="active"/>.</summary>
    public void Reset(int count, int active = 0)
    {
        Count = Math.Max(0, count);
        Active = Count == 0 ? 0 : Math.Clamp(active, 0, Count - 1);
        Other = Count < 2 ? Active : Active == 0 ? 1 : 0;
        Swapped = false;
        _peekKey = -1;
    }

    /// <summary>Goes to an image; the one left becomes the one compared with. False if nothing changed.</summary>
    public bool Select(int index)
    {
        if (index < 0 || index >= Count || index == Active) return false;
        Other = Active;
        Active = index;
        return true;
    }

    /// <summary>Goes forward or back through the set, around the ends.</summary>
    public bool Step(int delta) => Count > 1 && Select(((Active + delta) % Count + Count) % Count);

    /// <summary>Goes to the image compared with: back and forth between the two.</summary>
    public bool Toggle() => Select(Other);

    /// <summary>
    /// A key that stands for an image went down: go to that image, and remember where from, so
    /// that <see cref="Release"/> can go back. False if the image was already the active one.
    /// </summary>
    public bool Press(int key, int index, long timestamp)
    {
        if (_peekKey < 0)
        {
            _peekActive = Active;
            _peekOther = Other;
        }
        _peekKey = key;
        _peekStarted = timestamp;
        return Select(index);
    }

    /// <summary>
    /// The key came up. Held for at least <paramref name="holdTicks"/> it was a peek, and the
    /// selection returns to what it was; released sooner it was a switch, and stays. True if the selection changed.
    /// </summary>
    public bool Release(int key, long timestamp, long holdTicks)
    {
        if (key != _peekKey) return false;
        _peekKey = -1;
        if (timestamp - _peekStarted < holdTicks) return false;
        if (_peekActive >= Count || _peekOther >= Count || (Active == _peekActive && Other == _peekOther)) return false;
        Active = _peekActive;
        Other = _peekOther;
        return true;
    }

    /// <summary>
    /// Forgets a key that is down. For when its release will not be seen (the window lost the
    /// keyboard): the selection stays where the key took it.
    /// </summary>
    public void ForgetPress() => _peekKey = -1;

    /// <summary>An image was appended to the set.</summary>
    public void Added()
    {
        Count++;
        if (Count == 2) Other = 1 - Active;
    }

    /// <summary>The image at <paramref name="index"/> left the set; the ones after it moved up.</summary>
    public void Removed(int index)
    {
        if (index < 0 || index >= Count) return;
        Count--;
        _peekKey = -1;
        if (Count == 0)
        {
            Active = Other = 0;
            return;
        }
        Active = After(Active);
        Other = After(Other);
        if (Other == Active && Count > 1) Other = Active > 0 ? Active - 1 : 1;

        // An image after the removed one moves up; the removed one's place is taken by its successor, or by the new last one.
        int After(int i) => i > index ? i - 1 : Math.Min(i, Count - 1);
    }
}
