using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tot_gui_lib
{
    public class TinyRequestBase<T>(object? sender, Action<T> callback) : ITinyMessage
    {
        public object? Sender { get; } = sender;
        protected Action<T> Callback { get; } = callback;

        public void Respond(T response)
        {
            Callback?.Invoke(response);
        }
    }
}