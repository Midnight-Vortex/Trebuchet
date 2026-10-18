using System;

namespace tot_gui_lib;

public interface IScrollController
{
    event EventHandler ScrollToEnd;
    event EventHandler ScrollToHome;
}