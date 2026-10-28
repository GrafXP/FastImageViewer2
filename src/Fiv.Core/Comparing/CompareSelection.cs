using System.Numerics;

namespace Fiv.Comparing;

/// <summary>
/// Which images of a compare set are being looked at, and how. The active one is where the user
/// is: what the single view shows, what the readouts and file actions refer to. The one it is
/// compared with is simply the one that was active before; blinking goes between the two.
///
/// The views that show several images at once take in those that are shown, all of them unless
/// some are taken out, in the order of the set beginning at the reference: the image the others
/// are compared with and aligned to. Aligning moves the images that are ticked to move, never
/// the reference. With two images and the first as the reference this is the pair as it always
/// was; making the other one the reference swaps the two.
/// </summary>
public sealed class CompareSelection
{
    int _peekKey = -1, _peekActive, _peekOther;
    long _peekStarted;
    uint _shown, _moves; // bit i: image i

    public int Count { get; private set; }

    /// <summary>The image the user is at: what the single view shows, what the readouts and file actions refer to.</summary>
    public int Active { get; private set; }

    /// <summary>The image the active one is compared with. Differs from <see cref="Active"/> whenever there are two images.</summary>
    public int Other { get; private set; }

    /// <summary>The image the others are compared with and aligned to. It is always shown.</summary>
    public int Reference { get; private set; }

    /// <summary>How many images the views that show several at once take in.</summary>
    public int ShownCount => BitOperations.PopCount(_shown);

    /// <summary>Starts over with a set of <paramref name="count"/> images, at <paramref name="active"/>: all shown, the first the reference, all others moved by aligning.</summary>
    public void Reset(int count, int active = 0)
    {
        Count = Math.Max(0, count);
        Active = Count == 0 ? 0 : Math.Clamp(active, 0, Count - 1);
        Other = Count < 2 ? Active : Active == 0 ? 1 : 0;
        Reference = 0;
        _shown = _moves = All(Count);
        _peekKey = -1;
    }

    static uint All(int count) => count >= 32 ? uint.MaxValue : (1u << count) - 1;

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

    // ---------------------------------------------------------------- what the views show

    public bool IsShown(int index) => index >= 0 && index < Count && (_shown >> index & 1) != 0;

    /// <summary>Takes an image into the views or out of them. The reference stays in, and so does the last one left. False if nothing changed.</summary>
    public bool SetShown(int index, bool shown)
    {
        if (index < 0 || index >= Count || IsShown(index) == shown) return false;
        if (!shown && (index == Reference || ShownCount <= 1)) return false;
        _shown ^= 1u << index;
        return true;
    }

    /// <summary>Makes an image the reference; it is shown from then on. False if it already was.</summary>
    public bool SetReference(int index)
    {
        if (index < 0 || index >= Count || index == Reference) return false;
        Reference = index;
        _shown |= 1u << index;
        return true;
    }

    /// <summary>The next shown image after the reference, or with a negative <paramref name="delta"/> the one before it, becomes the reference. False if there is no other.</summary>
    public bool StepReference(int delta)
    {
        for (int k = 1; k < Count; k++)
        {
            int index = ((Reference + (delta < 0 ? -k : k)) % Count + Count) % Count;
            if (IsShown(index)) return SetReference(index);
        }
        return false;
    }

    /// <summary>
    /// The shown images in the order the views take them: the reference first, then on through
    /// the set and round its end. Returns how many there are; <paramref name="order"/> must have
    /// room for <see cref="Count"/>.
    /// </summary>
    public int Order(Span<int> order)
    {
        int n = 0;
        for (int k = 0; k < Count; k++)
        {
            int index = (Reference + k) % Count;
            if (IsShown(index)) order[n++] = index;
        }
        return n;
    }

    // ---------------------------------------------------------------- what aligning moves

    /// <summary>True if aligning moves this image: it is ticked to, and it is not the reference.</summary>
    public bool Moves(int index) => index >= 0 && index < Count && index != Reference && (_moves >> index & 1) != 0;

    /// <summary>Ticks an image to be moved by aligning, or takes the tick away. The reference has none to give. False if nothing changed.</summary>
    public bool SetMoves(int index, bool moves)
    {
        if (index < 0 || index >= Count || index == Reference || Moves(index) == moves) return false;
        _moves ^= 1u << index;
        return true;
    }

    // ---------------------------------------------------------------- peeking

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

    // ---------------------------------------------------------------- the set changes

    /// <summary>An image was appended to the set. It is shown, and moved by aligning.</summary>
    public void Added()
    {
        if (Count < 32)
        {
            _shown |= 1u << Count;
            _moves |= 1u << Count;
        }
        Count++;
        if (Count == 2) Other = 1 - Active;
    }

    /// <summary>The image at <paramref name="index"/> left the set; the ones after it moved up.</summary>
    public void Removed(int index)
    {
        if (index < 0 || index >= Count) return;
        Count--;
        _peekKey = -1;
        _shown = Close(_shown, index);
        _moves = Close(_moves, index);
        if (Count == 0)
        {
            Active = Other = Reference = 0;
            return;
        }
        Active = After(Active);
        Other = After(Other);
        if (Other == Active && Count > 1) Other = Active > 0 ? Active - 1 : 1;
        Reference = After(Reference);
        _shown |= 1u << Reference; // a reference that took the removed one's place is shown, as a reference is

        // An image after the removed one moves up; the removed one's place is taken by its successor, or by the new last one.
        int After(int i) => i > index ? i - 1 : Math.Min(i, Count - 1);

        // The bit of the removed image goes, and those above it move down one.
        static uint Close(uint bits, int at)
        {
            uint below = (1u << at) - 1;
            return (bits & below) | ((bits >> 1) & ~below);
        }
    }
}
