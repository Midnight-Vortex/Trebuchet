using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tot_gui_lib
{
    public interface ITinyReturn<T> : ITinyMessage
    {
        T? ResponseValue { get; }
        void Respond(T response);
    }

    public class TinyReturn<T> : ITinyReturn<T>
    {
        public TinyReturn(object? sender)
        {
            Sender = sender;
        }

        public T? ResponseValue { get; set; }

        public object? Sender { get; }

        public void Respond(T response)
        {
            ResponseValue = response;
        }
    }
}