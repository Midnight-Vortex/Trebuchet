using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tot_gui_lib
{
    public class LabelOrdering : IComparer<ITag>
    {
        public int Compare(ITag? x, ITag? y)
        {
            if (x == null || y == null) return 0;
            return String.Compare(x.Name, y.Name, StringComparison.Ordinal);
        }
    }
}