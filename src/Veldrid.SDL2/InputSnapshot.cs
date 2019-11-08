using System.Collections.Generic;
using System.Numerics;

namespace Veldrid
{
    public interface InputSnapshot
    {
        IReadOnlyCollection<KeyEvent> KeyEvents { get; }
        IReadOnlyCollection<MouseEvent> MouseEvents { get; }
        IReadOnlyCollection<char> KeyCharPresses { get; }
        bool IsMouseDown(MouseButton button);
        Vector2 MousePosition { get; }
        float WheelDelta { get; }
    }
}
