using System;

namespace thuvu
{
    public class ListViewItemEventArgs : EventArgs
    {
        public int Item { get; set; }
        public object? Value { get; set; }

        public ListViewItemEventArgs()
        {
        }

        public ListViewItemEventArgs(int item, object? value)
        {
            Item = item;
            Value = value;
        }
    }
}
