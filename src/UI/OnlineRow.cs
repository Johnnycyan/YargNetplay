using System;
using YARG.Menu.Navigation;

namespace YargNetplay.UI
{
    /// <summary>A list row on the Online page: YARG's profile row visuals with our own selection/confirm behavior.</summary>
    internal sealed class OnlineRow : NavigatableBehaviour
    {
        public Action WhenSelected;
        public Action WhenConfirmed;

        protected override void OnSelectionChanged(bool selected)
        {
            base.OnSelectionChanged(selected);
            if (selected) WhenSelected?.Invoke();
        }

        public override void Confirm() => WhenConfirmed?.Invoke();
    }
}
